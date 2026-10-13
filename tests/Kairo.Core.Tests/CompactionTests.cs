using System.Collections.Concurrent;
using Kairo.Core.Agents;
using Kairo.Core.Config;
using Kairo.Core.Models;
using Kairo.Core.Permissions;
using Kairo.Core.Secrets;
using Kairo.Core.Sessions;
using Kairo.Core.Tests.Fakes;
using Kairo.Core.Tools;
using Kairo.Host;
using Kairo.Protocol;
using Kairo.Tools;
using static Kairo.Core.Tests.Fakes.ScriptedProvider;

namespace Kairo.Core.Tests;

public class CompactionTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private static ChatMessage Summary(string text) => Compaction.Wrap(text);

    private static ChatMessage Assistant(string text) => new(ChatRole.Assistant, [new TextPart(text)]);

    private Agent NewAgent(ScriptedProvider model, IEnumerable<ChatMessage>? history = null, AgentOptions? options = null) =>
        new(model, new ToolRegistry(BuiltinTools.All()), new PermissionEngine(PermissionRules.WithDefaults(), PermissionMode.Default),
            new FakePrompt(PermissionAnswer.Allow), new ToolContext(_dir.Path), "system", options ?? new AgentOptions("m"), history);

    [Fact]
    public void The_model_sees_the_history_from_the_last_summary_on()
    {
        var history = new[] { ChatMessage.UserText("old"), Assistant("old answer"), Summary("first"), ChatMessage.UserText("mid"), Summary("second"), ChatMessage.UserText("new") };

        var effective = Compaction.Effective(history);

        Assert.Equal(2, effective.Count);
        Assert.Equal("second", Compaction.SummaryText(effective[0]));
        Assert.Equal(2, Compaction.Effective(history[..2]).Count); // no summary: unchanged
    }

    [Fact]
    public void A_summary_is_not_a_prompt_the_user_typed()
    {
        var summary = Summary("text");

        Assert.True(summary.IsSummary);
        Assert.Equal("", summary.UserTypedText);
        Assert.False(ChatMessage.UserText("hi").IsSummary);
    }

    [Fact]
    public async Task Compacting_replaces_the_history_with_a_summary_made_without_tools()
    {
        var model = new ScriptedProvider(Say("User wants X. Edited a.txt."));
        var agent = NewAgent(model, [ChatMessage.UserText("do X"), Assistant("done"), ChatMessage.UserText("and Y"), Assistant("done too")]);
        var appended = new List<ChatMessage>();
        agent.MessageAppended += appended.Add;

        var done = await agent.CompactAsync("keep the file names");

        var request = model.Requests.Single();
        Assert.Null(request.Tools);
        Assert.Contains("[User] do X", request.Messages.Single().Text);
        Assert.Contains("keep the file names", request.Messages.Single().Text);
        Assert.Equal("User wants X. Edited a.txt.", done.Summary);
        var kept = Assert.Single(agent.History);
        Assert.True(kept.IsSummary);
        Assert.Same(kept, Assert.Single(appended));
    }

    [Fact]
    public async Task After_compacting_the_next_prompt_follows_the_summary_alone()
    {
        var model = new ScriptedProvider(Say("summary"), Say("ok"));
        var agent = NewAgent(model, [ChatMessage.UserText("a"), Assistant("b")]);

        await agent.CompactAsync(null);
        await foreach (var _ in agent.RunAsync("next")) { }

        var request = model.Requests[1];
        Assert.Equal(2, request.Messages.Count);
        Assert.True(request.Messages[0].IsSummary);
        Assert.Equal("next", request.Messages[1].Text);
        Assert.NotNull(request.Tools);
    }

    [Fact]
    public async Task An_empty_summary_leaves_the_history_alone()
    {
        var model = new ScriptedProvider(new ModelResponse(new ChatMessage(ChatRole.Assistant, []), "end_turn", new Usage(1, 0)));
        var agent = NewAgent(model, [ChatMessage.UserText("a"), Assistant("b")]);

        await Assert.ThrowsAsync<ModelProviderException>(() => agent.CompactAsync(null));

        Assert.Equal(2, agent.History.Count);
    }

    [Fact]
    public async Task A_long_history_is_compacted_before_the_next_prompt()
    {
        var model = new ScriptedProvider(Say("the summary"), Say("answer"));
        var big = new string('x', 3000);
        var agent = NewAgent(model, [ChatMessage.UserText(big), Assistant(big)], new AgentOptions("m", ContextWindow: 1000, CompactAt: 0.5));

        var events = new List<AgentEvent>();
        await foreach (var ev in agent.RunAsync("go on"))
            events.Add(ev);

        Assert.Equal([typeof(ContextCompacting), typeof(ContextCompacted), typeof(AgentTextDelta), typeof(ModelCallCompleted), typeof(AgentFinished)], events.Select(e => e.GetType()));
        var compacted = events.OfType<ContextCompacted>().Single();
        Assert.True(compacted.TokensAfter < compacted.TokensBefore);
        Assert.Equal("go on", model.Requests[1].Messages[^1].Text);
        Assert.True(model.Requests[1].Messages[0].IsSummary);
    }

    [Fact]
    public async Task A_short_history_or_a_disabled_window_is_left_alone()
    {
        var small = new ScriptedProvider(Say("answer"));
        await foreach (var _ in NewAgent(small, [ChatMessage.UserText("a"), Assistant("b")], new AgentOptions("m", ContextWindow: 100_000)).RunAsync("c")) { }
        Assert.Single(small.Requests);

        var off = new ScriptedProvider(Say("answer"));
        var big = new string('x', 100_000);
        await foreach (var _ in NewAgent(off, [ChatMessage.UserText(big), Assistant(big)], new AgentOptions("m", ContextWindow: 0)).RunAsync("c")) { }
        Assert.Single(off.Requests);
    }

    [Fact]
    public async Task A_run_that_fills_the_window_is_compacted_between_model_calls()
    {
        File.WriteAllText(Path.Combine(_dir.Path, "a.txt"), "hello");
        var toolCall = new ModelResponse(
            new ChatMessage(ChatRole.Assistant, [new ToolCallPart("c1", "Read", Json.Of($$"""{"file_path":"{{Json.Str(Path.Combine(_dir.Path, "a.txt"))}}"}"""))]),
            "tool_use", new Usage(900, 5)); // the model reports a nearly full context
        var model = new ScriptedProvider(toolCall, Say("the summary"), Say("finished"));
        var agent = NewAgent(model, [], new AgentOptions("m", ContextWindow: 1000, CompactAt: 0.8));

        var events = new List<AgentEvent>();
        await foreach (var ev in agent.RunAsync("read a.txt then answer"))
            events.Add(ev);

        Assert.Contains(events, e => e is ContextCompacted);
        Assert.Equal(3, model.Requests.Count);
        Assert.Null(model.Requests[1].Tools); // the summarizer
        Assert.Contains("[User] read a.txt then answer", model.Requests[1].Messages.Single().Text);
        var last = model.Requests[2];
        Assert.Single(last.Messages);
        Assert.True(last.Messages[0].IsSummary);
        Assert.IsType<AgentFinished>(events[^1]);
    }

    [Fact]
    public async Task A_window_too_small_for_any_round_does_not_loop_on_summaries()
    {
        var responses = Enumerable.Range(0, 20).Select(_ => Say("s")).ToArray();
        var model = new ScriptedProvider(responses);
        var agent = NewAgent(model, [ChatMessage.UserText("a"), Assistant("b")], new AgentOptions("m", ContextWindow: 1, CompactAt: 0.5));

        var compacted = 0;
        await foreach (var ev in agent.RunAsync("c"))
            compacted += ev is ContextCompacted ? 1 : 0;

        Assert.Equal(1, compacted); // the turn ended after one answer; no tool calls to compact between
        Assert.Equal(2, model.Requests.Count);
    }

    [Fact]
    public void A_very_long_conversation_keeps_its_start_and_its_end_for_the_summarizer()
    {
        var history = new List<ChatMessage> { ChatMessage.UserText("the original task") };
        for (var i = 0; i < 100; i++)
        {
            history.Add(Assistant($"step {i} " + new string('y', 2000)));
            history.Add(ChatMessage.UserText($"reply {i}"));
        }

        var text = Compaction.Render(history, maxChars: 20_000);

        Assert.StartsWith("[User] the original task", text);
        Assert.Contains("earlier messages left out", text);
        Assert.Contains("reply 99", text);
        Assert.DoesNotContain("reply 3\n", text);
        Assert.True(text.Length < 24_000);
    }

    [Fact]
    public void Tool_calls_and_results_are_rendered_short()
    {
        var call = new ChatMessage(ChatRole.Assistant, [new ToolCallPart("c1", "Shell", Json.Of("""{"command":"dotnet test"}"""))]);
        var result = new ChatMessage(ChatRole.User, [new ToolResultPart("c1", new string('z', 10_000), IsError: true)]);

        var text = Compaction.Render([call, result], 100_000);

        Assert.Contains("[Assistant called Shell {\"command\":\"dotnet test\"}]", text);
        Assert.Contains("[Tool error] zzz", text);
        Assert.True(text.Length < 2000);
    }

    [Fact]
    public void The_context_settings_come_from_the_config_with_sane_limits()
    {
        var config = new KairoConfig();
        config.MergeToml("[context]\nwindow = 50000\ncompact_at = 0.5\n", "test");
        Assert.Equal(50_000, config.EffectiveContextWindow);
        Assert.Equal(0.5, config.EffectiveCompactAt);

        var clamped = new KairoConfig();
        clamped.MergeToml("[context]\ncompact_at = 5\n", "test");
        Assert.Equal(0.95, clamped.EffectiveCompactAt);
        Assert.Equal(KairoConfig.DefaultContextWindow, clamped.EffectiveContextWindow);
    }
}

