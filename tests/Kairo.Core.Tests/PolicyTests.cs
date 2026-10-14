using System.Text.Json;
using Kairo.Core.Decisions;
using Kairo.Core.Tests.Fakes;
using Kairo.Core.Tools;
using Kairo.PcControl;
using static Kairo.Core.Tests.Fakes.FakeDesktop;
using static Kairo.Core.Tests.Fakes.ScriptedDecisionModel;

namespace Kairo.Core.Tests;

public sealed class PolicyParserTests
{
    private static readonly PcGuard Guard = new(["notepad.exe"]);

    private const string Full = """
        {
          "$schema": "kairo.policy/v0",
          "goal": "Save a note as report.txt",
          "target": { "app": "Notepad" },
          "texts": { "name": "report.txt", "body": "hello" },
          "steps": [
            { "id": "open", "goal": "Open the Save As dialog", "until": "A Save As dialog is open", "hotkeys": ["Ctrl+Shift+S"] },
            { "goal": "Type the name and save", "until": "The title shows report.txt", "texts": ["name"], "hotkeys": ["enter"], "max_steps": 8 }
          ],
          "reflexes": [
            { "name": "keep", "when": { "window_title_contains": "Confirm", "element": { "role": "button", "name_contains": "Yes" } }, "do": { "invoke": { "role": "button", "name_contains": "Yes" } }, "max_fires": 2 },
            { "when": { "window_title_contains": "Error" }, "do": { "hotkey": "Escape" } }
          ],
          "limits": { "max_steps": 60, "max_seconds": 90 }
        }
        """;

    private static PolicyParse Parse(string json) => PolicyParser.Parse(JsonDocument.Parse(json).RootElement, Guard);

    [Fact]
    public void A_complete_policy_is_read_into_typed_data()
    {
        var parse = Parse(Full);

        Assert.True(parse.Ok, string.Join("; ", parse.Errors));
        var policy = parse.Policy!;
        Assert.Equal(("Save a note as report.txt", "notepad.exe"), (policy.Goal, policy.App));
        Assert.Equal(["open", "step2"], policy.Steps.Select(s => s.Id));
        Assert.Equal(["ctrl+shift+s"], policy.Steps[0].Hotkeys);
        Assert.Equal(["name"], policy.Steps[1].Texts);
        Assert.Equal(8, policy.Steps[1].MaxSteps);
        Assert.Equal(["keep", "reflex2"], policy.Reflexes.Select(r => r.Name));
        Assert.Equal(2, policy.Reflexes[0].MaxFires);
        Assert.Equal("escape", policy.Reflexes[1].Do.Hotkey);
        Assert.Equal(new PolicyLimits(60, 90), policy.Limits);
    }

    [Fact]
    public void Every_problem_is_listed_at_once_so_the_model_can_fix_them_together()
    {
        var parse = Parse("""
            {
              "goal": "",
              "target": { "app": "powershell.exe" },
              "texts": { "a": "x" },
              "steps": [
                { "goal": "no until", "texts": ["missing"], "hotkeys": ["win+r", "ctrl+s"] },
                { "id": "dup", "goal": "g", "until": "u" },
                { "id": "dup", "goal": "g", "until": "u" }
              ],
              "reflexes": [ { "when": {}, "do": { "invoke": {}, "hotkey": "enter" } } ]
            }
            """);

        Assert.False(parse.Ok);
        var all = string.Join("\n", parse.Errors);
        Assert.Contains("goal is required", all);
        Assert.Contains("cannot be controlled", all);
        Assert.Contains("steps[1].until is required", all);
        Assert.Contains("\"missing\", which is not defined", all);
        Assert.Contains("\"win+r\" is not a shortcut", all);
        Assert.Contains("Step ids must be unique", all);
        Assert.Contains("reflexes[1]", all);
    }

