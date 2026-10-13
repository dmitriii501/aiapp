using System.Text.Json;
using Kairo.Core.Config;
using Kairo.Core.Permissions;
using Kairo.Core.Tools;
using Kairo.Tools;

namespace Kairo.Core.Tests;

public sealed class SandboxPolicyTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("kairo-policy-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private sealed class FakeSandbox(params string[] writable) : IProcessSandbox
    {
        public IReadOnlyList<string> WritablePaths { get; } = writable;
        public List<ProcessLaunch> Launches { get; } = [];

        public Task<ProcessOutcome> RunAsync(ProcessLaunch launch, CancellationToken ct)
        {
            Launches.Add(launch);
            return Task.FromResult(new ProcessOutcome(0, "ran in the fake sandbox", false));
        }
    }

    private sealed class FakeProvider(Func<SandboxSpec, IProcessSandbox> create) : ISandboxProvider
    {
        public SandboxSpec? Last { get; private set; }

        public IProcessSandbox Create(SandboxSpec spec)
        {
            Last = spec;
            return create(spec);
        }
    }

    private static KairoConfig Config(string toml)
    {
        var dir = Directory.CreateTempSubdirectory("kairo-cfg-").FullName;
        File.WriteAllText(Path.Combine(dir, "config.toml"), toml);
        return KairoConfig.Load(dir, dir, includeProject: false);
    }

    [Theory]
    [InlineData("off", PermissionMode.Bypass, true, false)]
    [InlineData("autonomous", PermissionMode.Default, false, false)]
    [InlineData("autonomous", PermissionMode.AcceptEdits, false, false)]
    [InlineData("autonomous", PermissionMode.Bypass, false, true)]
    [InlineData("autonomous", PermissionMode.Default, true, true)]
    [InlineData("always", PermissionMode.Default, false, true)]
    public void The_setting_decides_who_runs_in_the_sandbox(string setting, PermissionMode mode, bool worker, bool expected)
    {
        var policy = new SandboxPolicy(Config($"[sandbox]\nshell = \"{setting}\""), new FakeProvider(_ => new FakeSandbox()));

        Assert.Equal(expected, policy.Applies(mode, worker));
        Assert.Equal(expected, policy.For(mode, worker, [_root]) is not null);
    }

    [Fact]
    public void Without_a_setting_nothing_is_sandboxed()
    {
        var policy = new SandboxPolicy(new KairoConfig(), new FakeProvider(_ => new FakeSandbox()));

        Assert.Null(policy.For(PermissionMode.Bypass, swarmWorker: true, [_root]));
    }

    [Fact]
    public void The_spec_carries_the_folders_network_and_memory_from_the_settings()
    {
        var read = Path.Combine(_root, "tools");
        var provider = new FakeProvider(_ => new FakeSandbox());
        var config = Config($"[sandbox]\nshell = \"always\"\nnetwork = true\nmemory_mb = 2048\nread = [\"{read.Replace("\\", "/")}\"]");

        new SandboxPolicy(config, provider).For(PermissionMode.Default, false, [_root]);

        Assert.NotNull(provider.Last);
        Assert.Equal([Path.GetFullPath(_root)], provider.Last.Writable);
        Assert.Equal([Path.GetFullPath(read)], provider.Last.Readable);
        Assert.True(provider.Last.Network);
        Assert.Equal(2048, provider.Last.MemoryLimitMb);
    }

    [Fact]
    public void Network_is_off_by_default()
    {
        var provider = new FakeProvider(_ => new FakeSandbox());

        new SandboxPolicy(Config("[sandbox]\nshell = \"always\""), provider).For(PermissionMode.Default, false, [_root]);

        Assert.False(provider.Last!.Network);
        Assert.Equal(SandboxPolicy.DefaultMemoryMb, provider.Last.MemoryLimitMb);
    }

    [Fact]
    public void A_project_cannot_lift_or_widen_the_users_sandbox()
    {
        var user = Directory.CreateTempSubdirectory("kairo-user-").FullName;
        var project = Directory.CreateTempSubdirectory("kairo-proj-").FullName;
        File.WriteAllText(Path.Combine(user, "config.toml"), "[sandbox]\nshell = \"always\"\nread = [\"C:/tools\"]");
        Directory.CreateDirectory(Path.Combine(project, ".kairo"));
        File.WriteAllText(Path.Combine(project, ".kairo", "config.toml"),
            "[sandbox]\nshell = \"off\"\nnetwork = true\nmemory_mb = 99999\nread = [\"C:/Users\"]");

        var config = KairoConfig.Load(project, user);

        Assert.Equal("always", config.SandboxShell);
        Assert.Null(config.SandboxNetwork);
        Assert.Null(config.SandboxMemoryMb);
        Assert.Equal(["C:/tools"], config.SandboxRead);
    }

    [Fact]
    public void An_unknown_mode_is_an_error_not_a_silent_off()
    {
        var ex = Assert.Throws<FormatException>(() => Config("[sandbox]\nshell = \"strict\""));

        Assert.Contains("shell", ex.Message);
    }

    [Fact]
    public async Task When_the_sandbox_is_wanted_but_cannot_be_made_commands_are_refused_not_run_freely()
    {
        var marker = Path.Combine(_root, "ran.txt");
        foreach (var provider in new ISandboxProvider?[] { null, new FakeProvider(_ => throw new InvalidOperationException("no profile")) })
        {
            var policy = new SandboxPolicy(Config("[sandbox]\nshell = \"always\""), provider);
            var ctx = new ToolContext(_root) { Sandbox = policy.For(PermissionMode.Default, false, [_root]) };
            var input = JsonSerializer.SerializeToElement(new Dictionary<string, object> { ["command"] = $"Set-Content '{marker}' x" });

            var result = await new ShellTool().InvokeAsync(input, ctx, default);

            Assert.True(result.IsError);
            Assert.Contains("was not run", result.Content);
            Assert.False(File.Exists(marker));
        }
    }

    [Fact]
    public async Task Shell_hands_the_command_to_the_sandbox_instead_of_starting_a_process()
    {
        var fake = new FakeSandbox(_root);
        var ctx = new ToolContext(_root) { Sandbox = fake };
        var input = JsonSerializer.SerializeToElement(new Dictionary<string, object> { ["command"] = "Write-Output hi", ["timeout_ms"] = 5000 });

        var result = await new ShellTool().InvokeAsync(input, ctx, default);

        Assert.Equal("ran in the fake sandbox", result.Content);
        var launch = Assert.Single(fake.Launches);
        Assert.Equal(Path.GetFullPath(_root), launch.WorkingDirectory);
        Assert.Equal(5000, launch.TimeoutMs);
        Assert.Contains("-EncodedCommand", launch.Arguments);
    }

    [Fact]
    public async Task Write_and_Edit_stay_inside_the_sandbox_folders()
    {
        var outside = Directory.CreateTempSubdirectory("kairo-outside-").FullName;
        try
        {
            var ctx = new ToolContext(_root) { Sandbox = new FakeSandbox(_root) };
            JsonElement Write(string path) => JsonSerializer.SerializeToElement(new { file_path = path, content = "x" });

            var inside = await new WriteTool().InvokeAsync(Write(Path.Combine(_root, "a.txt")), ctx, default);
            var denied = await new WriteTool().InvokeAsync(Write(Path.Combine(outside, "a.txt")), ctx, default);
            File.WriteAllText(Path.Combine(outside, "b.txt"), "old");
            ctx.ReadFiles.Add(Path.Combine(outside, "b.txt"));
            var edit = JsonSerializer.SerializeToElement(new { file_path = Path.Combine(outside, "b.txt"), old_string = "old", new_string = "new" });
            var deniedEdit = await new EditTool().InvokeAsync(edit, ctx, default);

            Assert.False(inside.IsError, inside.Content);
            Assert.True(denied.IsError);
            Assert.Contains("outside the folders", denied.Content);
            Assert.False(File.Exists(Path.Combine(outside, "a.txt")));
            Assert.True(deniedEdit.IsError);
            Assert.Equal("old", File.ReadAllText(Path.Combine(outside, "b.txt")));
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public async Task Without_a_sandbox_the_file_tools_are_not_fenced()
    {
        var outside = Directory.CreateTempSubdirectory("kairo-outside-").FullName;
        try
        {
            var input = JsonSerializer.SerializeToElement(new { file_path = Path.Combine(outside, "a.txt"), content = "x" });

            var result = await new WriteTool().InvokeAsync(input, new ToolContext(_root), default);

            Assert.False(result.IsError, result.Content);
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }
}
