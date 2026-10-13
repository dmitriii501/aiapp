using Kairo.Core.Agents;
using Kairo.Core.Config;
using Kairo.Core.Hooks;
using Kairo.Core.Models;
using Kairo.Core.Permissions;
using Kairo.Core.Tests.Fakes;
using Kairo.Core.Tools;
using Kairo.Tools;
using static Kairo.Core.Tests.Fakes.ScriptedProvider;

namespace Kairo.Core.Tests;

internal sealed class FakeHooks(Func<HookInput, HookOutcome> respond, params HookEvent[] events) : IHooks
{
    public List<HookInput> Calls { get; } = [];

    public bool Has(HookEvent hookEvent) => events.Contains(hookEvent);

    public Task<HookOutcome> RunAsync(HookInput input, CancellationToken ct)
    {
        Calls.Add(input);
        return Task.FromResult(respond(input));
    }
}

public class HookAgentTests : IDisposable
{
    private readonly TempDir _dir = new();

    public HookAgentTests() => _dir.File("a.txt", "hello");

    public void Dispose() => _dir.Dispose();

    private Agent NewAgent(ScriptedProvider model, IHooks hooks, FakePrompt? prompt = null) =>
        new(model, new ToolRegistry(BuiltinTools.All()), new PermissionEngine(PermissionRules.WithDefaults(), PermissionMode.Default), prompt ?? new FakePrompt(PermissionAnswer.Allow),
            new ToolContext(_dir.Path), "system", new AgentOptions("m")) { Hooks = hooks };

    private string ReadCall => $$"""{"file_path":"{{Json.Str(Path.Combine(_dir.Path, "a.txt"))}}"}""";

    private static async Task<List<AgentEvent>> Run(Agent agent, string text = "go")
    {
        var events = new List<AgentEvent>();
        await foreach (var ev in agent.RunAsync(text))
            events.Add(ev);
        return events;
    }

    [Fact]
    public async Task A_pre_tool_hook_can_block_a_call_and_the_model_is_told_why()
    {
        var hooks = new FakeHooks(i => i.ToolName == "Read" ? new HookOutcome(true, "reading is off today") : HookOutcome.Nothing, HookEvent.PreToolUse);
        var model = new ScriptedProvider(Call("c1", "Read", ReadCall), Say("ok"));
        var agent = NewAgent(model, hooks);

        var events = await Run(agent);

        var finished = events.OfType<ToolCallFinished>().Single();
        Assert.True(finished.Denied);
        Assert.Contains("Blocked by a hook: reading is off today", finished.Result.Content);
        Assert.DoesNotContain("hello", finished.Result.Content);
        Assert.Equal("Read", hooks.Calls.Single().ToolName);
    }