    [Theory]
    [InlineData("""{"goal":"g","steps":[{"goal":"a","until":"b"}]}""", "target.app is required")]
    [InlineData("""{"goal":"g","target":{"app":"chrome.exe"},"steps":[{"goal":"a","until":"b"}]}""", "not in the list")]
    [InlineData("""{"goal":"g","target":{"app":"notepad.exe"}}""", "steps is required")]
    [InlineData("""{"goal":"g","target":{"app":"notepad.exe"},"steps":[]}""", "steps is required")]
    [InlineData("""{"$schema":"kairo.policy/v9","goal":"g","target":{"app":"notepad.exe"},"steps":[{"goal":"a","until":"b"}]}""", "$schema must be")]
    [InlineData("""{"goal":"g","target":{"app":"notepad.exe"},"steps":[{"goal":"a","until":"b"}],"reflexes":[{"when":{"element":{}},"do":{"hotkey":"enter"}}]}""", "is empty")]
    [InlineData("""{"goal":"g","target":{"app":"notepad.exe"},"steps":[{"goal":"a","until":"b"}],"reflexes":[{"when":{"window_title_contains":"x"},"do":{}}]}""", "exactly one of invoke or hotkey")]
    [InlineData("""{"goal":"g","target":{"app":"notepad.exe"},"steps":[{"goal":"a","until":"b"}],"texts":{"t":""}}""", "texts.t")]
    public void Each_kind_of_mistake_is_refused_with_a_reason(string json, string message)
    {
        var parse = Parse(json);

        Assert.False(parse.Ok);
        Assert.Contains(parse.Errors, e => e.Contains(message, StringComparison.Ordinal));
    }

    [Fact]
    public void A_policy_that_is_not_an_object_is_refused()
    {
        Assert.False(PolicyParser.Parse(JsonDocument.Parse("[1]").RootElement, Guard).Ok);
    }

    [Fact]
    public void Too_many_steps_or_reflexes_are_refused_and_limits_are_kept_in_range()
    {
        var steps = string.Join(",", Enumerable.Range(0, 31).Select(i => $$"""{"goal":"g","until":"u"}"""));
        var many = Parse($$"""{"goal":"g","target":{"app":"notepad.exe"},"steps":[{{steps}}]}""");
        var limited = Parse("""{"goal":"g","target":{"app":"notepad.exe"},"steps":[{"goal":"a","until":"b","max_steps":999}],"limits":{"max_steps":99999,"max_seconds":1}}""");

        Assert.Contains(many.Errors, e => e.Contains("At most 30 steps"));
        Assert.Equal(40, limited.Policy!.Steps[0].MaxSteps);
        Assert.Equal(new PolicyLimits(300, 5), limited.Policy.Limits);
    }

    [Fact]
    public void The_description_shows_the_user_everything_that_will_run()
    {
        var text = Parse(Full).Policy!.Describe();

        Assert.Contains("Program: notepad.exe", text);
        Assert.Contains("1. Open the Save As dialog  [done when: A Save As dialog is open]", text);
        Assert.Contains("texts: name = \"report.txt\"", text);
        Assert.Contains("keys: ctrl+shift+s", text);
        Assert.Contains("keep: when window title contains \"Confirm\" and there is an element (role button, name contains \"Yes\") -> click the element (role button, name contains \"Yes\")", text);
        Assert.Contains("-> press escape", text);
        Assert.Contains("Limits: 60 steps, 90 s", text);
    }
}

public sealed class ReflexTests
{
    private static Observation Screen(string title, params UiElement[] elements) =>
        new(4, new WindowInfo(1, 1, "notepad.exe", title, true, false), elements, false);

    private static Reflex ConfirmYes(int fires = 5) => new("yes", new ReflexCondition("Confirm", new ElementSelector("button", NameContains: "yes")),
        new ReflexAction(new ElementSelector("button", NameContains: "yes")), fires);

    [Fact]
    public void A_reflex_fires_when_the_title_and_the_element_are_there_and_names_the_element_of_this_look()
    {
        var set = new ReflexSet([ConfirmYes()]);

        var hit = set.Match(Screen("Confirm Save As", El("e1", "button", "No", UiCapability.Invoke), El("e2", "button", "Yes", UiCapability.Invoke)));

        Assert.NotNull(hit);
        Assert.Equal(new DesktopAction(DesktopActionKind.Invoke, 4, "e2"), hit.Value.Action);
        Assert.Equal(1, set.Fired("yes"));
    }

    [Theory]
    [InlineData("Untitled - Notepad")]
    [InlineData("")]
    public void It_stays_quiet_when_the_window_is_not_the_one_it_knows(string title)
    {
        var set = new ReflexSet([ConfirmYes()]);

        Assert.Null(set.Match(Screen(title, El("e1", "button", "Yes", UiCapability.Invoke))));
        Assert.Equal(0, set.TotalFired);
    }

    [Fact]
    public void It_stays_quiet_when_the_element_is_missing_or_disabled()
    {
        var set = new ReflexSet([ConfirmYes()]);

        Assert.Null(set.Match(Screen("Confirm", El("e1", "button", "Cancel", UiCapability.Invoke))));
        Assert.Null(set.Match(Screen("Confirm", El("e1", "button", "Yes", UiCapability.Invoke, enabled: false))));
    }

