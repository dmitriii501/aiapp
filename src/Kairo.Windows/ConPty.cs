using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Kairo.Core.Terminal;
using Microsoft.Win32.SafeHandles;

namespace Kairo.Windows;

/// <summary>Starts processes inside a Windows pseudo-console (ConPTY, Windows 10 1809+).</summary>
[SupportedOSPlatform("windows")]
public sealed class ConPtyFactory : IPtyFactory
{
    public IPty Start(PtyStartInfo info, Action<ReadOnlyMemory<byte>> onOutput, Action<int> onExit) =>
        new ConPty(info, onOutput, onExit);
}

/// <summary>
/// One shell in a pseudo-console. The process is placed in a job object that kills the whole process tree
/// when the job closes, so shells and whatever they started never outlive the terminal or a crashed Kairo.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed unsafe partial class ConPty : IPty
{
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const nint ProcThreadAttributePseudoConsole = 0x00020016;
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;
    private const uint Infinite = 0xFFFFFFFF;
    private const int UseStdHandles = 0x00000100;

    private readonly Lock _gate = new();
    private readonly FileStream _input;
    private readonly nint _console;
    private readonly nint _process;
    private readonly nint _job;
    private int _consoleClosed;
    private long _lastOutputTick = Environment.TickCount64;
    private bool _disposed;

    public ConPty(PtyStartInfo info, Action<ReadOnlyMemory<byte>> onOutput, Action<int> onExit)
    {
        // ConPTY reads our keystrokes from one pipe and writes its output to another.
        CreatePipeOrThrow(out var inputRead, out var inputWrite);
        CreatePipeOrThrow(out var outputRead, out var outputWrite);

        var hr = CreatePseudoConsole(Size(info.Columns, info.Rows), inputRead, outputWrite, 0, out _console);
        // The console duplicated the ends it needs; ours are no longer used.
        CloseHandle(inputRead);
        CloseHandle(outputWrite);
        if (hr != 0)
        {
            CloseHandle(inputWrite);
            CloseHandle(outputRead);
            throw new Win32Exception(hr, "Не удалось создать псевдоконсоль.");
        }

        _input = new FileStream(new SafeFileHandle(inputWrite, ownsHandle: true), FileAccess.Write, 1, isAsync: false);
        var output = new FileStream(new SafeFileHandle(outputRead, ownsHandle: true), FileAccess.Read, 1, isAsync: false);

        try
        {
            _job = CreateKillOnCloseJob();
            _process = StartProcess(info);
            if (!AssignProcessToJobObject(_job, _process))
                throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
        catch
        {
            CloseConsole();
            output.Dispose();
            _input.Dispose();
            if (_process != 0) CloseHandle(_process);
            if (_job != 0) CloseHandle(_job);
            throw;
        }

        var reader = new Thread(() => ReadLoop(output, onOutput)) { IsBackground = true, Name = "Kairo terminal output" };
        reader.Start();

        new Thread(() =>
        {
            WaitForSingleObject(_process, Infinite);
            GetExitCodeProcess(_process, out var code);
            // ConPTY renders output asynchronously: closing right after the process ends can drop its last lines.
            // Give it a moment, then wait until the output has been quiet for a bit.
            Thread.Sleep(100);
            for (var waited = 0; waited < 400 && Environment.TickCount64 - Volatile.Read(ref _lastOutputTick) < 60; waited += 20)
                Thread.Sleep(20);
            // Closing the console flushes what is left and ends the reader.
            CloseConsole();
            reader.Join();
            output.Dispose();
            CloseHandle(_process);
            onExit(unchecked((int)code));
        }) { IsBackground = true, Name = "Kairo terminal exit" }.Start();
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            try
            {
                _input.Write(data);
                _input.Flush();
            }
            catch (IOException)
            {
                // The shell is gone; the exit notification follows.
            }
        }
    }

    public void Resize(int columns, int rows)
    {
        lock (_gate)
        {
            if (!_disposed && Volatile.Read(ref _consoleClosed) == 0)
                ResizePseudoConsole(_console, Size(columns, rows));
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
        }
        // Closing the job kills the shell and everything it started; the exit thread then cleans up.
        CloseHandle(_job);
        _input.Dispose();
    }

    private void ReadLoop(FileStream output, Action<ReadOnlyMemory<byte>> onOutput)
    {
        var buffer = new byte[16 * 1024];
        try
        {
            int read;
            while ((read = output.Read(buffer, 0, buffer.Length)) > 0)
            {
                Volatile.Write(ref _lastOutputTick, Environment.TickCount64);
                onOutput(buffer.AsMemory(0, read).ToArray());
            }
        }
        catch (IOException)
        {
            // The pipe closed with the console.
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void CloseConsole()
    {
        if (Interlocked.Exchange(ref _consoleClosed, 1) == 0)
            ClosePseudoConsole(_console);
    }

    private nint StartProcess(PtyStartInfo info)
    {
        nuint size = 0;
        InitializeProcThreadAttributeList(null, 1, 0, ref size);
        var attributes = (byte*)NativeMemory.Alloc(size);
        try
        {
            if (!InitializeProcThreadAttributeList(attributes, 1, 0, ref size))
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            if (!UpdateProcThreadAttribute(attributes, 0, ProcThreadAttributePseudoConsole, _console, (nuint)sizeof(nint), 0, 0))
                throw new Win32Exception(Marshal.GetLastPInvokeError());

            // Null standard handles: otherwise the shell inherits ours and writes past the pseudo-console.
            var startup = new StartupInfoEx
            {
                StartupInfo = new StartupInfo { cb = sizeof(StartupInfoEx), dwFlags = UseStdHandles },
                lpAttributeList = (nint)attributes,
            };
            var commandLine = (info.CommandLine + "\0").ToCharArray(); // CreateProcessW may modify the buffer
            ProcessInformation process;
            fixed (char* command = commandLine)
            {
                if (!CreateProcess(null, command, 0, 0, false, ExtendedStartupInfoPresent | CreateUnicodeEnvironment, 0, info.WorkingDirectory, &startup, &process))
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), $"Не удалось запустить «{info.CommandLine}».");
            }
            CloseHandle(process.hThread);
            return process.hProcess;
        }
        finally
        {
            DeleteProcThreadAttributeList(attributes);
            NativeMemory.Free(attributes);
        }
    }

    private static nint CreateKillOnCloseJob()
    {
        var job = CreateJobObject(0, null);
        if (job == 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError());

        var limits = new JobObjectExtendedLimits { Basic = { LimitFlags = JobObjectLimitKillOnJobClose } };
        if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, &limits, (uint)sizeof(JobObjectExtendedLimits)))
        {
            var error = Marshal.GetLastPInvokeError();
            CloseHandle(job);
            throw new Win32Exception(error);
        }
        return job;
    }

    private static void CreatePipeOrThrow(out nint read, out nint write)
    {
        if (!CreatePipe(out read, out write, 0, 0))
            throw new Win32Exception(Marshal.GetLastPInvokeError());
    }

    private static Coord Size(int columns, int rows) =>
        new() { X = (short)Math.Clamp(columns, 1, short.MaxValue), Y = (short)Math.Clamp(rows, 1, short.MaxValue) };

    // ---- Win32 ----

    [StructLayout(LayoutKind.Sequential)]
    private struct Coord
    {
        public short X;
        public short Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public int cb;
        public nint lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public nint lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public nint lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public nint hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimits
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimits
    {
        public JobObjectBasicLimits Basic;
        public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes;
        public nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreatePipe(out nint read, out nint write, nint attributes, uint size);

    [LibraryImport("kernel32.dll")]
    private static partial int CreatePseudoConsole(Coord size, nint input, nint output, uint flags, out nint console);

    [LibraryImport("kernel32.dll")]
    private static partial int ResizePseudoConsole(nint console, Coord size);

    [LibraryImport("kernel32.dll")]
    private static partial void ClosePseudoConsole(nint console);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool InitializeProcThreadAttributeList(byte* list, int count, int flags, ref nuint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UpdateProcThreadAttribute(byte* list, uint flags, nint attribute, nint value, nuint size, nint previous, nint returnSize);

    [LibraryImport("kernel32.dll")]
    private static partial void DeleteProcThreadAttributeList(byte* list);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateProcessW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateProcess(string? application, char* commandLine, nint processAttributes, nint threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint flags, nint environment, string? currentDirectory, StartupInfoEx* startup, ProcessInformation* process);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateJobObject(nint attributes, string? name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(nint job, int infoClass, void* info, uint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(nint job, nint process);

    [LibraryImport("kernel32.dll")]
    private static partial uint WaitForSingleObject(nint handle, uint milliseconds);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetExitCodeProcess(nint process, out uint code);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
