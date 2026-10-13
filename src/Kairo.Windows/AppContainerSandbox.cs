using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Kairo.Core.Tools;
using Microsoft.Win32.SafeHandles;

namespace Kairo.Windows;

/// <summary>What the sandbox may touch. Everything else on the machine is closed to the commands it runs.</summary>
/// <param name="Writable">Folders commands may read and change (the project, a swarm worker's worktree).</param>
/// <param name="Readable">Folders commands may only read (a swarm's main repository, toolchains outside Program Files).</param>
/// <param name="Network">False: no network at all. True: outgoing internet connections.</param>
/// <param name="MemoryLimitMb">Memory of everything a command starts, taken together.</param>
public sealed record SandboxOptions(
    IReadOnlyList<string> Writable,
    IReadOnlyList<string>? Readable = null,
    bool Network = false,
    int MemoryLimitMb = 4096);

/// <summary>
/// Runs commands in a Windows AppContainer: a process with a low-privilege token that can only use files it was explicitly granted,
/// has no network unless asked, and lives in a job object that bounds its memory and ends with it. Windows and Program Files are
/// readable (so PowerShell, git and the .NET SDK run); the user's profile, other drives and the registry's user hive are not.
/// </summary>
public sealed partial class AppContainerSandbox : IProcessSandbox
{
    private const string ProfileName = "Kairo.Sandbox";
    private const int MaxOutputChars = 1_000_000;

    private static readonly object GrantLock = new();
    private static readonly HashSet<string> Grants = new(StringComparer.OrdinalIgnoreCase);
    private static string? _sid;

    private readonly SandboxOptions _options;
    private readonly string _home;

    public AppContainerSandbox(SandboxOptions options)
    {
        _options = options;
        _home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kairo", "sandbox", "home");
        Directory.CreateDirectory(_home);
        WritablePaths = [.. options.Writable.Select(Path.GetFullPath), _home];
        EnsureGrants();
    }

    public IReadOnlyList<string> WritablePaths { get; }

    /// <summary>True where AppContainers exist (Windows 8 and later). Everything Kairo supports qualifies.</summary>
    public static bool IsSupported => OperatingSystem.IsWindowsVersionAtLeast(10);

    // ---- Access for the container ----

    /// <summary>The container's SID as text, creating the profile on first use.</summary>
    public static string ContainerSid
    {
        get
        {
            lock (GrantLock)
                return _sid ??= ConvertToString(OpenOrCreateProfile());
        }
    }

    private void EnsureGrants()
    {
        foreach (var dir in WritablePaths)
            Grant(dir, "M");
        foreach (var dir in _options.Readable ?? [])
            Grant(Path.GetFullPath(dir), "RX");
    }

    private static void Grant(string directory, string rights)
    {
        if (!Directory.Exists(directory))
            return;
        var key = rights + "|" + directory;
        lock (GrantLock)
        {
            if (Grants.Contains(key))
                return;
            RunIcacls(directory, "/grant", $"*{ContainerSid}:(OI)(CI){rights}");
            Grants.Add(key);
        }
    }