    [Fact]
    public void A_reflex_that_keeps_firing_stops_after_its_limit()
    {
        var set = new ReflexSet([ConfirmYes(fires: 2)]);
        var screen = Screen("Confirm", El("e1", "button", "Yes", UiCapability.Invoke));

        Assert.NotNull(set.Match(screen));
        Assert.NotNull(set.Match(screen));
        Assert.Null(set.Match(screen));
        Assert.Equal(2, set.Fired("yes"));
    }

    [Fact]
    public void A_hotkey_reflex_and_a_title_inside_a_dialog_both_work()
    {
        var set = new ReflexSet([new Reflex("esc", new ReflexCondition("Error"), new ReflexAction(Hotkey: "escape"))]);

        var viaDialog = set.Match(Screen("Untitled - Notepad", El("e1", "dialog", "Error: disk full")));

        Assert.NotNull(viaDialog);
        Assert.Equal((DesktopActionKind.Hotkey, "escape"), (viaDialog.Value.Action.Kind, viaDialog.Value.Action.Text));
    }

    [Fact]
    public void Selectors_compare_without_case_and_all_conditions_must_hold()
    {
        var selector = new ElementSelector("Button", NameEquals: "OK", AutomationId: "ok1");

        Assert.True(selector.Matches(El("e1", "button", "ok", UiCapability.Invoke) with { AutomationId = "OK1" }));
        Assert.False(selector.Matches(El("e1", "button", "ok", UiCapability.Invoke)));
        Assert.False(selector.Matches(El("e1", "edit", "ok", UiCapability.Invoke) with { AutomationId = "ok1" }));
        Assert.True(new ElementSelector().IsEmpty);
    }
}

public sealed class PolicyRunnerTests
{
    private readonly FakeDesktop _desktop = new();
    private readonly PcGuard _guard = new(["notepad.exe"]);

    private static PcPolicy Policy(params PolicyStep[] steps) =>
        new("goal", "notepad.exe", new Dictionary<string, string> { ["body"] = "hello", ["name"] = "report.txt" }, steps, [], new PolicyLimits(100, 60));

    private static PolicyStep Step(string id, string[]? texts = null, string[]? hotkeys = null, int max = 10) =>
        new(id, $"goal of {id}", $"until {id}", texts ?? [], hotkeys ?? [], max);

    private void Editor()
    {
        _desktop.Elements = [El("e1", "edit", "Text editor", UiCapability.SetValue), El("e2", "button", "Save", UiCapability.Invoke)];
        _desktop.OnPerform = a =>
        {
            if (a.Kind == DesktopActionKind.SetValue)
                _desktop.Elements[0] = _desktop.Elements[0] with { Value = a.Text };
            return ActionOutcome.Success();
        };
    }

    [Fact]
    public async Task The_steps_run_in_order_each_with_its_own_texts_and_the_run_ends_done()
    {
        Editor();
        var step = 0;
        var model = new ScriptedDecisionModel((req, _) =>
        {
            var goal = req.State.Value.GetProperty("subgoal").GetString()!;
            // First look at each step: do the typing if it is offered; at the second look say it is done.
            var typed = _desktop.Elements[0].Value is { Length: > 0 };
            return goal.EndsWith("one") && !typed ? Pick(req, Starts("type:e1:body"))
                : goal.EndsWith("two") && _desktop.Elements[0].Value != "report.txt" ? Pick(req, Starts("type:e1:name"))
                : Pick(req, Key("done"), achieved: 0.97 + 0 * step++);
        });
        var policy = Policy(Step("one", ["body"]) with { Goal = "goal one" }, Step("two", ["name"]) with { Goal = "goal two" });

        var report = await new PolicyRunner(_desktop, _guard, model).RunAsync(policy, 0x10, default);

        Assert.Equal(PursueStatus.Done, report.Status);
        Assert.Equal((2, 2), (report.Completed, report.Total));
        Assert.Equal(["one", "two"], report.Steps.Select(s => s.Id));
        Assert.Equal(["hello", "report.txt"], _desktop.Performed.Select(a => a.Text));
        // The second step's model never saw the first step's text offered: each step offers only its own texts.
        Assert.DoesNotContain(((ChoiceQuestion)model.Requests.Last().Questions["next"]).Options, o => o.Key.EndsWith(":body"));
    }

