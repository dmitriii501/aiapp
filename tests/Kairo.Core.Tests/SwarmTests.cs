using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Kairo.Core.Agents;
using Kairo.Core.Models;
using Kairo.Core.Permissions;
using Kairo.Core.Sessions;
using Kairo.Core.Tests.Fakes;
using Kairo.Core.Tools;
using Kairo.Swarm;
using Kairo.Tools;

namespace Kairo.Core.Tests;

/// <summary>A model that answers each agent of a swarm by its role, reading what it was told (the board, the news) like a model would.</summary>
internal sealed class SwarmModel(Func<string, ModelRequest, ModelResponse> respond) : IModelProvider
{
    public List<(string Agent, ModelRequest Request)> Requests { get; } = [];

    public string Id => "swarm-fake";

    public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var agent = Regex.Match(request.System ?? "", "You are \"([a-z0-9-]+)\"").Groups[1].Value;
        if (agent.Length == 0)
            agent = "(summarizer)";
        lock (Requests)
            Requests.Add((agent, request with { Messages = request.Messages.ToList() }));
        await Task.Yield();
        ct.ThrowIfCancellationRequested();
        var response = respond(agent, request);
        foreach (var t in response.Message.Parts.OfType<TextPart>())
            yield return new TextDelta(t.Text);
        yield return response;
    }

    public static ModelResponse Say(string text) => ScriptedProvider.Say(text);

    public static ModelResponse Calls(params (string Id, string Tool, string Json)[] calls) =>
        new(new ChatMessage(ChatRole.Assistant, calls.Select(c => (MessagePart)new ToolCallPart(c.Id, c.Tool, InvalidToolInput.ParseOrWrap(c.Json))).ToList()), "tool_use", new Usage(10, 5));

    /// <summary>The messages of the agent's current turn: everything after the last prompt.</summary>
    public static List<ChatMessage> CurrentTurn(ModelRequest request)
    {
        var last = request.Messages.ToList().FindLastIndex(m => m.Role == ChatRole.User && m.Parts.Any(p => p is TextPart));
        return request.Messages.Skip(last).ToList();
    }

    public static string? LastToolResult(ModelRequest request) =>
        request.Messages.SelectMany(m => m.Parts).OfType<ToolResultPart>().LastOrDefault()?.Content;

    public static string? LastToolName(ModelRequest request) =>
        request.Messages.SelectMany(m => m.Parts).OfType<ToolCallPart>().LastOrDefault()?.Name;

    public static string TurnPrompt(ModelRequest request) => CurrentTurn(request)[0].Text;
}

