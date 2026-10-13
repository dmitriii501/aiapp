namespace Kairo.Mcp;

/// <summary>Finds an executable the way the shell would, so a typo gives a clear message instead of a cmd.exe error.</summary>
public static class CommandResolver
{
    public static bool Exists(string command, string? workingDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(command))
            return false;

        var extensions = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
            .Split(';', StringSplitOptions.RemoveEmptyEntries);

        bool Matches(string basePath) =>
            File.Exists(basePath) || (!Path.HasExtension(basePath) && extensions.Any(e => File.Exists(basePath + e)));

        if (command.Contains('\\') || command.Contains('/') || Path.IsPathRooted(command))
        {
            var full = Path.IsPathRooted(command) ? command : Path.Combine(workingDirectory ?? Environment.CurrentDirectory, command);
            return Matches(full);
        }

        var directories = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        return directories.Any(d => Matches(Path.Combine(d.Trim('"'), command)));
    }
}
