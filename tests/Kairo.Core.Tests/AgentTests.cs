using Kairo.Core.Agents;
using Kairo.Core.Models;
using Kairo.Core.Permissions;
using Kairo.Core.Tests.Fakes;
using Kairo.Core.Tools;
using Kairo.Tools;
using static Kairo.Core.Tests.Fakes.ScriptedProvider;

namespace Kairo.Core.Tests;

public class AgentTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private Agent CreateAgent(ScriptedProvider model, IPermissionPrompt prompt, PermissionMode mode = PermissionMode.Default, PermissionRules? rules = null) =>
        new(model, new ToolRegistry(BuiltinTools.All()), new PermissionEngine(rules ?? PermissionRules.WithDefaults(), mode), prompt,
            new ToolContext(_dir.Path), "system", new AgentOptions("test-model"));

    private static async Task<List<AgentEvent>> Run(Agent agent, string text, CancellationToken ct = default)
    {
        var events = new List<AgentEvent>();
        await foreach (var e in agent.RunAsync(text, ct: ct))
            events.Add(e);
        return events;
    }

    private static ToolResultPart LastResult(Agent agent) =>
        agent.History.Last(m => m.Parts.OfType<ToolResultPart>().Any()).Parts.OfType<ToolResultPart>().Last();

    [Fact]
    public async Task Reads_then_edits_a_file_with_approval()
    {
        _dir.File("hello.txt", "Hello, world\n");
        var model = new ScriptedProvider(
            Call("c1", "Read", """{"file_path":"hello.txt"}"""),
            Call("c2", "Edit", """{"file_path":"hello.txt","old_string":"world","new_string":"Kairo"}"""),
            Say("Done."));
        var prompt = new FakePrompt(PermissionAnswer.Allow);
        var agent = CreateAgent(model, prompt);

        var events = await Run(agent, "rename world to Kairo");

        Assert.Equal("Hello, Kairo\n", File.ReadAllText(Path.Combine(_dir.Path, "hello.txt")));
        Assert.Single(prompt.Asked); // reading inside the workspace needs no approval; editing does
        Assert.Equal("Edit", prompt.Asked[0].Tool);
        Assert.Equal("end_turn", Assert.IsType<AgentFinished>(events[^1]).Reason);

        // The model saw the Read output (with line numbers) in its second request.
        var secondRequest = model.Requests[1].Messages;
        var readResult = secondRequest[^1].Parts.OfType<ToolResultPart>().Single();
        Assert.Contains("1\tHello, world", readResult.Content);
        Assert.Equal(6, agent.History.Count); // user, call, result, call, result, answer
    }

    [Fact]
    public async Task Denied_call_returns_error_result_and_does_not_run()
    {
        var model = new ScriptedProvider(Call("c1", "Write", """{"file_path":"new.txt","content":"x"}"""), Say("ok"));
        var agent = CreateAgent(model, new FakePrompt(PermissionAnswer.Deny));

        var events = await Run(agent, "make a file");

        Assert.False(File.Exists(Path.Combine(_dir.Path, "new.txt")));
        var finished = events.OfType<ToolCallFinished>().Single();
        Assert.True(finished.Denied);
        Assert.Contains("Permission denied", LastResult(agent).Content);
    }

    [Fact]
    public async Task Plan_mode_denies_writes_without_asking()
    {
        var model = new ScriptedProvider(Call("c1", "Shell", """{"command":"echo hi"}"""), Say("ok"));
        var prompt = new FakePrompt(PermissionAnswer.Allow);

        await Run(CreateAgent(model, prompt, PermissionMode.Plan), "run it");

        Assert.Empty(prompt.Asked);
        Assert.Contains("plan mode", LastResult(model.Requests[1].Messages).Content);
    }

    [Fact]
    public async Task Always_allow_is_remembered_for_the_session()
    {
        var model = new ScriptedProvider(
            Call("c1", "Write", """{"file_path":"a.txt","content":"1"}"""),
            Say("one"),
            Call("c2", "Write", """{"file_path":"a.txt","content":"2"}"""),
            Say("two"));
        var prompt = new FakePrompt(PermissionAnswer.AllowAlways);
        var agent = CreateAgent(model, prompt);

        await Run(agent, "write 1");
        await Run(agent, "write 2");

        Assert.Single(prompt.Asked);
        Assert.Equal("2", File.ReadAllText(Path.Combine(_dir.Path, "a.txt")));
    }

    [Fact]
    public async Task Invalid_tool_json_is_reported_back_and_not_executed()
    {
        var model = new ScriptedProvider(Call("c1", "Write", """{"file_path":"x.txt","content":"unterminated"""), Say("sorry"));
        var prompt = new FakePrompt(PermissionAnswer.Allow);
        var agent = CreateAgent(model, prompt);

        await Run(agent, "write");

        Assert.Empty(prompt.Asked);
        Assert.False(File.Exists(Path.Combine(_dir.Path, "x.txt")));
        var result = LastResult(agent);
        Assert.True(result.IsError);
        Assert.StartsWith("{\"INVALID_JSON\":", result.Content);
    }

    [Fact]
    public async Task Truncated_response_does_not_run_its_tool_calls()
    {
        var model = new ScriptedProvider(Call("c1", "Write", """{"file_path":"t.txt","content":"x"}""", stopReason: "max_tokens"), Say("retrying"));
        var prompt = new FakePrompt(PermissionAnswer.Allow);
        var agent = CreateAgent(model, prompt);

        await Run(agent, "go");

        Assert.Empty(prompt.Asked);
        Assert.False(File.Exists(Path.Combine(_dir.Path, "t.txt")));
        Assert.Contains("max_tokens", LastResult(agent).Content);
    }

    [Fact]
    public async Task Unknown_tool_gets_an_error_result()
    {
        var model = new ScriptedProvider(Call("c1", "Teleport", "{}"), Say("ok"));
        var agent = CreateAgent(model, new FakePrompt(PermissionAnswer.Allow));

        await Run(agent, "go");

        Assert.Contains("Unknown tool", LastResult(agent).Content);
    }

    [Fact]
    public async Task Every_tool_call_in_history_has_a_result()
    {
        var model = new ScriptedProvider(
            new ModelResponse(new ChatMessage(ChatRole.Assistant,
            [
                new TextPart("Two things."),
                new ToolCallPart("a", "Glob", Json.Of("""{"pattern":"*.txt"}""")),
                new ToolCallPart("b", "Shell", Json.Of("""{"command":"echo hi"}""")),
            ]), "tool_use", new Usage(1, 1)),
            Say("done"));
        var agent = CreateAgent(model, new FakePrompt(PermissionAnswer.Deny));

        await Run(agent, "go");

        var callIds = agent.History.SelectMany(m => m.Parts.OfType<ToolCallPart>()).Select(c => c.Id).ToList();
        var resultIds = agent.History.SelectMany(m => m.Parts.OfType<ToolResultPart>()).Select(r => r.ToolCallId).ToList();
        Assert.Equal(callIds, resultIds);
        // Both results go back in a single user message.
        Assert.Equal(2, agent.History[2].Parts.Count);
    }

    [Fact]
    public void History_repair_answers_dangling_tool_calls()
    {
        IReadOnlyList<ChatMessage> history =
        [
            ChatMessage.UserText("go"),
            new ChatMessage(ChatRole.Assistant, [new ToolCallPart("x", "Read", Json.Of("{}"))]),
        ];

        var repaired = HistoryRepair.Repair(history);

        Assert.Equal(3, repaired.Count);
        Assert.Equal("x", repaired[2].Parts.OfType<ToolResultPart>().Single().ToolCallId);
    }

    private static ToolResultPart LastResult(IReadOnlyList<ChatMessage> messages) =>
        messages.Last(m => m.Parts.OfType<ToolResultPart>().Any()).Parts.OfType<ToolResultPart>().Last();
}
