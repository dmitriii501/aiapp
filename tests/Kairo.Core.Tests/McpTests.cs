using System.ComponentModel;
using System.IO.Pipelines;
using System.Text.Json;
using Kairo.Core.Permissions;
using Kairo.Core.Tools;
using Kairo.Mcp;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Kairo.Core.Tests.Fakes;

namespace Kairo.Core.Tests;

public class McpTests : IAsyncLifetime
{
    private readonly TempDir _dir = new();
    private readonly List<IAsyncDisposable> _servers = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var s in _servers)
            await s.DisposeAsync();
        _dir.Dispose();
    }

    private McpConfigStore Store(string json = """{"mcpServers":{}}""")
    {
        var path = Path.Combine(_dir.Path, "mcp.json");
        File.WriteAllText(path, json);
        return new McpConfigStore(path);
    }

    /// <summary>A real MCP server running in this process, wired to the client over pipes.</summary>
    private IClientTransport InProcessServer(params McpServerTool[] tools)
    {
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();
        var options = new McpServerOptions { ToolCollection = [.. tools] };
        var server = McpServer.Create(new StreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream()), options);
        _servers.Add(server);
        _ = server.RunAsync();
        return new StreamClientTransport(clientToServer.Writer.AsStream(), serverToClient.Reader.AsStream());
    }

    private static McpServerTool Echo() =>
        McpServerTool.Create(([Description("Text")] string text) => $"echo: {text}", new McpServerToolCreateOptions { Name = "echo", Description = "Repeats the text" });

    private static McpServerTool Boom() =>
        McpServerTool.Create((Func<string>)(() => throw new InvalidOperationException("kaboom")), new McpServerToolCreateOptions { Name = "boom" });

    [Fact]
    public async Task Tools_are_exposed_under_prefixed_names_and_can_be_called()
    {
        await using var manager = new McpManager(Store("""{"mcpServers":{"demo":{"command":"x"}}}"""), (_, _, _) => InProcessServer(Echo()));

        await manager.ReloadAsync();

        var tool = Assert.Single(manager.Tools);
        Assert.Equal("mcp__demo__echo", tool.Spec.Name);
        Assert.Contains("Repeats the text", tool.Spec.Description);
        Assert.Equal("object", tool.Spec.InputSchema.GetProperty("type").GetString());
        var state = Assert.Single(manager.Servers);
        Assert.Equal((McpStatus.Ready, "stdio"), (state.Status, state.Transport));
        Assert.Equal(["echo"], state.Tools);

        var input = JsonDocument.Parse("""{"text":"hi"}""").RootElement;
        var ctx = new ToolContext(_dir.Path);
        var assessment = tool.Assess(input, ctx);
        var result = await tool.InvokeAsync(input, ctx, CancellationToken.None);

        Assert.Equal(RiskLevel.Exec, assessment.Risk);
        Assert.Equal("mcp__demo__echo({\"text\":\"hi\"})", assessment.Summary);
        Assert.False(result.IsError);
        Assert.Equal("echo: hi", result.Content);
    }

    [Fact]
    public async Task A_tool_that_fails_returns_an_error_result()
    {
        await using var manager = new McpManager(Store("""{"mcpServers":{"demo":{"command":"x"}}}"""), (_, _, _) => InProcessServer(Boom()));
        await manager.ReloadAsync();

        var result = await manager.Tools.Single().InvokeAsync(JsonDocument.Parse("{}").RootElement, new ToolContext(_dir.Path), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("boom", result.Content); // the SDK hides the exception text from the client
    }

    [Fact]
    public async Task A_failing_server_does_not_block_the_others()
    {
        var store = Store("""{"mcpServers":{"bad":{"command":"nope"},"good":{"command":"x"},"off":{"command":"x","disabled":true}}}""");
        await using var manager = new McpManager(store, (name, _, onStderr) =>
        {
            if (name != "good")
            {
                onStderr("spawn nope ENOENT");
                throw new FileNotFoundException("Cannot start 'nope'.");
            }
            return InProcessServer(Echo());
        });

        await manager.ReloadAsync();

        var states = manager.Servers.ToDictionary(s => s.Name);
        Assert.Equal(McpStatus.Ready, states["good"].Status);
        Assert.Equal(McpStatus.Disabled, states["off"].Status);
        Assert.Equal(McpStatus.Failed, states["bad"].Status);
        Assert.Contains("Cannot start 'nope'", states["bad"].Error);
        Assert.Contains("spawn nope ENOENT", states["bad"].Error);
        Assert.Equal(["mcp__good__echo"], manager.Tools.Select(t => t.Spec.Name));
    }

    [Fact]
    public async Task Reload_keeps_unchanged_servers_connected_and_applies_changes()
    {
        var store = Store("""{"mcpServers":{"a":{"command":"x"},"b":{"command":"y"}}}""");
        var started = new List<string>();
        await using var manager = new McpManager(store, (name, _, _) =>
        {
            started.Add(name);
            return InProcessServer(Echo());
        });
        await manager.ReloadAsync();

        store.Remove("b");
        store.Add("c", new McpServerConfig { Command = "z" });
        await manager.ReloadAsync();

        Assert.Equal(["a", "b", "c"], started); // "a" was not restarted
        Assert.Equal(["a", "c"], manager.Servers.Select(s => s.Name));
        Assert.Equal(["mcp__a__echo", "mcp__c__echo"], manager.Tools.Select(t => t.Spec.Name));

        store.SetDisabled("a", true);
        await manager.ReloadAsync();

        Assert.Equal(McpStatus.Disabled, manager.Servers.Single(s => s.Name == "a").Status);
        Assert.Equal(["mcp__c__echo"], manager.Tools.Select(t => t.Spec.Name));
    }

    [Fact]
    public async Task A_broken_config_file_is_reported_not_thrown()
    {
        await using var manager = new McpManager(Store("{ not json"));

        await manager.ReloadAsync();

        var state = Assert.Single(manager.Servers);
        Assert.Equal(McpStatus.Failed, state.Status);
        Assert.Contains("mcp.json", state.Error);
        Assert.Empty(manager.Tools);
    }

    [Theory]
    [InlineData("github", "create_issue", "mcp__github__create_issue")]
    [InlineData("my server", "list-files", "mcp__my_server__list_files")]
    [InlineData("сервер", "тул", "mcp_____________")]
    public void Tool_names_use_only_characters_the_model_apis_accept(string server, string tool, string expected)
    {
        Assert.Equal(expected, McpManager.UniqueName(server, tool, []));
    }

    [Fact]
    public void Tool_names_are_unique_and_at_most_64_characters()
    {
        var used = new HashSet<string>();
        var first = McpManager.UniqueName("a b", "x", used);
        var second = McpManager.UniqueName("a_b", "x", used); // sanitizes to the same name
        var long1 = McpManager.UniqueName("server", new string('t', 100), used);
        var long2 = McpManager.UniqueName("server", new string('t', 100), used);

        Assert.Equal("mcp__a_b__x", first);
        Assert.Equal("mcp__a_b__x_2", second);
        Assert.True(long1.Length <= 64 && long2.Length <= 64);
        Assert.NotEqual(long1, long2);
    }

    [Fact]
    public void Config_store_adds_removes_and_validates()
    {
        var store = Store();

        store.Add("files", new McpServerConfig { Command = "npx", Args = ["-y", "server-filesystem", "C:\\proj"] });
        store.Add("remote", new McpServerConfig { Url = "https://mcp.example.com/mcp", Headers = new() { ["Authorization"] = "Bearer x" } });

        var loaded = store.Load();
        Assert.Equal(["files", "remote"], loaded.Keys.Order());
        Assert.Equal("http", loaded["remote"].Transport);
        Assert.Equal("Bearer x", loaded["remote"].Headers!["Authorization"]);

        Assert.Throws<McpConfigException>(() => store.Add("files", new McpServerConfig { Command = "x" })); // duplicate
        Assert.Throws<McpConfigException>(() => store.Add("bad/name", new McpServerConfig { Command = "x" }));
        Assert.Throws<McpConfigException>(() => store.Add("both", new McpServerConfig { Command = "x", Url = "https://a.b" }));
        Assert.Throws<McpConfigException>(() => store.Add("none", new McpServerConfig()));
        Assert.Throws<McpConfigException>(() => store.Add("ftp", new McpServerConfig { Url = "ftp://a.b" }));

        store.SetDisabled("files", true);
        Assert.True(store.Load()["files"].Disabled);
        Assert.True(store.Remove("files"));
        Assert.False(store.Remove("files"));
        Assert.Equal(["remote"], store.Load().Keys);
    }

    [Fact]
    public void Servers_can_be_imported_from_claude_desktop_without_overwriting()
    {
        var store = Store("""{"mcpServers":{"keep":{"command":"mine"}}}""");
        var desktop = Path.Combine(_dir.Path, "claude_desktop_config.json");
        File.WriteAllText(desktop, """
            { "theme": "dark",
              "mcpServers": {
                "keep": { "command": "theirs" },
                "fs": { "command": "npx", "args": ["-y", "fs"], "env": { "TOKEN": "t" } } } }
            """);

        var imported = store.ImportFrom(desktop);

        Assert.Equal(["fs"], imported);
        var all = store.Load();
        Assert.Equal("mine", all["keep"].Command);
        Assert.Equal("t", all["fs"].Env!["TOKEN"]);
        Assert.Throws<McpConfigException>(() => store.ImportFrom(Path.Combine(_dir.Path, "missing.json")));
    }

    [Theory]
    [InlineData("npx -y server", "npx", new[] { "-y", "server" })]
    [InlineData("node \"C:\\my folder\\s.js\" --flag", "node", new[] { "C:\\my folder\\s.js", "--flag" })]
    [InlineData("   ", "", new string[0])]
    public void Command_lines_are_split_with_quotes(string line, string command, string[] args)
    {
        var (c, a) = CommandLine.Split(line);

        Assert.Equal(command, c);
        Assert.Equal(args, a);
    }

    [Fact]
    public void A_server_wide_rule_matches_all_its_tools_and_always_allow_covers_the_tool()
    {
        var ctx = new ToolContext(_dir.Path);
        var call = new ToolAssessment(RiskLevel.Exec, "{}", "mcp__github__create_issue({})");
        var rules = new PermissionRules();
        rules.Allow.Add(PermissionRule.Parse("mcp__github__*"));
        var engine = new PermissionEngine(rules, PermissionMode.Default);

        Assert.Equal(PermissionDecision.Allow, engine.Evaluate("mcp__github__create_issue", call, ctx).Decision);
        Assert.Equal(PermissionDecision.Ask, engine.Evaluate("mcp__gitlab__create_issue", call, ctx).Decision);

        var other = new PermissionEngine(new PermissionRules(), PermissionMode.Default);
        other.AllowForSession("mcp__x__y", new ToolAssessment(RiskLevel.Exec, "{\"a\":1}", "s"));
        Assert.Equal(PermissionDecision.Allow, other.Evaluate("mcp__x__y", new ToolAssessment(RiskLevel.Exec, "{\"a\":2}", "s"), ctx).Decision);
    }
}