    [Fact]
    public async Task The_first_step_that_does_not_finish_stops_the_run_and_hands_the_screen_back()
    {
        Editor();
        var model = new ScriptedDecisionModel((req, _) => Pick(req, Starts("type:"), confidence: 0.3));

        var report = await new PolicyRunner(_desktop, _guard, model).RunAsync(Policy(Step("one", ["body"]), Step("two")), 0x10, default);

        Assert.Equal(PursueStatus.Escalated, report.Status);
        Assert.Equal("one", report.FailedStep);
        Assert.Contains("step \"one\"", report.Reason);
        Assert.Single(report.Steps);
        Assert.NotNull(report.Last);
        Assert.NotNull(report.Pending);
        Assert.Empty(_desktop.Performed);
    }

    [Fact]
    public async Task A_reflex_answers_a_known_window_without_asking_the_model()
    {
        _desktop.Elements = [El("e1", "button", "Yes", UiCapability.Invoke)];
        _desktop.Window = _desktop.Window with { Title = "Confirm overwrite" };
        _desktop.OnPerform = _ =>
        {
            _desktop.Window = _desktop.Window with { Title = "Untitled - Notepad" };
            _desktop.Elements = [El("e1", "edit", "Text editor", UiCapability.SetValue, value: "saved")];
            return ActionOutcome.Success();
        };
        var model = new ScriptedDecisionModel((req, _) => Pick(req, Key("done"), achieved: 0.99));
        var reflex = new Reflex("yes", new ReflexCondition("Confirm", new ElementSelector("button", NameEquals: "Yes")), new ReflexAction(new ElementSelector("button", NameEquals: "Yes")));
        var policy = Policy(Step("save")) with { Reflexes = [reflex] };

        var report = await new PolicyRunner(_desktop, _guard, model).RunAsync(policy, 0x10, default);

        Assert.Equal(PursueStatus.Done, report.Status);
        Assert.Equal(1, report.ReflexFires);
        Assert.Equal(DesktopActionKind.Invoke, Assert.Single(_desktop.Performed).Kind);
        Assert.Equal(1, model.Requests.Count); // only the "is it done" look after the reflex, none for the dialog
        var first = report.Steps[0].Report.Steps[0];
        Assert.StartsWith("reflex yes:", first.Action);
        Assert.Equal(0, first.DecideMs);
    }

    [Fact]
    public async Task A_window_of_another_program_is_refused_whatever_the_model_passed()
    {
        Editor();
        _desktop.Window = _desktop.Window with { Process = "calc.exe" };

        var report = await new PolicyRunner(_desktop, new PcGuard(["notepad.exe", "calc.exe"]), new ScriptedDecisionModel((r, _) => Pick(r, Key("stuck"))))
            .RunAsync(Policy(Step("one")), 0x10, default);

        Assert.Equal(PursueStatus.Failed, report.Status);
        Assert.Contains("the policy is for notepad.exe", report.Reason);
    }

    [Fact]
    public async Task The_whole_policy_shares_one_step_budget()
    {
        var on = false;
        _desktop.Elements = [El("e1", "check box", "Wrap", UiCapability.Toggle, isChecked: false)];
        _desktop.OnPerform = _ =>
        {
            on = !on;
            _desktop.Elements[0] = _desktop.Elements[0] with { Checked = on };
            return ActionOutcome.Success();
        };
        var model = new ScriptedDecisionModel((req, _) => Pick(req, Starts("toggle:")));
        var policy = Policy(Step("a", max: 3), Step("b", max: 3)) with { Limits = new PolicyLimits(5, 60) };

        var report = await new PolicyRunner(_desktop, _guard, model).RunAsync(policy, 0x10, default);

        Assert.Equal(PursueStatus.StepLimit, report.Status);
        Assert.Equal("a", report.FailedStep); // the first step already ran out of its own limit
    }

    [Fact]
    public async Task The_time_limit_ends_the_run_with_a_reason_instead_of_hanging()
    {
        Editor();
        var model = new HangingModel();
        var policy = Policy(Step("one")) with { Limits = new PolicyLimits(100, 1) };
        var started = DateTime.UtcNow;

        var report = await new PolicyRunner(_desktop, _guard, model).RunAsync(policy, 0x10, default);

        Assert.Equal(PursueStatus.StepLimit, report.Status);
        Assert.Contains("time limit", report.Reason);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Cancelling_the_call_is_an_abort_not_a_time_limit()
    {
        Editor();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        var report = await new PolicyRunner(_desktop, _guard, new HangingModel()).RunAsync(Policy(Step("one")), 0x10, cts.Token);

        Assert.Equal(PursueStatus.Aborted, report.Status);
    }

    private sealed class HangingModel : IDecisionModel
    {
        public string Id => "hanging";

        public async Task<DecisionResult> DecideAsync(DecisionRequest request, CancellationToken ct)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            throw new InvalidOperationException();
        }
    }
}

