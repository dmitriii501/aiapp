using Kairo.Core.Agents;
using Kairo.Core.Models;
using Kairo.Core.Permissions;
using Kairo.Core.Tests.Fakes;
using Kairo.Core.Tools;
using Kairo.Tools;
using static Kairo.Core.Tests.Fakes.ScriptedProvider;

namespace Kairo.Core.Tests;

public class SubagentTests : IDisposable
{
    private readonly TempDir _dir = new();

    public SubagentTests() => _dir.File("a.txt", "hello");

    public void Dispose() => _dir.Dispose();

    private Agent NewAgent(ScriptedProvider model, FakePrompt? prompt = null, PermissionMode mode = PermissionMode.Default, ToolContext? ctx = null) =>
        new(model, new ToolRegistry(BuiltinTools.All()), new PermissionEngine(PermissionRules.WithDefaults(), mode), prompt ?? new FakePrompt(PermissionAnswer.Allow),
            ctx ?? new ToolContext(_dir.Path), "system", new AgentOptions("m"));

    private static ModelResponse Task(string id, string type, string prompt = "find it") =>
        Call(id, "Task", $$"""{"description":"look around","prompt":"{{prompt}}","subagent_type":"{{type}}"}""");

    private static string[] ToolNames(ModelRequest request) => request.Tools!.Select(t => t.Name).Order().ToArray();

    private static async Task Run(Agent agent, string text = "go")
    {
        await foreach (var _ in agent.RunAsync(text)) { }
    }

    [Fact]
    public async Task A_subagent_works_alone_and_only_its_report_comes_back()
    {
        var model = new ScriptedProvider(
            Task("t1", "explore", "where is a.txt used?"),
            Call("r1", "Read", $$"""{"file_path":"{{Json.Str(Path.Combine(_dir.Path, "a.txt"))}}"}"""),
            Say("a.txt is read by nobody."),
            Say("The subagent says nobody."));
        var agent = NewAgent(model);

        await Run(agent);

        // Request 1 is the subagent's first call: fresh history with just the task, a smaller tool set, its own brief.
        var sub = model.Requests[1];
        Assert.Equal("where is a.txt used?", Assert.Single(sub.Messages).Text);
        Assert.Equal(["Glob", "Grep", "Read", "WebFetch", "WebSearch"], ToolNames(sub));
        Assert.Contains("research subagent", sub.System);
        // The parent's history holds the Task call and its result, nothing of what the subagent did.
        Assert.Equal(4, agent.History.Count);
        var result = agent.History[2].Parts.OfType<ToolResultPart>().Single();
        Assert.False(result.IsError);
        Assert.Contains("a.txt is read by nobody.", result.Content);
        Assert.Contains("1 tool call", result.Content);
        Assert.DoesNotContain("hello", result.Content);
    }

    [Fact]
    public async Task A_general_subagent_has_the_main_tools_but_cannot_start_subagents()
    {
        var model = new ScriptedProvider(Task("t1", "general"), Say("ok"), Say("done"));

        await Run(NewAgent(model));

        var names = ToolNames(model.Requests[1]);
        Assert.Contains("Write", names);
        Assert.Contains("Shell", names);
        Assert.DoesNotContain("Task", names);
        Assert.DoesNotContain("TodoWrite", names);
        Assert.DoesNotContain("ExitPlanMode", names);
    }

    [Fact]
    public async Task The_main_agent_keeps_its_own_tools_after_a_subagent_ran()
    {
        var model = new ScriptedProvider(Task("t1", "explore"), Say("first"), Task("t2", "general"), Say("second"), Say("done"));

        await Run(NewAgent(model));

        // Request 3 is the second subagent: it must see the main agent's full tool set, not the first subagent's.
        Assert.Contains("Write", ToolNames(model.Requests[3]));
        Assert.Equal(5, model.Requests.Count);
    }

    [Fact]
    public async Task A_subagents_actions_ask_the_user_through_the_same_prompt()
    {
        var path = Path.Combine(_dir.Path, "new.txt");
        var prompt = new FakePrompt(PermissionAnswer.Deny);
        var model = new ScriptedProvider(
            Task("t1", "general"),
            Call("w1", "Write", $$"""{"file_path":"{{Json.Str(path)}}","content":"x"}"""),
            Say("could not write"),
            Say("done"));

        await Run(NewAgent(model, prompt));

        Assert.Equal("Write", Assert.Single(prompt.Asked).Tool);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task Plan_mode_binds_subagents_too()
    {
        var path = Path.Combine(_dir.Path, "new.txt");
        var prompt = new FakePrompt(PermissionAnswer.Allow);
        var model = new ScriptedProvider(
            Task("t1", "general"),
            Call("w1", "Write", $$"""{"file_path":"{{Json.Str(path)}}","content":"x"}"""),
            Say("blocked"),
            Say("done"));

        await Run(NewAgent(model, prompt, PermissionMode.Plan));

        Assert.Empty(prompt.Asked);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task An_explore_subagent_cannot_write_even_if_it_tries()
    {
        var path = Path.Combine(_dir.Path, "new.txt");
        var model = new ScriptedProvider(
            Task("t1", "explore"),
            Call("w1", "Write", $$"""{"file_path":"{{Json.Str(path)}}","content":"x"}"""),
            Say("could not"),
            Say("done"));

        await Run(NewAgent(model));

        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task A_subagent_that_stops_at_its_step_limit_says_so()
    {
        var read = Call("r", "Read", $$"""{"file_path":"{{Json.Str(Path.Combine(_dir.Path, "a.txt"))}}"}""");
        var script = new List<ModelResponse> { Task("t1", "explore") };
        for (var i = 0; i < SubagentRunner.MaxModelCalls; i++)
            script.Add(read);
        script.Add(Say("done"));

        var agent = NewAgent(new ScriptedProvider([.. script]));
        await Run(agent);

        var result = agent.History.SelectMany(m => m.Parts).OfType<ToolResultPart>().First(r => r.ToolCallId == "t1");
        Assert.Contains("step limit", result.Content);
    }

    [Fact]
    public void Bad_task_input_is_reported_to_the_model_and_nothing_runs_without_a_runner()
    {
        var tool = new TaskTool();
        var ctx = new ToolContext(_dir.Path);

        Assert.Throws<ToolInputException>(() => tool.Assess(Json.Of("""{"description":"x","prompt":"  "}"""), ctx));
        var ex = Assert.Throws<ToolInputException>(() => tool.Assess(Json.Of("""{"description":"x","prompt":"p","subagent_type":"wizard"}"""), ctx));
        Assert.Contains("explore", ex.Message);

        var assessment = tool.Assess(Json.Of("""{"description":"look","prompt":"p"}"""), ctx);
        Assert.Equal(RiskLevel.Read, assessment.Risk);
        Assert.Equal("Task(explore: look)", assessment.Summary);
        var result = tool.InvokeAsync(Json.Of("""{"description":"look","prompt":"p"}"""), ctx, default).Result;
        Assert.True(result.IsError);
    }
}