    /// <summary>Takes back every grant this process made. Call when the app closes so project folders do not keep the entry.</summary>
    public static void RevokeAll()
    {
        lock (GrantLock)
        {
            foreach (var directory in Grants.Select(g => g[(g.IndexOf('|') + 1)..]).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    RunIcacls(directory, "/remove", "*" + ContainerSid);
                }
                catch (Win32Exception)
                {
                    // The folder is gone or locked; the entry is harmless without the container.
                }
            }
            Grants.Clear();
        }
    }

    private static void RunIcacls(string directory, string verb, string argument)
    {
        var psi = new ProcessStartInfo("icacls.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add(directory);
        psi.ArgumentList.Add(verb);
        psi.ArgumentList.Add(argument);
        psi.ArgumentList.Add("/Q");
        using var process = Process.Start(psi) ?? throw new Win32Exception("icacls did not start.");
        process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new Win32Exception($"icacls {verb} on '{directory}' failed: {error.Trim()}");
    }

    // ---- Running ----

    public async Task<ProcessOutcome> RunAsync(ProcessLaunch launch, CancellationToken ct)
    {
        var (read, write) = CreateInheritablePipe();
        var (inRead, inWrite) = CreateInheritablePipe();
        nint job = 0, process = 0, thread = 0;
        try
        {
            job = CreateJob(_options.MemoryLimitMb);
            (process, thread) = Start(launch, inRead, write, read, inWrite);

            // The child owns its ends now; ours must go so the reader sees the end of the stream.
            CloseHandle(write);
            write = 0;
            CloseHandle(inRead);
            inRead = 0;
            CloseHandle(inWrite);
            inWrite = 0;

            if (!AssignProcessToJobObject(job, process))
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            if (ResumeThread(thread) == uint.MaxValue)
                throw new Win32Exception(Marshal.GetLastPInvokeError());

            var output = new StringBuilder();
            using var stream = new FileStream(new SafeFileHandle(read, ownsHandle: true), FileAccess.Read, 4096, isAsync: false);
            read = 0;
            var reader = Task.Run(() => Pump(stream, output), CancellationToken.None);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(launch.TimeoutMs);
            var timedOut = false;
            try
            {
                await WaitAsync(process, timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                TerminateJobObject(job, 1);
                if (ct.IsCancellationRequested)
                    throw;
                timedOut = true;
            }

            // Whatever the command left running goes with it; then the output is complete.
            TerminateJobObject(job, 1);
            await Task.WhenAny(reader, Task.Delay(2_000, CancellationToken.None)).ConfigureAwait(false);
            _ = GetExitCodeProcess(process, out var code);

            string text;
            lock (output)
                text = output.ToString();
            return new ProcessOutcome(timedOut ? -1 : unchecked((int)code), text, timedOut);
        }
        finally
        {
            foreach (var handle in new[] { read, write, inRead, inWrite, thread, process, job })
            {
                if (handle != 0)
                    CloseHandle(handle);
            }
        }
    }

    private static void Pump(Stream stream, StringBuilder output)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var buffer = new char[4096];
        int count;
        try
        {
            while ((count = reader.Read(buffer, 0, buffer.Length)) > 0)
            {
                lock (output)
                {
                    if (output.Length < MaxOutputChars)
                        output.Append(buffer, 0, Math.Min(count, MaxOutputChars - output.Length));
                }
            }
        }
        catch (IOException)
        {
            // The pipe broke because the job was terminated.
        }
    }

    private unsafe (nint Process, nint Thread) Start(ProcessLaunch launch, nint stdin, nint stdout, nint ourRead, nint ourWrite)
    {
        var sid = OpenOrCreateProfile();
        var network = _options.Network ? ConvertFromString("S-1-15-3-1") : 0; // internetClient
        var capabilities = stackalloc SidAndAttributes[1];
        capabilities[0] = new SidAndAttributes { Sid = network, Attributes = 0x4 /* SE_GROUP_ENABLED */ };
        var security = new SecurityCapabilities
        {
            AppContainerSid = sid,
            Capabilities = network != 0 ? capabilities : null,
            CapabilityCount = network != 0 ? 1u : 0u,
        };

        // Only the three pipe ends the child needs are inherited; the rest of our handles stay ours.
        var inherit = stackalloc nint[] { stdin, stdout };
        nuint size = 0;
        InitializeProcThreadAttributeList(null, 2, 0, ref size);
        var attributes = (byte*)NativeMemory.Alloc(size);
        try
        {
            if (!InitializeProcThreadAttributeList(attributes, 2, 0, ref size))
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            if (!UpdateProcThreadAttribute(attributes, 0, 0x20009 /* SECURITY_CAPABILITIES */, (nint)(&security), (nuint)sizeof(SecurityCapabilities), 0, 0))
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            if (!UpdateProcThreadAttribute(attributes, 0, 0x20002 /* HANDLE_LIST */, (nint)inherit, (nuint)(2 * sizeof(nint)), 0, 0))
                throw new Win32Exception(Marshal.GetLastPInvokeError());

            var startup = new StartupInfoEx
            {
                StartupInfo = { cb = sizeof(StartupInfoEx), dwFlags = 0x100 /* USESTDHANDLES */, hStdInput = stdin, hStdOutput = stdout, hStdError = stdout },
                lpAttributeList = attributes,
            };
            var commandLine = (BuildCommandLine(launch.FileName, launch.Arguments) + "\0").ToCharArray();
            var environment = Marshal.StringToHGlobalUni(BuildEnvironment());
            try
            {
                ProcessInformation info;
                bool ok;
                fixed (char* line = commandLine)
                {
                    ok = CreateProcess(launch.FileName, line, 0, 0, true,
                        0x80000 /* EXTENDED_STARTUPINFO_PRESENT */ | 0x4 /* SUSPENDED */ | 0x400 /* UNICODE_ENVIRONMENT */ | 0x08000000 /* NO_WINDOW */,
                        environment, launch.WorkingDirectory, &startup, &info);
                }
                if (!ok)
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), $"The sandbox could not start '{launch.FileName}' in '{launch.WorkingDirectory}'.");
                return (info.hProcess, info.hThread);
            }
            finally
            {
                Marshal.FreeHGlobal(environment);
            }
        }
        finally
        {
            DeleteProcThreadAttributeList(attributes);
            NativeMemory.Free(attributes);
            if (network != 0)
                LocalFree(network);
        }
    }

    /// <summary>The container gets a home inside the sandbox folder, so tools that write caches and settings have somewhere allowed.</summary>
    private string BuildEnvironment()
    {
        var variables = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
            variables[(string)entry.Key] = (string?)entry.Value ?? "";

        var temp = Path.Combine(_home, "temp");
        var appData = Path.Combine(_home, "AppData");
        var local = Path.Combine(_home, "LocalAppData");
        foreach (var dir in new[] { temp, appData, local })
            Directory.CreateDirectory(dir);
        variables["TEMP"] = variables["TMP"] = temp;
        variables["USERPROFILE"] = variables["HOME"] = _home;
        variables["APPDATA"] = appData;
        variables["LOCALAPPDATA"] = local;
        variables["DOTNET_CLI_HOME"] = _home;
        variables["DOTNET_NOLOGO"] = "1";
        variables["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";

        var sb = new StringBuilder();
        foreach (var (name, value) in variables)
            sb.Append(name).Append('=').Append(value).Append('\0');
        return sb.Append('\0').ToString();
    }

    private static string BuildCommandLine(string fileName, IReadOnlyList<string> arguments) =>
        string.Join(' ', new[] { fileName }.Concat(arguments).Select(Quote));

    /// <summary>Quotes one argument the way CommandLineToArgvW reads it back.</summary>
    internal static string Quote(string argument)
    {
        if (argument.Length > 0 && argument.IndexOfAny([' ', '\t', '"']) < 0)
            return argument;

        var sb = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var c in argument)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }
            if (c == '"')
                sb.Append('\\', backslashes * 2 + 1).Append('"');
            else
                sb.Append('\\', backslashes).Append(c);
            backslashes = 0;
        }
        return sb.Append('\\', backslashes * 2).Append('"').ToString();
    }

    private static Task WaitAsync(nint process, CancellationToken ct)
    {
        var done = new TaskCompletionSource();
        var handle = new ManualResetEvent(false) { SafeWaitHandle = new SafeWaitHandle(process, ownsHandle: false) };
        var registered = ThreadPool.RegisterWaitForSingleObject(handle, (_, _) => done.TrySetResult(), null, Timeout.Infinite, executeOnlyOnce: true);
        var cancel = ct.Register(() => done.TrySetCanceled(ct));
        return done.Task.ContinueWith(t =>
        {
            registered.Unregister(null);
            cancel.Dispose();
            handle.Dispose();
            t.GetAwaiter().GetResult();
        }, TaskScheduler.Default);
    }

    // ---- Profile, SIDs, pipes, job ----

    private static unsafe nint OpenOrCreateProfile()
    {
        var hr = CreateAppContainerProfile(ProfileName, "Kairo", "Commands run by the Kairo agent", null, 0, out var sid);
        if (hr == 0)
            return sid;
        if (hr != unchecked((int)0x800700B7)) // ERROR_ALREADY_EXISTS
            throw new Win32Exception(hr & 0xFFFF, "The AppContainer profile could not be created.");
        hr = DeriveAppContainerSidFromAppContainerName(ProfileName, out sid);
        if (hr != 0)
            throw new Win32Exception(hr & 0xFFFF, "The AppContainer SID could not be read.");
        return sid;
    }

    private static string ConvertToString(nint sid)
    {
        if (!ConvertSidToStringSid(sid, out var text))
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        try
        {
            return Marshal.PtrToStringUni(text)!;
        }
        finally
        {
            LocalFree(text);
        }
    }

    private static nint ConvertFromString(string sid)
    {
        if (!ConvertStringSidToSid(sid, out var result))
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        return result;
    }

    private static unsafe (nint Read, nint Write) CreateInheritablePipe()
    {
        var attributes = new SecurityAttributes { nLength = sizeof(SecurityAttributes), bInheritHandle = 1 };
        if (!CreatePipe(out var read, out var write, &attributes, 0))
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        return (read, write);
    }

    private static unsafe nint CreateJob(int memoryMb)
    {
        var job = CreateJobObject(0, null);
        if (job == 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        var limits = new JobObjectExtendedLimits
        {
            Basic = { LimitFlags = 0x2000 /* KILL_ON_JOB_CLOSE */ | 0x200 /* JOB_MEMORY */ },
            JobMemoryLimit = (nuint)Math.Max(64, memoryMb) * 1024 * 1024,
        };
        if (!SetInformationJobObject(job, 9, &limits, (uint)sizeof(JobObjectExtendedLimits)))
        {
            var error = Marshal.GetLastPInvokeError();
            CloseHandle(job);
            throw new Win32Exception(error);
        }
        return job;
    }

    // ---- Win32 ----

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int nLength;
        public nint lpSecurityDescriptor;
        public int bInheritHandle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SidAndAttributes
    {
        public nint Sid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct SecurityCapabilities
    {
        public nint AppContainerSid;
        public SidAndAttributes* Capabilities;
        public uint CapabilityCount;
        public uint Reserved;
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
    private unsafe struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public byte* lpAttributeList;
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

    [LibraryImport("userenv.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static unsafe partial int CreateAppContainerProfile(string name, string displayName, string description, SidAndAttributes* capabilities, uint count, out nint sid);

    [LibraryImport("userenv.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int DeriveAppContainerSidFromAppContainerName(string name, out nint sid);

    [LibraryImport("advapi32.dll", EntryPoint = "ConvertSidToStringSidW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ConvertSidToStringSid(nint sid, out nint text);

    [LibraryImport("advapi32.dll", EntryPoint = "ConvertStringSidToSidW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ConvertStringSidToSid(string text, out nint sid);

    [LibraryImport("kernel32.dll")]
    private static partial nint LocalFree(nint memory);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool CreatePipe(out nint read, out nint write, SecurityAttributes* attributes, uint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool InitializeProcThreadAttributeList(byte* list, int count, int flags, ref nuint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool UpdateProcThreadAttribute(byte* list, uint flags, nint attribute, nint value, nuint size, nint previous, nint returnSize);

    [LibraryImport("kernel32.dll")]
    private static unsafe partial void DeleteProcThreadAttributeList(byte* list);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateProcessW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool CreateProcess(string? application, char* commandLine, nint processAttributes, nint threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint flags, nint environment, string? currentDirectory, StartupInfoEx* startup, ProcessInformation* process);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint ResumeThread(nint thread);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateJobObject(nint attributes, string? name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool SetInformationJobObject(nint job, int infoClass, void* info, uint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(nint job, nint process);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TerminateJobObject(nint job, uint exitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetExitCodeProcess(nint process, out uint code);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
