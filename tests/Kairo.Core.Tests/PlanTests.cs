using System.Text.Json;
using Kairo.Core.Agents;
using Kairo.Core.Context;
using Kairo.Core.Models;
using Kairo.Core.Permissions;
using Kairo.Core.Tests.Fakes;
using Kairo.Core.Tools;
using Kairo.Tools;
using static Kairo.Core.Tests.Fakes.ScriptedProvider;

namespace Kairo.Core.Tests;

public class TodoWriteTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private static JsonElement Input(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public async Task A_valid_list_is_summarised_normalised_and_needs_no_approval()
    {
        var tool = new TodoWriteTool();
        var ctx = new ToolContext(_dir.Path);
        var input = Input("""{"todos":[{"content":"  Read code ","status":"completed"},{"content":"Fix bug","status":"in_progress"},{"content":"Test","status":"pending"}]}""");

        var assessment = tool.Assess(input, ctx);
        var result = await tool.InvokeAsync(input, ctx, default);

        Assert.Equal("TodoWrite(1/3)", assessment.Summary);
        Assert.Equal(RiskLevel.Read, assessment.Risk);
        Assert.Equal("""[{"content":"Read code","status":"completed"},{"content":"Fix bug","status":"in_progress"},{"content":"Test","status":"pending"}]""", assessment.Detail);
        Assert.Contains("1 of 3 done", result.Content);
        var engine = new PermissionEngine(PermissionRules.WithDefaults(), PermissionMode.Plan);
        Assert.Equal(PermissionDecision.Allow, engine.Evaluate("TodoWrite", assessment, ctx).Decision); // even in plan mode
    }

    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{"todos":"nope"}""")]
    [InlineData("""{"todos":[{"content":"","status":"pending"}]}""")]
    [InlineData("""{"todos":[{"content":"x","status":"done"}]}""")]
    [InlineData("""{"todos":[{"status":"pending"}]}""")]
    public void Invalid_lists_are_rejected_with_a_message_for_the_model(string json)
    {
        Assert.Throws<ToolInputException>(() => new TodoWriteTool().Assess(Input(json), new ToolContext(_dir.Path)));
    }

    [Fact]
    public void A_list_with_too_many_tasks_is_rejected()
    {
        var tasks = string.Join(',', Enumerable.Range(0, TodoWriteTool.MaxTodos + 1).Select(i => $$"""{"content":"t{{i}}","status":"pending"}"""));

        Assert.Throws<ToolInputException>(() => new TodoWriteTool().Assess(Input($$"""{"todos":[{{tasks}}]}"""), new ToolContext(_dir.Path)));
    }
}

public class PlanModeTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private static ToolAssessment Plan() => new(RiskLevel.Read, ".", "ExitPlanMode", "the plan");

    private PermissionEngine Engine(PermissionMode mode, params string[] allow)
    {
        var rules = PermissionRules.WithDefaults();
        rules.Allow.AddRange(allow.Select(PermissionRule.Parse));
        return new PermissionEngine(rules, mode);
    }

    [Fact]
    public void Leaving_plan_mode_always_asks_and_only_in_plan_mode()
    {
        var ctx = new ToolContext(_dir.Path);

        Assert.Equal(PermissionDecision.Ask, Engine(PermissionMode.Plan).Evaluate("ExitPlanMode", Plan(), ctx).Decision);
        Assert.Equal(PermissionDecision.Ask, Engine(PermissionMode.Plan, "ExitPlanMode").Evaluate("ExitPlanMode", Plan(), ctx).Decision); // no allow rule skips it
        Assert.Equal(PermissionDecision.Deny, Engine(PermissionMode.Default).Evaluate("ExitPlanMode", Plan(), ctx).Decision);
    }

    [Fact]
    public void Always_allow_does_not_remember_plan_approvals()
    {
        var engine = Engine(PermissionMode.Plan);

        engine.AllowForSession("ExitPlanMode", Plan());

        Assert.Empty(engine.Rules.Allow);
    }

    private async Task<(Agent Agent, PermissionEngine Engine, FakePrompt Prompt)> RunPlan(PermissionAnswer answer)
    {
        var model = new ScriptedProvider(Call("c1", "ExitPlanMode", """{"plan":"1. Edit a.txt\n2. Run tests"}"""), Say("ok"));
        var engine = Engine(PermissionMode.Plan);
        var prompt = new FakePrompt(answer);
        var agent = new Agent(model, new ToolRegistry(BuiltinTools.All()), engine, prompt, new ToolContext(_dir.Path), "system", new AgentOptions("m"));
        await foreach (var _ in agent.RunAsync("plan it")) { }
        return (agent, engine, prompt);
    }

    [Theory]
    [InlineData(PermissionAnswer.AllowAlways, PermissionMode.AcceptEdits)]
    [InlineData(PermissionAnswer.Allow, PermissionMode.Default)]
    public async Task Approving_the_plan_leaves_plan_mode_in_the_chosen_way(PermissionAnswer answer, PermissionMode expected)
    {
        var (agent, engine, prompt) = await RunPlan(answer);

        Assert.Equal(expected, engine.Mode);
        Assert.Contains("Edit a.txt", prompt.Asked.Single().Call.Detail);
        var result = agent.History.SelectMany(m => m.Parts).OfType<ToolResultPart>().Single();
        Assert.False(result.IsError);
        Assert.Contains("approved", result.Content);
    }

    [Fact]
    public async Task Declining_the_plan_keeps_plan_mode_and_tells_the_model()
    {
        var (agent, engine, _) = await RunPlan(PermissionAnswer.Deny);

        Assert.Equal(PermissionMode.Plan, engine.Mode);
        var result = agent.History.SelectMany(m => m.Parts).OfType<ToolResultPart>().Single();
        Assert.True(result.IsError);
        Assert.Contains("Permission denied", result.Content);
    }

    [Fact]
    public void The_system_prompt_explains_plan_mode_only_in_plan_mode()
    {
        var normal = SystemPrompt.Build(_dir.Path, [], DateTimeOffset.Parse("2026-10-13"));
        var plan = SystemPrompt.Build(_dir.Path, [], DateTimeOffset.Parse("2026-10-13"), PermissionMode.Plan);

        Assert.DoesNotContain("plan mode", normal);
        Assert.Contains("ExitPlanMode", plan);
        Assert.Contains("TodoWrite", normal); // the task list is always encouraged
    }

    [Fact]
    public void A_plan_call_without_a_plan_is_an_input_error()
    {
        var tool = new ExitPlanModeTool();

        Assert.Throws<ToolInputException>(() => tool.Assess(JsonDocument.Parse("""{"plan":"   "}""").RootElement, new ToolContext(_dir.Path)));
        Assert.Throws<ToolInputException>(() => tool.Assess(JsonDocument.Parse("{}").RootElement, new ToolContext(_dir.Path)));
    }
}
