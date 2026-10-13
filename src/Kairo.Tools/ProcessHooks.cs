using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Kairo.Core.Hooks;

namespace Kairo.Tools;

/// <summary>Runs hooks as PowerShell commands in the working directory, with the event as JSON on standard input.</summary>
public sealed class ProcessHooks(IReadOnlyList<HookConfig> hooks) : IHooks
{
    public const int MaxOutputChars = 4000;

    public bool Has(HookEvent hookEvent) => hooks.Any(h => h.Event == hookEvent);

    public async Task<HookOutcome> RunAsync(HookInput input, CancellationToken ct)
    {
        var context = new StringBuilder();
        foreach (var hook in hooks.Where(h => h.Event == input.Event && h.Matches(input.ToolName)))
        {
            var (code, stdout, stderr) = await RunOneAsync(hook, input, ct).ConfigureAwait(false);
            if (code == 2)
                return new HookOutcome(true, Clip(stderr.Trim().Length > 0 ? stderr : stdout, "The hook blocked this without saying why."));
            if (code == 0 && stdout.Trim().Length > 0)
                context.AppendLine(Clip(stdout, ""));
        }
        return context.Length == 0 ? HookOutcome.Nothing : new HookOutcome(false, Context: context.ToString().Trim());
    }

    internal static byte[] BuildInput(HookInput input)
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("hook_event_name", input.Event.ToString());
            w.WriteString("cwd", input.WorkingDirectory);
            if (input.ToolName is not null)
                w.WriteString("tool_name", input.ToolName);
            if (input.ToolInput is { } toolInput)
            {
                w.WritePropertyName("tool_input");
                toolInput.WriteTo(w);
            }
            if (input.ToolResponse is not null)
                w.WriteString("tool_response", input.ToolResponse);
            if (input.Prompt is not null)
                w.WriteString("prompt", input.Prompt);
            w.WriteEndObject();
        }
        return buffer.ToArray();
    }

    private static async Task<(int Code, string Stdout, string Stderr)> RunOneAsync(HookConfig hook, HookInput input, CancellationToken ct)
    {
        var script = "[Console]::OutputEncoding = [System.Text.Encoding]::UTF8; [Console]::InputEncoding = [System.Text.Encoding]::UTF8; $ProgressPreference = 'SilentlyContinue'\n" + hook.Command;
        var psi = new ProcessStartInfo(PowerShellLocator.Path)
        {
            WorkingDirectory = input.WorkingDirectory,
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
        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return (-1, "", ex.Message);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(hook.TimeoutSeconds, 1, 600)));
        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.StandardInput.BaseStream.WriteAsync(BuildInput(input), timeout.Token).ConfigureAwait(false);
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            return (process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            if (ct.IsCancellationRequested)
                throw new OperationCanceledException(ct);
            return (-1, "", "timed out");
        }
    }

    private static string Clip(string text, string fallback)
    {
        text = text.Trim();
        if (text.Length == 0)
            return fallback;
        return text.Length <= MaxOutputChars ? text : text[..MaxOutputChars] + "…";
    }
}

internal static class PowerShellLocator
{
    private static readonly Lazy<string> Located = new(Find);

    public static string Path => Located.Value;

    private static string Find()
    {
        // Prefer PowerShell 7 when installed; Windows PowerShell 5.1 ships with every supported Windows.
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(System.IO.Path.PathSeparator))
        {
            var pwsh = System.IO.Path.Combine(dir.Trim(), "pwsh.exe");
            if (File.Exists(pwsh))
                return pwsh;
        }
        return "powershell.exe";
    }
}