[Collection("Swarm")]
public class SwarmTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly string _repo;
    private readonly SwarmStore _store;
    private readonly SessionStore _sessions;
    private readonly List<SwarmEvent> _log = [];

    public SwarmTests()
    {
        _repo = Path.Combine(_dir.Path, "repo");
        Directory.CreateDirectory(_repo);
        _store = new SwarmStore(Path.Combine(_dir.Path, "swarms.db"));
        _sessions = new SessionStore(Path.Combine(_dir.Path, "data.db"));
    }

    public void Dispose()
    {
        _store.Dispose();
        _sessions.Dispose();
        // Worktrees hold read-only git files; deleting the temp folder is best effort.
        try { Directory.Delete(_dir.Path, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private async Task InitRepoAsync()
    {
        foreach (var args in new[] { new[] { "init", "-q" }, ["config", "user.name", "t"], ["config", "user.email", "t@t"] })
            await GitWorkspace.RunAsync(_repo, args);
        File.WriteAllText(Path.Combine(_repo, "README.md"), "# demo\n");
        await GitWorkspace.RunAsync(_repo, ["add", "-A"]);
        var (code, output) = await GitWorkspace.RunAsync(_repo, ["commit", "-q", "-m", "init"]);
        Assert.True(code == 0, output);
    }

    private SwarmRunner NewRunner(IModelProvider model, IPermissionPrompt? prompt = null, PermissionMode workerMode = PermissionMode.AcceptEdits)
    {
        var setup = new SwarmSetup(
            "m", Path.Combine(_dir.Path, "worktrees"), prompt ?? new FakePrompt(PermissionAnswer.Allow), PermissionRules.WithDefaults(),
            () => BuiltinTools.All(), WorkerMode: workerMode, Log: e => { lock (_log) _log.Add(e); });
        return new SwarmRunner(_store, _sessions, model, setup);
    }

    private SwarmInfo NewSwarm(string goal = "add two files", SwarmLimits? limits = null) => _store.Create(_repo, goal, limits ?? new SwarmLimits());

    // ---- The fake team ----

    private static readonly Regex BoardLine = new(@"^#(?<seq>\d+) \[(?<status>\w+)\] (?<title>.*?)(?: — (?<who>[\w-]+))?(?: \(after .*\))?$", RegexOptions.Multiline);

    /// <summary>A lead that spawns two coders, gives each a task, merges what they finish and ends the swarm when the board is done.</summary>
    private static ModelResponse Lead(string agent, ModelRequest request)
    {
        var prompt = SwarmModel.TurnPrompt(request);
        var turn = SwarmModel.CurrentTurn(request);
        var assistantSteps = turn.Count(m => m.Role == ChatRole.Assistant);

        if (prompt.StartsWith("The goal of the swarm", StringComparison.Ordinal) || prompt.StartsWith("The swarm was paused", StringComparison.Ordinal))
        {
            // Plan from what exists: start the missing workers, then give each a task, then stop for the news.
            var existing = Regex.Match(prompt, "Agents: (.*)").Groups[1].Value;
            var missing = new[] { "coder-1", "coder-2" }.Where(n => !existing.Contains(n, StringComparison.Ordinal)).ToList();
            var done = turn.SelectMany(m => m.Parts).OfType<ToolCallPart>().Select(c => c.Name).ToList();
            if (missing.Count > 0 && !done.Contains("spawn_agent"))
                return SwarmModel.Calls(missing.Select((n, i) => ($"s{i}", "spawn_agent", $$"""{"role":"coder","name":"{{n}}"}""")).ToArray());
            if (!BoardLine.IsMatch(prompt) && !done.Contains("task_create"))
            {
                return SwarmModel.Calls(
                    ("t1", "task_create", """{"title":"Add coder-1.txt","description":"Create coder-1.txt","assignee":"coder-1"}"""),
                    ("t2", "task_create", """{"title":"Add coder-2.txt","description":"Create coder-2.txt","assignee":"coder-2"}"""));
            }
            return SwarmModel.Say("planned");
        }

        // News: merge what was finished, then end the swarm when the whole board is done.
        if (assistantSteps == 0)
        {
            var finished = Regex.Matches(prompt, @"is done \((?<who>[\w-]+)\)").Select(m => m.Groups["who"].Value).Distinct().ToList();
            if (finished.Count > 0)
                return SwarmModel.Calls(finished.Select((w, i) => ($"m{i}", "merge_agent", $$"""{"name":"{{w}}"}""")).ToArray());
        }
        var board = BoardLine.Matches(prompt).Select(m => m.Groups["status"].Value).ToList();
        return board.Count > 0 && board.All(s => s == "done") && SwarmModel.LastToolName(request) != "swarm_finish"
            ? SwarmModel.Calls(("f1", "swarm_finish", """{"summary":"Both files were added and merged."}"""))
            : SwarmModel.Say("waiting for the others");
    }

    /// <summary>A worker that reads the board, takes its task, writes a file named after itself and reports.</summary>
    private static ModelResponse Worker(string agent, ModelRequest request)
    {
        var turn = SwarmModel.CurrentTurn(request);
        if (turn.All(m => m.Role != ChatRole.Assistant))
            return SwarmModel.Calls(("l1", "task_list", "{}"));

        switch (SwarmModel.LastToolName(request))
        {
            case "task_list":
            {
                var mine = BoardLine.Matches(SwarmModel.LastToolResult(request)!)
                    .FirstOrDefault(m => m.Groups["status"].Value == "pending" && (m.Groups["who"].Value is "" || m.Groups["who"].Value == agent));
                return mine is null
                    ? SwarmModel.Say("nothing for me yet")
                    : SwarmModel.Calls(("c1", "task_claim", $$"""{"id":{{mine.Groups["seq"].Value}}}"""));
            }
            case "task_claim":
                return SwarmModel.LastToolResult(request)!.StartsWith("You now hold", StringComparison.Ordinal)
                    ? SwarmModel.Calls(("w1", "Write", $$"""{"file_path":"{{agent}}.txt","content":"made by {{agent}}\n"}"""))
                    : SwarmModel.Say("someone else got it");
            case "Write":
            {
                var seq = Regex.Match(turn.SelectMany(m => m.Parts).OfType<ToolResultPart>().First(r => r.Content.StartsWith("You now hold", StringComparison.Ordinal)).Content, @"#(\d+)").Groups[1].Value;
                return SwarmModel.Calls(("u1", "task_update", $$"""{"id":{{seq}},"status":"done","result":"wrote {{agent}}.txt"}"""));
            }
            default:
                return SwarmModel.Say("done with my task");
        }
    }

    internal static ModelResponse Team(string agent, ModelRequest request) => agent == "lead" ? Lead(agent, request) : Worker(agent, request);

    // ---- Tests ----

    [Fact]
    public async Task A_lead_and_two_workers_split_a_goal_work_in_worktrees_and_the_lead_merges()
    {
        await InitRepoAsync();
        var model = new SwarmModel(Team);
        var swarm = NewSwarm();

        var outcome = await NewRunner(model).RunAsync(swarm.Id, default);

        Assert.Equal(SwarmStatus.Done, outcome.Status);
        Assert.Equal("Both files were added and merged.", outcome.Summary);
        Assert.Equal(["lead", "coder-1", "coder-2"], outcome.Agents.Select(a => a.Name));
        // Both files reached the main folder through merges of the workers' branches.
        Assert.Equal("made by coder-1\n", File.ReadAllText(Path.Combine(_repo, "coder-1.txt")).ReplaceLineEndings("\n"));
        Assert.True(File.Exists(Path.Combine(_repo, "coder-2.txt")));
        // Each worker worked in its own worktree and branch.
        var coder1 = outcome.Agents.Single(a => a.Name == "coder-1");
        Assert.StartsWith($"kairo/{swarm.Id}/", coder1.Branch);
        Assert.True(File.Exists(Path.Combine(coder1.WorkingDirectory, "coder-1.txt")));
        Assert.False(File.Exists(Path.Combine(coder1.WorkingDirectory, "coder-2.txt")));
        Assert.All(_store.Tasks(swarm.Id), t => Assert.Equal(TaskState.Done, t.Status));
        var (_, log) = await GitWorkspace.RunAsync(_repo, ["log", "--oneline"]);
        Assert.Contains("kairo: merge coder-1", log);
        Assert.True(outcome.TokensUsed > 0);
        Assert.True(outcome.ModelCalls >= 6);
    }

    [Fact]
    public async Task Workers_run_in_parallel_up_to_the_concurrency_limit()
    {
        await InitRepoAsync();
        var inFlight = 0;
        var peak = 0;
        var model = new SwarmModel((agent, request) =>
        {
            var now = Interlocked.Increment(ref inFlight);
            peak = Math.Max(peak, now);
            Thread.Sleep(30);
            Interlocked.Decrement(ref inFlight);
            return Team(agent, request);
        });
        var swarm = NewSwarm(limits: new SwarmLimits(MaxConcurrency: 1));

        var outcome = await NewRunner(model).RunAsync(swarm.Id, default);

        Assert.Equal(SwarmStatus.Done, outcome.Status);
        Assert.Equal(1, peak);
    }

    [Fact]
    public async Task Workers_can_only_take_what_is_free_and_unblocked_and_only_once()
    {
        await InitRepoAsync();
        var swarm = NewSwarm();
        _store.AddAgent(swarm.Id, "coder-1", "coder", "s1", _repo, null);
        _store.AddAgent(swarm.Id, "coder-2", "coder", "s2", _repo, null);
        var first = _store.CreateTask(swarm.Id, "first", "", [], null);
        var second = _store.CreateTask(swarm.Id, "second", "", [first.Seq], null);
        var mine = _store.CreateTask(swarm.Id, "reserved", "", [], "coder-2");

        Assert.Contains("waits for task", _store.Claim(swarm.Id, second.Seq, "coder-1"));
        Assert.Contains("assigned to coder-2", _store.Claim(swarm.Id, mine.Seq, "coder-1"));
        Assert.Null(_store.Claim(swarm.Id, first.Seq, "coder-1"));
        Assert.Contains("in_progress", _store.Claim(swarm.Id, first.Seq, "coder-2"));
        _store.UpdateTask(swarm.Id, first.Seq, TaskState.Done, "ok");
        Assert.Null(_store.Claim(swarm.Id, second.Seq, "coder-2"));
    }

    [Fact]
    public async Task A_budget_pauses_the_swarm_and_a_bigger_one_lets_it_go_on()
    {
        await InitRepoAsync();
        var model = new SwarmModel(Team);
        var swarm = NewSwarm(limits: new SwarmLimits(MaxTokens: 60)); // a few calls at 15 tokens each

        var paused = await NewRunner(model).RunAsync(swarm.Id, default);

        Assert.Equal(SwarmStatus.Paused, paused.Status);
        Assert.Contains(_log, e => e.Kind == "limit");
        Assert.Equal(SwarmStatus.Paused, _store.Get(swarm.Id)!.Status);
        var leadMessages = _sessions.LoadMessages(_store.Agents(swarm.Id).Single(a => a.Name == "lead").SessionId).Count;
        Assert.True(leadMessages > 0); // the lead's history was kept

        _store.SetLimits(swarm.Id, new SwarmLimits(MaxTokens: 1_000_000));
        var resumed = await NewRunner(model).RunAsync(swarm.Id, default);

        Assert.Equal(SwarmStatus.Done, resumed.Status);
        Assert.Single(_store.Agents(swarm.Id), a => a.Name == "lead"); // no second lead
        Assert.True(File.Exists(Path.Combine(_repo, "coder-1.txt")) && File.Exists(Path.Combine(_repo, "coder-2.txt")));
    }

    [Fact]
    public async Task A_lead_that_ends_without_tasks_or_a_report_leaves_the_swarm_stalled()
    {
        await InitRepoAsync();
        var model = new SwarmModel((_, _) => SwarmModel.Say("I think this is already done."));
        var swarm = NewSwarm();

        var outcome = await NewRunner(model).RunAsync(swarm.Id, default);

        Assert.Equal(SwarmStatus.Stalled, outcome.Status);
        Assert.Contains(_log, e => e.Text.Contains("stalled", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_swarm_needs_a_git_repository_with_a_commit()
    {
        Directory.CreateDirectory(_repo);
        var swarm = NewSwarm();

        var ex = await Assert.ThrowsAsync<SwarmException>(() => NewRunner(new SwarmModel(Team)).RunAsync(swarm.Id, default));

        Assert.Contains("git repository", ex.Message);
    }

    [Fact]
    public async Task A_worker_that_fails_gives_its_task_back_and_the_lead_is_told()
    {
        await InitRepoAsync();
        var model = new SwarmModel((agent, request) =>
        {
            if (agent == "coder-1")
                throw new ModelProviderException("503 overloaded");
            return Team(agent, request);
        });
        var swarm = NewSwarm();

        var outcome = await NewRunner(model).RunAsync(swarm.Id, default);

        Assert.Contains(_log, e => e.Agent == "coder-1" && e.Text.Contains("503", StringComparison.Ordinal));
        Assert.Contains(_store.Messages(swarm.Id), m => m.From == "system" && m.Text.Contains("coder-1", StringComparison.Ordinal));
        // The task of the failed worker is still open; the other worker finished its own.
        Assert.Equal(TaskState.Done, _store.Tasks(swarm.Id).Single(t => t.Title.Contains("coder-2")).Status);
        Assert.NotEqual(SwarmStatus.Done, outcome.Status);
    }

    [Fact]
    public async Task Merging_asks_the_user_and_a_refusal_merges_nothing()
    {
        await InitRepoAsync();
        var prompt = new FakePrompt(PermissionAnswer.Deny);
        var model = new SwarmModel(Team);
        var swarm = NewSwarm();

        var outcome = await NewRunner(model, prompt).RunAsync(swarm.Id, default);

        Assert.Contains(prompt.Asked, a => a.Tool == "merge_agent" && a.Call.Summary.StartsWith("[lead] merge_agent(coder-", StringComparison.Ordinal));
        Assert.False(File.Exists(Path.Combine(_repo, "coder-1.txt")));
        Assert.NotEqual("", outcome.Status);
    }

    [Fact]
    public async Task A_merge_conflict_is_reported_and_leaves_the_main_folder_clean()
    {
        await InitRepoAsync();
        var swarm = NewSwarm();
        var worktree = Path.Combine(_dir.Path, "wt");
        await GitWorkspace.AddWorktreeAsync(_repo, worktree, "kairo/x/w");
        File.WriteAllText(Path.Combine(worktree, "README.md"), "worker version\n");
        Assert.True(await GitWorkspace.CommitAllAsync(worktree, "worker change"));
        File.WriteAllText(Path.Combine(_repo, "README.md"), "main version\n");
        await GitWorkspace.RunAsync(_repo, ["add", "-A"]);
        await GitWorkspace.RunAsync(_repo, ["commit", "-q", "-m", "main change"]);

        var result = await GitWorkspace.MergeAsync(_repo, "kairo/x/w", "merge");

        Assert.False(result.Merged);
        Assert.Equal(["README.md"], result.Conflicts);
        var (_, status) = await GitWorkspace.RunAsync(_repo, ["status", "--porcelain"]);
        Assert.Equal("", status); // aborted: no conflict markers left behind
    }
}

public class SwarmToolTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly SwarmStore _store;
    private readonly SwarmInfo _swarm;
    private readonly StubHost _host;

    private sealed class StubHost(SwarmStore store, string id, string dir) : ISwarmHost
    {
        public int Changes { get; private set; }
        public string? Finished { get; private set; }
        public string SwarmId { get; } = id;
        public string MainDirectory { get; } = dir;
        public SwarmStore Store { get; } = store;
        public Task<string> SpawnAsync(string role, string? name, string? brief, CancellationToken ct) => Task.FromResult(name ?? "coder-1");
        public void Finish(string summary) => Finished = summary;
        public void Changed() => Changes++;
    }

    public SwarmToolTests()
    {
        _store = new SwarmStore(Path.Combine(_dir.Path, "swarms.db"));
        _swarm = _store.Create(_dir.Path, "goal", new SwarmLimits());
        _store.AddAgent(_swarm.Id, "lead", "lead", "s0", _dir.Path, null);
        _store.AddAgent(_swarm.Id, "coder-1", "coder", "s1", _dir.Path, "kairo/b/coder-1");
        _store.AddAgent(_swarm.Id, "coder-2", "coder", "s2", _dir.Path, "kairo/b/coder-2");
        _host = new StubHost(_store, _swarm.Id, _dir.Path);
    }

    public void Dispose()
    {
        _store.Dispose();
        _dir.Dispose();
    }

    private ToolContext Ctx => new(_dir.Path);

    private static System.Text.Json.JsonElement In(string json) => Json.Of(json);

    [Fact]
    public async Task Only_the_lead_creates_tasks_and_they_must_name_real_dependencies_and_agents()
    {
        var lead = new TaskCreateTool(_host, "lead");

        Assert.True((await new TaskCreateTool(_host, "coder-1").InvokeAsync(In("""{"title":"t","description":"d"}"""), Ctx, default)).IsError);
        Assert.Contains("#1", (await lead.InvokeAsync(In("""{"title":"one","description":"d","assignee":"CODER-2"}"""), Ctx, default)).Content);
        Assert.Equal("coder-2", _store.Tasks(_swarm.Id).Single().Assignee);
        await Assert.ThrowsAsync<ToolInputException>(() => lead.InvokeAsync(In("""{"title":"two","description":"d","depends_on":[9]}"""), Ctx, default));
        await Assert.ThrowsAsync<ToolInputException>(() => lead.InvokeAsync(In("""{"title":"two","description":"d","assignee":"nobody"}"""), Ctx, default));
        await lead.InvokeAsync(In("""{"title":"two","description":"d","depends_on":[1]}"""), Ctx, default);
        Assert.Equal([1], _store.Tasks(_swarm.Id)[1].DependsOn);
        Assert.True(_host.Changes >= 2);
    }

    [Fact]
    public async Task A_worker_cannot_update_a_task_that_is_not_its_own()
    {
        _store.CreateTask(_swarm.Id, "t", "d", [], "coder-1");
        _store.Claim(_swarm.Id, 1, "coder-1");

        var other = await new TaskUpdateTool(_host, "coder-2").InvokeAsync(In("""{"id":1,"status":"done","result":"x"}"""), Ctx, default);
        var owner = await new TaskUpdateTool(_host, "lead").InvokeAsync(In("""{"id":1,"status":"failed","result":"gave up"}"""), Ctx, default);

        Assert.True(other.IsError);
        Assert.False(owner.IsError);
        Assert.Equal(TaskState.Failed, _store.GetTask(_swarm.Id, 1)!.Status);
        await Assert.ThrowsAsync<ToolInputException>(() => new TaskUpdateTool(_host, "lead").InvokeAsync(In("""{"id":1,"status":"done"}"""), Ctx, default));
    }

    [Fact]
    public async Task Messages_reach_the_named_agents_once_and_everyone_on_all()
    {
        var send = new SendMessageTool(_host, "coder-1");

        await send.InvokeAsync(In("""{"to":"lead","text":"which API?"}"""), Ctx, default);
        await send.InvokeAsync(In("""{"to":"all","text":"I own src/api"}"""), Ctx, default);

        Assert.True((await send.InvokeAsync(In("""{"to":"coder-1","text":"to myself"}"""), Ctx, default)).IsError);
        var inbox = await new ReadInboxTool(_host, "lead").InvokeAsync(In("{}"), Ctx, default);
        Assert.Contains("<message from=\"coder-1\">\nwhich API?", inbox.Content);
        Assert.Contains("I own src/api", inbox.Content);
        Assert.Equal("No new messages.", (await new ReadInboxTool(_host, "lead").InvokeAsync(In("{}"), Ctx, default)).Content);
        Assert.Equal(1, _store.UnreadCount(_swarm.Id, "coder-2"));
        Assert.Equal(0, _store.UnreadCount(_swarm.Id, "coder-1"));
    }

    [Fact]
    public async Task Only_the_lead_spawns_merges_and_finishes_and_workers_share_notes()
    {
        Assert.True((await new SpawnAgentTool(_host, "coder-1").InvokeAsync(In("""{"role":"coder"}"""), Ctx, default)).IsError);
        Assert.True((await new SwarmFinishTool(_host, "coder-1").InvokeAsync(In("""{"summary":"x"}"""), Ctx, default)).IsError);
        Assert.Throws<ToolInputException>(() => new MergeAgentTool(_host, "coder-1").Assess(In("""{"name":"coder-2"}"""), Ctx));

        var merge = new MergeAgentTool(_host, "lead").Assess(In("""{"name":"coder-2"}"""), Ctx);
        Assert.Equal(RiskLevel.Exec, merge.Risk);
        Assert.Equal("git merge kairo/b/coder-2", merge.Subject);

        await new SwarmFinishTool(_host, "lead").InvokeAsync(In("""{"summary":"all done"}"""), Ctx, default);
        Assert.Equal("all done", _host.Finished);

        await new MemoryWriteTool(_host, "coder-1").InvokeAsync(In("""{"text":"tests live in /t"}"""), Ctx, default);
        Assert.Contains("(coder-1) tests live in /t", (await new MemoryReadTool(_host, "coder-2").InvokeAsync(In("{}"), Ctx, default)).Content);
    }
}

[Collection("Swarm")]
public class SwarmServiceTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly string _repo;
    private readonly SwarmStore _swarms;
    private readonly Kairo.Core.Sessions.SessionStore _sessions;
    private readonly Kairo.Host.SettingsService _settings;
    private readonly System.Collections.Concurrent.ConcurrentQueue<Kairo.Protocol.SwarmDetail> _pushed = new();

    public SwarmServiceTests()
    {
        _repo = Path.Combine(_dir.Path, "repo");
        Directory.CreateDirectory(_repo);
        _swarms = new SwarmStore(Path.Combine(_dir.Path, "swarms.db"));
        _sessions = new Kairo.Core.Sessions.SessionStore(Path.Combine(_dir.Path, "data.db"));
        _settings = new Kairo.Host.SettingsService(Path.Combine(_dir.Path, "user"), new Kairo.Core.Secrets.InMemorySecretStore());
    }

    public void Dispose()
    {
        _swarms.Dispose();
        _sessions.Dispose();
        try { Directory.Delete(_dir.Path, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private async Task InitRepoAsync()
    {
        foreach (var args in new[] { new[] { "init", "-q" }, ["config", "user.name", "t"], ["config", "user.email", "t@t"] })
            await GitWorkspace.RunAsync(_repo, args);
        File.WriteAllText(Path.Combine(_repo, "README.md"), "# demo\n");
        await GitWorkspace.RunAsync(_repo, ["add", "-A"]);
        await GitWorkspace.RunAsync(_repo, ["commit", "-q", "-m", "init"]);
    }

    private Kairo.Host.SwarmService NewService(IModelProvider model)
    {
        _sessions.TouchWorkspace(_repo);
        return new Kairo.Host.SwarmService(_swarms, _sessions, _settings, d => { _pushed.Enqueue(d); return Task.CompletedTask; }, _ => model);
    }

    private static async Task<T> Eventually<T>(Func<T?> probe, int seconds = 30) where T : class
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            if (probe() is { } found)
                return found;
            await Task.Delay(20);
        }
        throw new TimeoutException("The swarm did not get there in time.");
    }

    [Fact]
    public async Task A_swarm_started_from_the_ui_runs_asks_before_merging_and_reports_its_state()
    {
        await InitRepoAsync();
        var service = NewService(new SwarmModel(SwarmTests.Team));

        var summary = await service.StartAsync(new Kairo.Protocol.SwarmStartParams(_repo, "add two files", Workers: 2), default);
        Assert.True(summary.Running);

        // The user answers every question the swarm asks, as the UI would.
        var asked = new List<string>();
        var detail = await Eventually(() =>
        {
            var d = service.Get(summary.Id);
            foreach (var prompt in d.PendingPermissions)
            {
                asked.Add(prompt.Summary);
                service.TryRespond(prompt.RequestId, Kairo.Core.Agents.PermissionAnswer.Allow);
            }
            return d.Summary.Running ? null : d;
        });

        Assert.Equal("done", detail.Summary.Status);
        Assert.Equal("Both files were added and merged.", detail.Report);
        Assert.Equal(3, detail.Agents.Count);
        Assert.All(detail.Tasks, t => Assert.Equal("done", t.Status));
        Assert.Contains(detail.Log, l => l.Kind == "tool" && l.Agent == "coder-1");
        Assert.Contains(asked, a => a.StartsWith("[lead] merge_agent(coder-", StringComparison.Ordinal));
        Assert.True(File.Exists(Path.Combine(_repo, "coder-1.txt")));
        Assert.Contains(_pushed, d => d.Summary.Running && d.Log.Count > 0); // updates were pushed while it ran
        Assert.False(service.AnyRunning());
    }

    [Fact]
    public async Task A_running_swarm_can_be_paused_and_resumed_with_a_new_budget()
    {
        await InitRepoAsync();
        var model = new SwarmModel((agent, request) =>
        {
            Thread.Sleep(150);
            return SwarmTests.Team(agent, request);
        });
        var service = NewService(model);
        var summary = await service.StartAsync(new Kairo.Protocol.SwarmStartParams(_repo, "add two files", Workers: 2, MaxTokens: 50_000), default);

        service.Stop(summary.Id);
        var paused = await Eventually(() => service.Get(summary.Id) is { Summary.Running: false } d ? d : null);
        Assert.Equal("paused", paused.Summary.Status);

        var resumed = service.Resume(summary.Id, 900_000);
        Assert.True(resumed.Summary.Running);
        Assert.Equal(900_000, resumed.Summary.MaxTokens);
        Assert.Throws<Kairo.Core.Rpc.RpcException>(() => service.Resume(summary.Id, null)); // already running

        // Whoever asks is answered, so the swarm can end.
        var final = await Eventually(() =>
        {
            var d = service.Get(summary.Id);
            foreach (var p in d.PendingPermissions)
                service.TryRespond(p.RequestId, Kairo.Core.Agents.PermissionAnswer.Allow);
            return d.Summary.Running ? null : d;
        });
        Assert.Equal("done", final.Summary.Status);
    }

    [Fact]
    public async Task A_swarm_needs_a_known_git_folder_and_a_goal()
    {
        var service = NewService(new SwarmModel(SwarmTests.Team));
        var plain = Path.Combine(_dir.Path, "plain");
        Directory.CreateDirectory(plain);
        _sessions.TouchWorkspace(plain);

        await Assert.ThrowsAsync<Kairo.Core.Rpc.RpcException>(() => service.StartAsync(new Kairo.Protocol.SwarmStartParams(_repo, "   "), default));
        var ex = await Assert.ThrowsAsync<Kairo.Core.Rpc.RpcException>(() => service.StartAsync(new Kairo.Protocol.SwarmStartParams(plain, "goal"), default));
        Assert.Contains("git", ex.Message);
        await Assert.ThrowsAsync<Kairo.Core.Rpc.RpcException>(() => service.StartAsync(new Kairo.Protocol.SwarmStartParams(@"C:\", "goal"), default));
    }
}