public sealed class PcRunPolicyToolTests
{
    private readonly FakeDesktop _desktop = new();

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    private ITool Tool(ScriptedDecisionModel? model = null)
    {
        var dir = Directory.CreateTempSubdirectory("kairo-pol-").FullName;
        File.WriteAllText(Path.Combine(dir, "config.toml"), "[pc]\nenabled = true\napps = [\"notepad.exe\"]");
        var config = Kairo.Core.Config.KairoConfig.Load(dir, dir, includeProject: false);
        return PcTools.For(config, () => model, _desktop).Single(t => t.Spec.Name == "PcRunPolicy");
    }

    private const string Policy = """
        { "goal": "Write hello", "target": {"app": "notepad.exe"}, "texts": {"body": "hello"},
          "steps": [ {"id": "type", "goal": "Type the greeting", "until": "The editor shows hello", "texts": ["body"]} ] }
        """;

    private ToolContext Ctx => new(Path.GetTempPath());

    private void Editor()
    {
        _desktop.Elements = [El("e1", "edit", "Text editor", UiCapability.SetValue)];
        _desktop.OnPerform = a =>
        {
            _desktop.Elements[0] = _desktop.Elements[0] with { Value = a.Text };
            return ActionOutcome.Success();
        };
    }

    [Fact]
    public void The_user_is_shown_the_whole_policy_and_it_asks_even_without_confirmations()
    {
        var tool = Tool();

        var assessment = tool.Assess(Json($$"""{"policy": {{Policy}} }"""), Ctx);

        Assert.Equal(Kairo.Core.Tools.RiskLevel.System, assessment.Risk);
        Assert.Equal("notepad.exe", assessment.Subject);
        Assert.Contains("Write hello", assessment.Summary);
        Assert.Contains("Type the greeting", assessment.Detail);
        Assert.Contains("Limits: 100 steps", assessment.Detail);
    }

    [Fact]
    public void A_policy_sent_as_a_string_of_JSON_is_accepted()
    {
        var tool = Tool();
        var wrapped = JsonSerializer.Serialize(new { policy = Policy });

        Assert.Contains("Type the greeting", tool.Assess(Json(wrapped), Ctx).Detail);
    }

    [Fact]
    public void An_invalid_policy_comes_back_with_every_problem()
    {
        var tool = Tool();

        var ex = Assert.Throws<ToolInputException>(() => tool.Assess(Json("""{"policy": {"goal": "x", "target": {"app": "notepad.exe"}, "steps": [{"goal": "a"}]}}"""), Ctx));

        Assert.StartsWith("The policy is not valid:", ex.Message);
        Assert.Contains("steps[1].until is required", ex.Message);
        Assert.Throws<ToolInputException>(() => tool.Assess(Json("""{"policy": "not json"}"""), Ctx));
        Assert.Throws<ToolInputException>(() => tool.Assess(Json("{}"), Ctx));
    }

    [Fact]
    public async Task It_runs_the_policy_and_reports_the_steps()
    {
        Editor();
        var model = new ScriptedDecisionModel((req, n) => n == 1 ? Pick(req, Starts("type:")) : Pick(req, Key("done"), achieved: 0.99));

        var result = await Tool(model).InvokeAsync(Json($$"""{"policy": {{Policy}} }"""), Ctx, default);

        Assert.False(result.IsError, result.Content);
        Assert.StartsWith("Result: done", result.Content);
        Assert.Contains("1/1 step(s) done", result.Content);
        Assert.Contains("[type] Type the greeting: done", result.Content);
        Assert.Equal("hello", _desktop.Elements[0].Value);
    }

    [Fact]
    public async Task Without_the_fast_model_it_says_what_to_do_instead()
    {
        Editor();

        var result = await Tool().InvokeAsync(Json($$"""{"policy": {{Policy}} }"""), Ctx, default);

        Assert.True(result.IsError);
        Assert.Contains("PcObserve and PcAct", result.Content);
    }

    [Fact]
    public async Task A_run_that_stops_returns_the_screen_with_valid_ids()
    {
        Editor();
        var model = new ScriptedDecisionModel((req, _) => Pick(req, Starts("type:"), confidence: 0.2));

        var result = await Tool(model).InvokeAsync(Json($$"""{"policy": {{Policy}} }"""), Ctx, default);

        Assert.Contains("Result: escalated", result.Content);
        Assert.Contains("0/1 step(s) done", result.Content);
        Assert.Contains("The screen as it is now (look", result.Content);
    }
}
