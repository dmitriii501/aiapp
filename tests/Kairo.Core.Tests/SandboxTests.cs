using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Kairo.Core.Tools;
using Kairo.Tools;
using Kairo.Windows;

namespace Kairo.Core.Tests;

/// <summary>These run real AppContainer processes: they check what the OS enforces, not what we intend.</summary>
public sealed class SandboxTests : IDisposable
{
    private static readonly string PowerShell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

    private readonly string _allowed = Directory.CreateTempSubdirectory("kairo-sbx-allowed-").FullName;
    private readonly string _closed = Directory.CreateTempSubdirectory("kairo-sbx-closed-").FullName;
    private readonly string _readOnly = Directory.CreateTempSubdirectory("kairo-sbx-ro-").FullName;

    public void Dispose()
    {
        AppContainerSandbox.RevokeAll();
        foreach (var dir in new[] { _allowed, _closed, _readOnly })
        {
            // A command that was cancelled is killed a moment after the test ends: its files are let go a little later.
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    Directory.Delete(dir, recursive: true);
                    break;
                }
                catch (IOException) when (attempt < 20)
                {
                    Thread.Sleep(250);
                }
                catch (IOException)
                {
                    break; // a temp folder left behind is not a failure of the test
                }
            }
        }
    }

    private Task<ProcessOutcome> Run(AppContainerSandbox sandbox, string command, int timeoutMs = 60_000) =>
        sandbox.RunAsync(new ProcessLaunch(PowerShell, ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", "[Console]::OutputEncoding = [Text.Encoding]::UTF8; " + command], _allowed, timeoutMs), default);

    private AppContainerSandbox Sandbox(bool network = false) =>
        new(new SandboxOptions([_allowed], [_readOnly], network));

    [Fact]
    public async Task A_command_runs_and_its_output_comes_back()
    {
        var result = await Run(Sandbox(), "Write-Output 'привет'; [Console]::Error.WriteLine('oops'); exit 3");

        Assert.True(result.ExitCode == 3, $"{result.ExitCode}: {result.Output}");
        Assert.Contains("привет", result.Output);
        Assert.Contains("oops", result.Output);
        Assert.False(result.TimedOut);
    }

    private async Task<ToolResult> Shell(string command, bool network = false, int timeoutMs = 60_000)
    {
        var ctx = new ToolContext(_allowed) { Sandbox = Sandbox(network) };
        var input = JsonSerializer.SerializeToElement(new Dictionary<string, object> { ["command"] = command, ["timeout_ms"] = timeoutMs });
        return await new ShellTool().InvokeAsync(input, ctx, default);
    }

    [Fact]
    public async Task The_shell_tool_works_in_the_project_folder_with_relative_paths()
    {
        var result = await Shell("Set-Content -Path inside.txt -Value ok -Encoding ASCII; Get-ChildItem | Select-Object -ExpandProperty Name; [Environment]::CurrentDirectory");

        Assert.False(result.IsError, result.Content);
        Assert.Contains("inside.txt", result.Content);
        Assert.Contains(_allowed, result.Content, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("ok", (await File.ReadAllTextAsync(Path.Combine(_allowed, "inside.txt"))).Trim());
    }

    [Fact]
    public async Task The_shell_tool_reports_failures_and_timeouts_like_it_does_outside_the_sandbox()
    {
        var failed = await Shell("Write-Output boom; exit 5");
        var slow = await Shell("Start-Sleep -Seconds 60", timeoutMs: 1_500);

        Assert.True(failed.IsError);
        Assert.StartsWith("Exit code 5", failed.Content);
        Assert.Contains("boom", failed.Content);
        Assert.True(slow.IsError);
        Assert.Contains("timed out", slow.Content);
    }

    [Fact]
    public async Task Git_either_works_or_the_result_says_why_it_cannot()
    {
        // Git for Windows opens the NUL device when it starts, which an AppContainer may be denied (it is on a stock Windows 10).
        var result = await Shell("git init -q; git --version");

        Assert.DoesNotContain("CLIXML", result.Content);
        Assert.True(!result.IsError || result.Content.Contains("git cannot start inside the sandbox"), result.Content);
    }

    [Fact]
    public async Task A_folder_that_was_not_granted_can_be_neither_written_nor_read()
    {
        var secret = Path.Combine(_closed, "secret.txt");
        await File.WriteAllTextAsync(secret, "top secret");

        var write = await Run(Sandbox(), $"Set-Content -Path '{Path.Combine(_closed, "new.txt")}' -Value x -ErrorAction Stop");
        var read = await Run(Sandbox(), $"Get-Content -Path '{secret}' -ErrorAction Stop");

        Assert.NotEqual(0, write.ExitCode);
        Assert.False(File.Exists(Path.Combine(_closed, "new.txt")));
        Assert.NotEqual(0, read.ExitCode);
        Assert.DoesNotContain("top secret", read.Output);
    }

    [Fact]
    public async Task A_read_only_folder_can_be_read_but_not_changed()
    {
        var file = Path.Combine(_readOnly, "info.txt");
        await File.WriteAllTextAsync(file, "readable");
        var sandbox = Sandbox();

        var read = await Run(sandbox, $"Get-Content -Path '{file}' -ErrorAction Stop");
        var write = await Run(sandbox, $"Set-Content -Path '{file}' -Value changed -ErrorAction Stop");

        Assert.Contains("readable", read.Output);
        Assert.NotEqual(0, write.ExitCode);
        Assert.Equal("readable", await File.ReadAllTextAsync(file));
    }

    [Fact]
    public async Task The_user_profile_is_closed()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var result = await Run(Sandbox(), $"Get-ChildItem -Path '{Path.Combine(profile, "Documents")}' -ErrorAction Stop");

        Assert.NotEqual(0, result.ExitCode);
    }

    [Fact]
    public async Task Without_network_even_the_loopback_is_unreachable()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var accepted = listener.AcceptTcpClientAsync();

        var result = await Run(Sandbox(), $"$c = New-Object Net.Sockets.TcpClient; $c.Connect('127.0.0.1', {port}); 'connected'", 20_000);

        Assert.NotEqual(0, result.ExitCode);
        Assert.DoesNotContain("connected", result.Output);
        Assert.False(accepted.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task A_command_that_runs_too_long_is_stopped_with_everything_it_started()
    {
        var started = DateTime.UtcNow;

        var result = await Run(Sandbox(), "Start-Process -FilePath ping.exe -ArgumentList '-n','60','127.0.0.1' -NoNewWindow; Start-Sleep -Seconds 60", timeoutMs: 3_000);

        Assert.True(result.TimedOut);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task Cancelling_stops_the_command()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Sandbox().RunAsync(
            new ProcessLaunch(PowerShell, ["-NoLogo", "-NoProfile", "-Command", "Start-Sleep -Seconds 60"], _allowed, 120_000), cts.Token));
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("with space", "\"with space\"")]
    [InlineData("say \"hi\"", "\"say \\\"hi\\\"\"")]
    [InlineData("C:\\dir with space\\", "\"C:\\dir with space\\\\\"")]
    [InlineData("", "\"\"")]
    public void Arguments_are_quoted_the_way_Windows_reads_them_back(string argument, string expected) =>
        Assert.Equal(expected, AppContainerSandbox.Quote(argument));
}
