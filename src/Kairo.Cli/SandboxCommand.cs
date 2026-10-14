using System.Diagnostics;
using System.Text.Json;
using Kairo.Core.Config;
using Kairo.Core.Tools;
using Kairo.Tools;
using Kairo.Windows;

namespace Kairo.Cli;

/// <summary>
/// <c>kairo sandbox check</c>: which commands start inside the sandbox on this machine, and for the ones that do not, which folder to
/// add under <c>[sandbox] read</c>. Swarm workers run in the sandbox by default, so this is how a user finds out what a worker can use.
/// </summary>
internal static class SandboxCommand
{
    private sealed record Probe(string Name, string Command, string Executable, string Note);

    private static readonly Probe[] Probes =
    [
        new("powershell", "'ok'", "powershell.exe", "the shell itself"),
        new("dotnet", "dotnet --version", "dotnet.exe", ".NET SDK"),
        new("node", "node --version", "node.exe", "Node.js"),
        new("npm", "npm --version", "npm.cmd", "npm"),
        new("python", "python --version", "python.exe", "Python"),
        new("pip", "pip --version", "pip.exe", "pip"),
        new("cargo", "cargo --version", "cargo.exe", "Rust"),
        new("go", "go version", "go.exe", "Go"),
        new("git", "git --version", "git.exe", "git (does not start in the sandbox on Windows; workers have the read-only Git tool)"),
    ];

    public static async Task<int> RunAsync(CliOptions options, string userConfigDir)
    {
        if (options.Positional.FirstOrDefault() != "check")
            return Fail("Usage: kairo sandbox check");
        if (!AppContainerSandbox.IsSupported)
            return Fail("The sandbox needs Windows 10 or later.");

        KairoConfig config;
        try
        {
            config = KairoConfig.Load(userConfigDir, userConfigDir, includeProject: false);
        }
        catch (FormatException ex)
        {
            return Fail(ex.Message);
        }

        var work = Directory.CreateTempSubdirectory("kairo-sandbox-check-").FullName;
        ConsoleUi.Write(ConsoleColor.Magenta, "Kairo · sandbox check");
        ConsoleUi.Write(ConsoleColor.DarkGray, $"  setting shell = {config.SandboxShell ?? "workers (default)"} · network {(config.SandboxNetwork == true ? "on" : "off")} · {config.SandboxRead.Count} extra read folder(s)\n\n");

        try
        {
            var sandbox = new SandboxPolicy(config, new AppContainerSandboxProvider()).For(
                Kairo.Core.Permissions.PermissionMode.Default, swarmWorker: true, [work])
                ?? new AppContainerSandboxProvider().Create(new SandboxSpec([work], [.. config.SandboxRead.Select(Path.GetFullPath)], config.SandboxNetwork ?? false, config.SandboxMemoryMb ?? SandboxPolicy.DefaultMemoryMb));
            var shell = new ShellTool();
            var ctx = new ToolContext(work) { Sandbox = sandbox };
            var suggestions = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            var adminFolders = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var probe in Probes)
            {
                var onHost = Locate(probe.Executable);
                var clock = Stopwatch.StartNew();
                var result = await shell.InvokeAsync(JsonSerializer.SerializeToElement(new { command = probe.Command, timeout = 30_000 }), ctx, default);
                var ran = !result.IsError;
                var detail = result.Content.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "";

                if (ran)
                {
                    ConsoleUi.Write(ConsoleColor.Green, $"  ok        {probe.Name,-11}");
                    ConsoleUi.Write(ConsoleColor.Gray, $"{detail}  ({clock.ElapsedMilliseconds} ms)\n");
                }
                else if (onHost is null)
                {
                    ConsoleUi.Write(ConsoleColor.DarkGray, $"  absent    {probe.Name,-11}not installed on this PC\n");
                }
                else if (probe.Name == "git")
                {
                    ConsoleUi.Write(ConsoleColor.Yellow, $"  blocked   {probe.Name,-11}{probe.Note}\n");
                }
                else
                {
                    var folder = Path.GetDirectoryName(onHost);
                    ConsoleUi.Write(ConsoleColor.Yellow, $"  blocked   {probe.Name,-11}found at {onHost}, but it did not run inside the sandbox\n");
                    ConsoleUi.Write(ConsoleColor.DarkGray, $"            {Reason(result.Content)}\n");
                    // A folder in Program Files belongs to the administrators: only they can change who may read it.
                    if (folder is not null)
                        (InProgramFiles(folder) ? adminFolders : suggestions).Add(folder);
                }
            }

            Console.WriteLine();
            if (suggestions.Count > 0)
            {
                ConsoleUi.Write(ConsoleColor.Cyan, "Add these folders (Settings → Песочница → «Папки, которые команды могут читать», or in config.toml) to let workers use those tools:\n");
                ConsoleUi.Write(ConsoleColor.Gray, "[sandbox]\nread = [" + string.Join(", ", suggestions.Select(s => $"\"{s.Replace('\\', '/')}\"")) + "]\n");
                ConsoleUi.Write(ConsoleColor.DarkGray, "They are opened for reading only. Check that a folder holds no secrets before adding it.\n");
            }
            if (adminFolders.Count > 0)
            {
                ConsoleUi.Write(ConsoleColor.Cyan, "These tools live in Program Files, where the sandbox has no read access. Once, in a PowerShell run as administrator (it gives the folder the read right most Program Files folders already have):\n");
                foreach (var folder in adminFolders)
                    ConsoleUi.Write(ConsoleColor.Gray, $"icacls \"{folder}\" /grant \"*S-1-15-2-1:(OI)(CI)RX\"\n");
                Console.WriteLine();
            }
            if (config.SandboxNetwork != true)
                ConsoleUi.Write(ConsoleColor.DarkGray, "The network is off, so installs and downloads fail inside the sandbox ([sandbox] network = true allows it).\n");
            return 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Fail($"The sandbox could not be made: {ex.Message}");
        }
        finally
        {
            AppContainerSandbox.RevokeAll();
            try { Directory.Delete(work, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static string Reason(string output)
    {
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(l => !l.StartsWith("Exit code", StringComparison.Ordinal)).Take(2);
        var text = string.Join(" ", lines);
        return text.Length > 170 ? text[..170] + "…" : text;
    }

    /// <summary>Where Windows would find the program: the first match on PATH.</summary>
    private static string? Locate(string executable)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim().Trim('"'), executable);
                if (File.Exists(candidate))
                    return candidate;
            }
            catch (ArgumentException)
            {
            }
        }
        return null;
    }

    private static bool InProgramFiles(string folder) =>
        new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) }
            .Any(root => !string.IsNullOrEmpty(root) && folder.StartsWith(root, StringComparison.OrdinalIgnoreCase));

    private static int Fail(string message)
    {
        ConsoleUi.Write(ConsoleColor.Red, message + "\n");
        return 1;
    }
}
