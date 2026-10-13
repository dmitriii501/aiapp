using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Kairo.Core.Models;
using Kairo.Core.Tools;

namespace Kairo.Tools;

/// <summary>
/// Runs a PowerShell command in the working directory and returns its combined output.
/// M1 uses plain pipes; ConPTY (shared with the visible terminal) and the AppContainer sandbox come later.
/// </summary>
public sealed class ShellTool : ITool
{
    private const int DefaultTimeoutMs = 120_000;
    private const int MaxTimeoutMs = 600_000;

    private static readonly Lazy<string> PowerShell = new(FindPowerShell);

    public ToolSpec Spec { get; } = new(
        "Shell",
        "Runs a PowerShell command in the working directory, e.g. to build, run tests or use git. Returns stdout and " +
        "stderr. Do not use it to read, search or edit files — use Read, Grep, Glob and Edit instead. " +
        "Commands are non-interactive; avoid anything that waits for input.",
        ToolInput.Schema(
            ("command", "string", "The PowerShell command to run.", true),
            ("timeout_ms", "integer", $"Timeout in milliseconds (default {DefaultTimeoutMs}, max {MaxTimeoutMs}).", false)));

    public ToolAssessment Assess(JsonElement input, ToolContext ctx)
    {
        var command = ToolInput.RequiredString(input, "command");
        if (string.IsNullOrWhiteSpace(command))
            throw new ToolInputException("command is empty.");
        return new(RiskLevel.Exec, command, $"Shell({(command.Length > 120 ? command[..120] + "…" : command)})");
    }

    public async Task<ToolResult> InvokeAsync(JsonElement input, ToolContext ctx, CancellationToken ct)
    {
        var command = ToolInput.RequiredString(input, "command");
        var timeout = Math.Clamp(ToolInput.OptionalInt(input, "timeout_ms") ?? DefaultTimeoutMs, 1_000, MaxTimeoutMs);

        // UTF-8 output so Cyrillic and other non-ASCII text survives; -EncodedCommand avoids quoting issues.
        var script = WrapScript(command, ctx.Sandbox is null ? "" : SandboxPreamble(ctx.WorkingDirectory));
        if (ctx.Sandbox is { } sandbox)
            return await RunSandboxedAsync(sandbox, script, ctx.WorkingDirectory, timeout, ct).ConfigureAwait(false);

        var psi = new ProcessStartInfo(PowerShell.Value)
        {
            WorkingDirectory = ctx.WorkingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-OutputFormat", "Text", "-ExecutionPolicy", "Bypass", "-EncodedCommand",
                     Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) })
            psi.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = psi };
        var output = new StringBuilder();
        var gate = new Lock();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (gate) output.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (gate) output.AppendLine(e.Data); };

        process.Start();
        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            process.WaitForExit(); // flush async output readers
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            if (ct.IsCancellationRequested)
                throw;
            string partial;
            lock (gate) partial = output.ToString();
            return ToolResult.Error(TextFiles.Truncate($"Command timed out after {timeout} ms and was stopped.\n{partial}"));
        }

        string text;
        lock (gate) text = output.ToString().TrimEnd();
        var body = text.Length == 0 ? "(no output)" : text;
        return process.ExitCode == 0
            ? new ToolResult(TextFiles.Truncate(body))
            : ToolResult.Error(TextFiles.Truncate($"Exit code {process.ExitCode}\n{body}"));
    }

    /// <summary>
    /// Windows PowerShell 5.1 prints error records as CLIXML (an XML dump) when -EncodedCommand runs with redirected output. The script
    /// merges every stream and turns it into plain text itself, so errors come back readable and in order with the other output; the
    /// exit code is the one the command would have given on its own (a native command's code, 1 for a failed cmdlet or a throw).
    /// </summary>
    internal static string WrapScript(string command, string preamble = "") =>
        // The header shares the first line with the command so line numbers in PowerShell's messages stay the command's own.
        "[Console]::OutputEncoding = [System.Text.Encoding]::UTF8; $ProgressPreference = 'SilentlyContinue'; " + preamble + "$kairoOk = $true; & { try { " + command +
        "\n$script:kairoOk = $?\n} catch { $_; $script:kairoOk = $false } } *>&1 | Out-String -Stream\n" +
        "if (-not $kairoOk) { if ($LASTEXITCODE) { exit $LASTEXITCODE } else { exit 1 } }";

    /// <summary>
    /// The sandboxed process cannot read the parents of the project folder, and Set-Location refuses a folder whose parents it cannot
    /// check; a drive rooted at the folder is the way around it. The process's own current directory is already right.
    /// </summary>
    private static string SandboxPreamble(string workingDirectory) =>
        $"New-PSDrive -Name W -PSProvider FileSystem -Root '{workingDirectory.Replace("'", "''")}' | Out-Null; Set-Location W:\\; ";

    private static async Task<ToolResult> RunSandboxedAsync(IProcessSandbox sandbox, string script, string workingDirectory, int timeout, CancellationToken ct)
    {
        string[] args = ["-NoLogo", "-NoProfile", "-NonInteractive", "-OutputFormat", "Text", "-ExecutionPolicy", "Bypass", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script))];
        ProcessOutcome outcome;
        try
        {
            outcome = await sandbox.RunAsync(new ProcessLaunch(SandboxPowerShell(), args, workingDirectory, timeout), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Fail closed: when the sandbox cannot start, the command does not run outside it.
            return ToolResult.Error($"The sandbox could not run the command, so it was not run: {ex.Message}");
        }

        if (outcome.TimedOut)
            return ToolResult.Error(TextFiles.Truncate($"Command timed out after {timeout} ms and was stopped.\n{outcome.Output}"));
        var text = outcome.Output.TrimEnd();
        // Git for Windows opens the NUL device when it starts, and a Windows AppContainer may not; say so instead of leaving the model guessing.
        if (outcome.ExitCode != 0 && text.Contains("/dev/null", StringComparison.Ordinal) && text.Contains("Permission denied", StringComparison.Ordinal))
            text += "\n(git cannot start inside the sandbox: Windows denies it the NUL device. Do not retry; use Read, Grep and Glob to look at the code, or ask the user to run the git command.)";
        var body = text.Length == 0 ? "(no output)" : text;
        return outcome.ExitCode == 0
            ? new ToolResult(TextFiles.Truncate(body))
            : ToolResult.Error(TextFiles.Truncate($"Exit code {outcome.ExitCode}\n{body}"));
    }

    /// <summary>The sandbox reads Windows and Program Files only: PowerShell 7 counts when it lives in one of them, else Windows PowerShell.</summary>
    private static string SandboxPowerShell()
    {
        var found = PowerShell.Value;
        var allowed = new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.Windows) };
        if (Path.IsPathRooted(found) && allowed.Any(root => found.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            return found;
        return Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
    }

    private static string FindPowerShell()
    {
        // Prefer PowerShell 7 when installed; Windows PowerShell 5.1 ships with every supported Windows.
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            var pwsh = Path.Combine(dir.Trim(), "pwsh.exe");
            if (File.Exists(pwsh))
                return pwsh;
        }
        return "powershell.exe";
    }
}