public class McpImportTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private string Write(string name, string json)
    {
        var path = Path.Combine(_dir.Path, name);
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public async Task Import_reads_every_claude_desktop_config_and_reports_when_there_is_none()
    {
        var store = new McpConfigStore(Write("mcp.json", """{"mcpServers":{}}"""));
        await using var manager = new McpManager(store, (_, _, _) => throw new FileNotFoundException("not started in this test"));
        var usual = Write("usual.json", """{"mcpServers":{"a":{"command":"x"},"shared":{"command":"x"}}}""");
        var packaged = Write("packaged.json", """{"mcpServers":{"b":{"command":"x"},"shared":{"command":"other"}}}""");

        var result = new Kairo.Host.McpService(manager, store, () => [usual, packaged]).ImportDesktop();

        Assert.Equal(["a", "shared", "b"], result.Imported);
        Assert.Equal("x", store.Load()["shared"].Command); // the first file wins

        var none = new Kairo.Host.McpService(manager, store, () => []);
        var ex = Assert.Throws<Kairo.Core.Rpc.RpcException>(none.ImportDesktop);
        Assert.Contains("не найден", ex.Message);
    }
}

public class CommandResolverTests
{
    [Fact]
    public void Finds_programs_on_path_and_by_full_path_and_rejects_unknown_ones()
    {
        using var dir = new Fakes.TempDir();
        var tool = Path.Combine(dir.Path, "mytool.cmd");
        File.WriteAllText(tool, "@echo off");

        Assert.True(CommandResolver.Exists(tool));
        Assert.True(CommandResolver.Exists(Path.Combine(dir.Path, "mytool"))); // extension from PATHEXT
        Assert.True(CommandResolver.Exists(".\\mytool", dir.Path));
        Assert.False(CommandResolver.Exists("definitely-not-a-command-xyz"));
        Assert.False(CommandResolver.Exists(""));
    }
}
