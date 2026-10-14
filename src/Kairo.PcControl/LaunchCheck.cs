using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Kairo.PcControl;

/// <summary>
/// When the user lets Kairo drive every program, <c>PcLaunch</c> must not become a way to run any executable on the PATH (python, curl,
/// ping…): only programs that are windowed applications may be started that way. A program is found the way Windows finds it for the
/// user: on the PATH, under App Paths in the registry, or as a Start Menu shortcut (where most installed programs, Steam and
/// Telegram among them, are only to be found).
/// </summary>
internal static unsafe partial class LaunchCheck
{
    private const int GuiSubsystem = 2;

    private static readonly string[] DefaultStartMenus =
    [
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Microsoft", "Windows", "Start Menu", "Programs"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "Windows", "Start Menu", "Programs"),
    ];

    /// <summary>The full path of the program Windows would start for this bare name, or null when it cannot be found.</summary>
    public static string? Resolve(string name, IEnumerable<string>? startMenus = null)
    {
        var file = name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name : name + ".exe";
        return OnPath(file) ?? InAppPaths(file) ?? InStartMenu(file, startMenus ?? DefaultStartMenus);
    }

    /// <summary>The reason <paramref name="name"/> may not be started by name, or null when it is a windowed program.</summary>
    public static string? Refusal(string name, IEnumerable<string>? startMenus = null)
    {
        var path = Resolve(name, startMenus);
        if (path is null)
            return $"{name} is not a program Kairo can find by name (not on the PATH, not under App Paths, no Start Menu shortcut). Ask the user to open it, or to list it under [pc] apps.";
        try
        {
            using var file = File.OpenRead(path);
            var header = new byte[64];
            if (file.Read(header, 0, 64) < 64 || header[0] != 0x4D || header[1] != 0x5A)
                return $"{name} is not a Windows program.";
            var peOffset = BitConverter.ToInt32(header, 0x3C);
            file.Seek(peOffset, SeekOrigin.Begin);
            var pe = new byte[24 + 70];
            if (file.Read(pe, 0, pe.Length) < pe.Length || pe[0] != 0x50 || pe[1] != 0x45)
                return $"{name} is not a Windows program.";
            var subsystem = BitConverter.ToUInt16(pe, 24 + 68);
            return subsystem == GuiSubsystem
                ? null
                : $"{name} is a console program, not a windowed one: Kairo does not start those by PcLaunch (that is what the Shell tool and its sandbox are for).";
        }
        catch (IOException)
        {
            return $"{name} could not be inspected.";
        }
        catch (UnauthorizedAccessException)
        {
            return $"{name} could not be inspected.";
        }
    }

    private static string? OnPath(string file)
    {
        var buffer = stackalloc char[520];
        var length = SearchPath(null, file, null, 520, buffer, 0);
        return length == 0 || length >= 520 ? null : new string(buffer, 0, (int)length);
    }

    private static string? InAppPaths(string file)
    {
        foreach (var (hive, view) in new[] { (RegistryHive.LocalMachine, RegistryView.Registry64), (RegistryHive.LocalMachine, RegistryView.Registry32), (RegistryHive.CurrentUser, RegistryView.Default) })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var key = root.OpenSubKey($@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{file}");
                if (key?.GetValue(null) is string value && value.Trim('"') is { Length: > 0 } path && File.Exists(path))
                    return path;
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
            }
        }
        return null;
    }

    // A shortcut named like the program ("Steam.lnk" for steam.exe), else one that points at it.
    private static string? InStartMenu(string file, IEnumerable<string> startMenus)
    {
        var wanted = Path.GetFileNameWithoutExtension(file);
        var links = new List<string>();
        foreach (var root in startMenus.Where(Directory.Exists))
        {
            try
            {
                links.AddRange(Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories));
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
            }
        }

        foreach (var link in links.Where(l => string.Equals(Path.GetFileNameWithoutExtension(l), wanted, StringComparison.OrdinalIgnoreCase)))
        {
            if (TargetOf(link) is { } target && target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(target))
                return target;
        }
        foreach (var link in links)
        {
            if (TargetOf(link) is { } target && string.Equals(Path.GetFileName(target), file, StringComparison.OrdinalIgnoreCase) && File.Exists(target))
                return target;
        }
        return null;
    }

    /// <summary>Where a Start Menu shortcut points, read through the Windows scripting host (late bound: there is no managed API for it).</summary>
    internal static string? TargetOf(string linkPath)
    {
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null)
                return null;
            var shell = Activator.CreateInstance(shellType);
            try
            {
                var link = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, [linkPath]);
                try
                {
                    return link?.GetType().InvokeMember("TargetPath", BindingFlags.GetProperty, null, link, null) as string;
                }
                finally
                {
                    if (link is not null && Marshal.IsComObject(link))
                        Marshal.ReleaseComObject(link);
                }
            }
            finally
            {
                if (shell is not null && Marshal.IsComObject(shell))
                    Marshal.ReleaseComObject(shell);
            }
        }
        catch (Exception ex) when (ex is COMException or TargetInvocationException or InvalidComObjectException or MissingMethodException)
        {
            return null;
        }
    }

    [LibraryImport("kernel32.dll", EntryPoint = "SearchPathW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint SearchPath(string? path, string fileName, string? extension, uint bufferLength, char* buffer, nint filePart);
}