    [Fact]
    public async Task A_pre_tool_hook_cannot_allow_what_the_user_would_be_asked_about()
    {
        var path = Path.Combine(_dir.Path, "new.txt");
        var prompt = new FakePrompt(PermissionAnswer.Deny);
        var hooks = new FakeHooks(_ => HookOutcome.Nothing, HookEvent.PreToolUse);
        var model = new ScriptedProvider(Call("c1", "Write", $$"""{"file_path":"{{Json.Str(path)}}","content":"x"}"""), Say("ok"));

        await Run(NewAgent(model, hooks, prompt));

        Assert.Single(prompt.Asked);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task A_post_tool_hook_can_add_its_message_to_the_result()
    {
        var hooks = new FakeHooks(i => new HookOutcome(true, "format check failed"), HookEvent.PostToolUse);
        var model = new ScriptedProvider(Call("c1", "Read", ReadCall), Say("ok"));
        var agent = NewAgent(model, hooks);

        await Run(agent);

        var result = agent.History.SelectMany(m => m.Parts).OfType<ToolResultPart>().Single();
        Assert.Contains("hello", result.Content);
        Assert.EndsWith("[hook] format check failed", result.Content);
        Assert.Contains("hello", hooks.Calls.Single().ToolResponse);
    }

    [Fact]
    public async Task A_prompt_hook_can_stop_the_prompt_before_the_model_sees_it()
    {
        var hooks = new FakeHooks(_ => new HookOutcome(true, "no secrets in prompts"), HookEvent.UserPromptSubmit);
        var model = new ScriptedProvider(Say("never"));
        var agent = NewAgent(model, hooks);

        var events = await Run(agent, "my password is 1234");

        Assert.Empty(model.Requests);
        Assert.Empty(agent.History);
        Assert.Contains("no secrets in prompts", events.OfType<HookNotice>().Single().Text);
        Assert.Equal("hook_blocked", events.OfType<AgentFinished>().Single().Reason);
    }

    [Fact]
    public async Task What_a_prompt_hook_prints_reaches_the_model_as_a_hidden_note()
    {
        var hooks = new FakeHooks(_ => new HookOutcome(false, Context: "Current branch: main"), HookEvent.UserPromptSubmit);
        var model = new ScriptedProvider(Say("ok"));
        var agent = NewAgent(model, hooks);

        await Run(agent, "what branch?");

        var message = model.Requests[0].Messages[0];
        Assert.Contains("Current branch: main", message.Text);
        Assert.Equal("what branch?", message.UserTypedText);
    }

    [Fact]
    public async Task A_stop_hook_sends_the_model_back_to_work_a_limited_number_of_times()
    {
        var hooks = new FakeHooks(_ => new HookOutcome(true, "tests still fail"), HookEvent.Stop);
        var model = new ScriptedProvider(Say("done"), Say("fixed?"), Say("fixed!"), Say("never asked"));
        var agent = NewAgent(model, hooks);

        var events = await Run(agent);

        Assert.Equal(3, model.Requests.Count); // the answer plus two continuations
        Assert.Equal(2, events.OfType<HookNotice>().Count());
        Assert.Contains("tests still fail", model.Requests[1].Messages[^1].Text);
        Assert.False(Transcript(model.Requests[2].Messages[^1])); // the hook's note is not a user prompt
    }

    private static bool Transcript(ChatMessage message) => Kairo.Host.Transcript.IsPrompt(message);

    [Fact]
    public async Task Subagents_run_tool_hooks_but_not_prompt_or_stop_hooks()
    {
        var hooks = new FakeHooks(_ => HookOutcome.Nothing, HookEvent.PreToolUse, HookEvent.UserPromptSubmit, HookEvent.Stop);
        var model = new ScriptedProvider(
            Call("t1", "Task", """{"description":"d","prompt":"look","subagent_type":"explore"}"""),
            Call("r1", "Read", ReadCall),
            Say("report"),
            Say("done"));

        await Run(NewAgent(model, hooks));

        Assert.Contains(hooks.Calls, c => c.Event == HookEvent.PreToolUse && c.ToolName == "Read");
        Assert.Single(hooks.Calls, c => c.Event == HookEvent.UserPromptSubmit);
        Assert.Single(hooks.Calls, c => c.Event == HookEvent.Stop);
    }
}

public class HookConfigTests
{
    [Fact]
    public void Hooks_are_read_from_the_users_file_and_never_from_a_project()
    {
        using var dir = new TempDir();
        var user = Path.Combine(dir.Path, "user");
        var project = Path.Combine(dir.Path, "project");
        Directory.CreateDirectory(user);
        Directory.CreateDirectory(Path.Combine(project, ".kairo"));
        File.WriteAllText(Path.Combine(user, "config.toml"), "[[hooks]]\nevent = \"PreToolUse\"\nmatcher = \"Shell\"\ncommand = \"exit 0\"\ntimeout = 5\n\n[[hooks]]\nevent = \"stop\"\ncommand = \"exit 0\"\n");
        File.WriteAllText(Path.Combine(project, ".kairo", "config.toml"), "[[hooks]]\nevent = \"PreToolUse\"\ncommand = \"calc.exe\"\n");

        var hooks = KairoConfig.Load(project, user).Hooks;

        Assert.Equal(2, hooks.Count);
        Assert.Equal(new HookConfig(HookEvent.PreToolUse, "Shell", "exit 0", 5), hooks[0]);
        Assert.Equal(HookEvent.Stop, hooks[1].Event);
        Assert.DoesNotContain(hooks, h => h.Command.Contains("calc"));
    }

