using System.Runtime.Versioning;

namespace Kairo.Windows;

/// <summary>Picks the shell for the built-in terminal: PowerShell 7 if installed, otherwise Windows PowerShell.</summary>
[SupportedOSPlatform("windows")]
public static class ShellLocator
{
    public sealed record Shell(string CommandLine, string Title);

    public static Shell Find()
    {
        var pwsh = FindOnPath("pwsh.exe")
            ?? Existing(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe"));
        if (pwsh is not null)
            return new Shell($"\"{pwsh}\" -NoLogo", "PowerShell 7");

        var windowsPowerShell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        return new Shell($"\"{windowsPowerShell}\" -NoLogo", "PowerShell");
    }

    private static string? Existing(string path) => File.Exists(path) ? path : null;

    private static string? FindOnPath(string fileName) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(d => Path.Combine(d.Trim('"'), fileName))
            .FirstOrDefault(File.Exists);
}