public class CompactionHostTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly string _project;
    private readonly SessionStore _store;
    private readonly SettingsService _settings;
    private readonly ConcurrentQueue<AgentEventDto> _events = new();
    private ScriptedProvider _model = new();

    public CompactionHostTests()
    {
        _project = Path.Combine(_dir.Path, "project");
        Directory.CreateDirectory(_project);
        _store = new SessionStore(Path.Combine(_dir.Path, "data.db"));
        _settings = new SettingsService(Path.Combine(_dir.Path, "user"), new InMemorySecretStore());
    }

    public void Dispose()
    {
        _store.Dispose();
        _dir.Dispose();
    }

    private AgentHost CreateHost() =>
        new(_store, _settings, ev => { _events.Enqueue(ev); return Task.CompletedTask; }, () => Task.CompletedTask, _ => _model);

    private string NewSession(AgentHost host)
    {
        _store.TouchWorkspace(_project);
        return host.Create(_project).Id;
    }

    private async Task<AgentEventDto> WaitFor(string type)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (_events.FirstOrDefault(e => e.Type == type) is { } found)
                return found;
            await Task.Delay(10);
        }
        throw new TimeoutException($"No '{type}' event. Got: {string.Join(", ", _events.Select(e => e.Type))}");
    }

    [Fact]
    public async Task Compacting_a_session_keeps_the_log_and_the_next_turn_starts_from_the_summary()
    {
        _model = new ScriptedProvider(Say("first answer"), Say("The user asked for a first thing."), Say("second answer"));
        var host = CreateHost();
        var id = NewSession(host);
        host.Send(id, "first thing");
        await WaitFor("turn_finished");
        _events.Clear();

        host.Compact(id, null);
        var compacted = await WaitFor("compacted");
        await WaitFor("turn_finished");

        Assert.Equal("The user asked for a first thing.", compacted.Text);
        Assert.Equal(["turn_started", "compacting", "compacted", "turn_finished"], _events.Select(e => e.Type));
        Assert.Equal(3, _store.LoadMessages(id).Count); // prompt, answer and the summary: nothing was removed

        _events.Clear();
        host.Send(id, "second thing");
        await WaitFor("turn_finished");

        var request = _model.Requests[^1];
        Assert.Equal(2, request.Messages.Count);
        Assert.True(request.Messages[0].IsSummary);
        var items = host.Open(id).Items;
        Assert.Equal(["user", "assistant", "compaction", "user", "assistant"], items.Select(i => i.Kind));
        Assert.Equal("The user asked for a first thing.", items[2].Text);
    }

    [Fact]
    public async Task A_compacted_session_survives_a_restart()
    {
        _model = new ScriptedProvider(Say("answer"), Say("summary text"), Say("later answer"));
        var host = CreateHost();
        var id = NewSession(host);
        host.Send(id, "first");
        await WaitFor("turn_finished");
        _events.Clear();
        host.Compact(id, null);
        await WaitFor("compacted");
        await WaitFor("turn_finished");
        _events.Clear();

        var restarted = CreateHost(); // loads the stored log again
        restarted.Send(id, "after restart");
        await WaitFor("turn_finished");

        var request = _model.Requests[^1];
        Assert.Equal(2, request.Messages.Count);
        Assert.True(request.Messages[0].IsSummary);
        Assert.Equal("after restart", request.Messages[1].Text);
    }

    [Fact]
    public void Nothing_to_compact_is_refused()
    {
        var host = CreateHost();
        var id = NewSession(host);

        var ex = Assert.Throws<Kairo.Core.Rpc.RpcException>(() => host.Compact(id, null));

        Assert.Contains("нечего", ex.Message);
    }
}
