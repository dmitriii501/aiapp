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
        var script = "[Console]::OutputEncoding = [System.Text.Encoding]::UTF8; $ProgressPreference = 'SilentlyContinue'\n" + command;
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
        foreach (var arg in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand",
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
