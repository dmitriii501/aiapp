using System.Runtime.InteropServices;

namespace Kairo.PcControl;

/// <summary>
/// When the user lets Kairo drive every program, <c>PcLaunch</c> must not become a way to run any executable on the PATH (python, curl,
/// ping…): only programs that are windowed applications, found by their bare name, may be started that way.
/// </summary>
internal static unsafe partial class LaunchCheck
{
    private const int GuiSubsystem = 2;

    /// <summary>The reason <paramref name="name"/> may not be started by name, or null when it is a windowed program.</summary>
    public static string? Refusal(string name)
    {
        var buffer = stackalloc char[520];
        var length = SearchPath(null, name, null, 520, buffer, 0);
        if (length == 0 || length >= 520)
            return $"{name} is not a program Kairo can find by name. Ask the user to open it, or to list it under [pc] apps.";
        var path = new string(buffer, 0, (int)length);
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

    [LibraryImport("kernel32.dll", EntryPoint = "SearchPathW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint SearchPath(string? path, string fileName, string? extension, uint bufferLength, char* buffer, nint filePart);
}