    [Theory]
    [InlineData("[[hooks]]\nevent = \"OnFire\"\ncommand = \"x\"\n", "unknown hook event")]
    [InlineData("[[hooks]]\nevent = \"Stop\"\n", "no command")]
    public void A_broken_hook_is_reported(string toml, string message)
    {
        var config = new KairoConfig();

        var ex = Assert.Throws<FormatException>(() => config.MergeToml(toml, "test"));

        Assert.Contains(message, ex.Message);
    }

    [Theory]
    [InlineData("Shell", "Shell", true)]
    [InlineData("Edit|Write", "Write", true)]
    [InlineData("Edit|Write", "Read", false)]
    [InlineData("mcp__*", "mcp__github__create_issue", true)]
    [InlineData("mcp__github__*", "mcp__slack__post", false)]
    [InlineData("", "Anything", true)]
    [InlineData(null, "Anything", true)]
    [InlineData("Shell", null, false)]
    public void Matchers_select_tools_by_name(string? matcher, string? tool, bool expected)
    {
        Assert.Equal(expected, new HookConfig(HookEvent.PreToolUse, matcher, "x").Matches(tool));
    }
}

public class ProcessHooksTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private HookInput Input(string tool = "Shell", HookEvent e = HookEvent.PreToolUse) =>
        new(e, _dir.Path, tool, Json.Of("""{"command":"git push"}"""));

    [Fact]
    public async Task A_hook_that_exits_with_2_blocks_and_its_error_output_is_the_reason()
    {
        var hooks = new ProcessHooks([new HookConfig(HookEvent.PreToolUse, "Shell", "[Console]::Error.WriteLine('pushing is not allowed'); exit 2")]);

        var outcome = await hooks.RunAsync(Input(), default);

        Assert.True(outcome.Blocked);
        Assert.Equal("pushing is not allowed", outcome.Message);
    }

    [Fact]
    public async Task The_hook_gets_the_event_as_json_on_standard_input()
    {
        var script = "$j = [Console]::In.ReadToEnd() | ConvertFrom-Json; if ($j.tool_name -eq 'Shell' -and $j.tool_input.command -like 'git push*' -and $j.cwd) { [Console]::Error.WriteLine('saw ' + $j.hook_event_name); exit 2 }";
        var hooks = new ProcessHooks([new HookConfig(HookEvent.PreToolUse, null, script)]);

        var outcome = await hooks.RunAsync(Input(), default);

        Assert.True(outcome.Blocked);
        Assert.Equal("saw PreToolUse", outcome.Message);
    }

    [Fact]
    public async Task Success_other_failures_and_other_tools_do_not_block()
    {
        var hooks = new ProcessHooks(
        [
            new HookConfig(HookEvent.PreToolUse, "Shell", "Write-Output 'branch: main'"),
            new HookConfig(HookEvent.PreToolUse, "Shell", "exit 1"),
            new HookConfig(HookEvent.PreToolUse, "Edit", "exit 2"),
            new HookConfig(HookEvent.Stop, null, "exit 2"),
        ]);

        var outcome = await hooks.RunAsync(Input(), default);

        Assert.False(outcome.Blocked);
        Assert.Equal("branch: main", outcome.Context);
    }

    [Fact]
    public async Task A_hook_that_takes_too_long_is_stopped_and_does_not_block()
    {
        var hooks = new ProcessHooks([new HookConfig(HookEvent.PreToolUse, null, "Start-Sleep -Seconds 60; exit 2", TimeoutSeconds: 1)]);

        var started = DateTime.UtcNow;
        var outcome = await hooks.RunAsync(Input(), default);

        Assert.False(outcome.Blocked);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(20));
    }
}
