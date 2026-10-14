using System.Text.Json;
using Kairo.Core.Config;
using Kairo.Core.Decisions;
using Kairo.Core.Permissions;
using Kairo.Core.Tests.Fakes;
using Kairo.Core.Tools;
using Kairo.PcControl;
using static Kairo.Core.Tests.Fakes.FakeDesktop;
using static Kairo.Core.Tests.Fakes.ScriptedDecisionModel;

namespace Kairo.Core.Tests;

public sealed class PcToolsTests
{
    private readonly FakeDesktop _desktop = new();

    private static KairoConfig Config(string toml)
    {
        var dir = Directory.CreateTempSubdirectory("kairo-pc-").FullName;
        File.WriteAllText(Path.Combine(dir, "config.toml"), toml);
        return KairoConfig.Load(dir, dir, includeProject: false);
    }

    private IReadOnlyList<ITool> Tools(string apps = "\"notepad.exe\"", Func<IDecisionModel?>? model = null) =>
        PcTools.For(Config($"[pc]\nenabled = true\napps = [{apps}]"), model ?? (() => null), _desktop);

    private ITool Tool(string name, IReadOnlyList<ITool>? tools = null) => (tools ?? Tools()).Single(t => t.Spec.Name == name);

    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);

    private ToolContext Ctx => new(Path.GetTempPath());

    private void NotepadLike()
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
    public void Nothing_is_offered_unless_the_user_turned_it_on_and_listed_programs()
    {
        Assert.Empty(PcTools.For(new KairoConfig(), () => null, _desktop));
        Assert.Empty(PcTools.For(Config("[pc]\nenabled = true"), () => null, _desktop));
        Assert.Empty(PcTools.For(Config("[pc]\napps = [\"notepad.exe\"]"), () => null, _desktop));
        Assert.Equal(["PcWindows", "PcLaunch", "PcObserve", "PcAct", "PcPursue", "PcRunPolicy"], Tools().Select(t => t.Spec.Name));
    }

    [Fact]
    public void A_project_cannot_turn_on_or_widen_control_of_the_desktop()
    {
        var user = Directory.CreateTempSubdirectory("kairo-pc-user-").FullName;
        var project = Directory.CreateTempSubdirectory("kairo-pc-proj-").FullName;
        File.WriteAllText(Path.Combine(user, "config.toml"), "[pc]\napps = [\"notepad.exe\"]");
        Directory.CreateDirectory(Path.Combine(project, ".kairo"));
        File.WriteAllText(Path.Combine(project, ".kairo", "config.toml"), "[pc]\nenabled = true\napps = [\"chrome.exe\"]\nmax_steps = 60\nmin_confidence = 0.3");

        var config = KairoConfig.Load(project, user);

        Assert.Null(config.PcEnabled);
        Assert.Equal(["notepad.exe"], config.PcApps);
        Assert.Null(config.PcMaxSteps);
        Assert.Null(config.PcMinConfidence);
        Assert.Empty(PcTools.For(config, () => null, _desktop));
    }

    [Fact]
    public void The_pc_settings_are_read_and_kept_in_range()
    {
        var config = Config("[pc]\nenabled = true\napps = [\"notepad.exe\", \"calc.exe\"]\nmax_steps = 500\nmin_confidence = 0.1");

        Assert.True(config.PcEnabled);
        Assert.Equal(["notepad.exe", "calc.exe"], config.PcApps);
        Assert.Equal(60, config.PcMaxSteps);
        Assert.Equal(0.3, config.PcMinConfidence);
    }

    [Fact]
    public void Driving_the_desktop_asks_even_without_confirmations_and_only_listing_windows_is_free()
    {
        NotepadLike();
        var tools = Tools();
        var engine = new PermissionEngine(new PermissionRules(), PermissionMode.Bypass);
        var assessment = Tool("PcAct", tools).Assess(Json(new { action = "hotkey", text = "ctrl+s" }), Ctx);

        Assert.Equal(RiskLevel.System, assessment.Risk);
        Assert.Equal("notepad.exe", assessment.Subject);
        Assert.Equal(PermissionDecision.Ask, engine.Evaluate("PcAct", assessment, Ctx).Decision);
        Assert.Equal(RiskLevel.System, Tool("PcObserve", tools).Assess(Json(new { }), Ctx).Risk);
        Assert.Equal(RiskLevel.System, Tool("PcPursue", tools).Assess(Json(new { subgoal = "g", success_criterion = "c" }), Ctx).Risk);
        Assert.Equal(PermissionDecision.Allow, engine.Evaluate("PcWindows", Tool("PcWindows", tools).Assess(Json(new { }), Ctx), Ctx).Decision);
    }

    [Fact]
    public void An_allow_rule_for_one_program_skips_the_question_for_that_program_only()
    {
        NotepadLike();
        var rules = new PermissionRules();
        rules.Allow.Add(PermissionRule.Parse("PcAct(notepad.exe)"));
        var engine = new PermissionEngine(rules, PermissionMode.Default);
        var assessment = Tool("PcAct").Assess(Json(new { action = "hotkey", text = "ctrl+s" }), Ctx);

        Assert.Equal(PermissionDecision.Allow, engine.Evaluate("PcAct", assessment, Ctx).Decision);
        Assert.Equal(PermissionDecision.Ask, engine.Evaluate("PcObserve", Tool("PcObserve").Assess(Json(new { }), Ctx), Ctx).Decision);
    }

    [Fact]
    public void Plan_mode_cannot_touch_the_desktop()
    {
        NotepadLike();
        var engine = new PermissionEngine(new PermissionRules(), PermissionMode.Plan);

        Assert.Equal(PermissionDecision.Deny, engine.Evaluate("PcObserve", Tool("PcObserve").Assess(Json(new { }), Ctx), Ctx).Decision);
    }

    [Fact]
    public async Task The_window_list_shows_only_allowed_programs()
    {
        _desktop.Window = _desktop.Window with { Process = "chrome.exe", Title = "Bank" };

        var result = await Tool("PcWindows").InvokeAsync(Json(new { }), Ctx, default);

        Assert.DoesNotContain("Bank", result.Content);
        Assert.Contains("No window of an allowed application is open", result.Content);
        Assert.Contains("notepad.exe", result.Content);
    }

    [Fact]
    public void A_window_of_a_program_that_is_not_allowed_is_refused_before_anything_is_asked()
    {
        _desktop.Window = _desktop.Window with { Process = "chrome.exe" };

        var ex = Assert.Throws<ToolInputException>(() => Tool("PcObserve").Assess(Json(new { window = "w10" }), Ctx));

        Assert.Contains("not in the list", ex.Message);
    }

    [Fact]
    public void Terminals_cannot_be_driven_even_if_listed()
    {
        _desktop.Window = _desktop.Window with { Process = "powershell.exe" };
        var tools = Tools("\"powershell.exe\"");

        var ex = Assert.Throws<ToolInputException>(() => Tool("PcObserve", tools).Assess(Json(new { window = "w10" }), Ctx));

        Assert.Contains("cannot be controlled", ex.Message);
        Assert.Throws<ToolInputException>(() => Tool("PcLaunch", tools).Assess(Json(new { application = "powershell.exe" }), Ctx));
    }

    [Fact]
    public async Task Observing_returns_the_compact_screen_and_the_look_number()
    {
        NotepadLike();

        var result = await Tool("PcObserve").InvokeAsync(Json(new { }), Ctx, default);

        Assert.StartsWith("Look 1.", result.Content);
        Assert.Contains("[e1] edit \"Text editor\" [setvalue]", result.Content);
    }

    [Fact]
    public async Task An_action_does_the_thing_and_returns_the_new_screen_in_the_same_call()
    {
        NotepadLike();
        await Tool("PcObserve").InvokeAsync(Json(new { }), Ctx, default);

        var result = await Tool("PcAct").InvokeAsync(Json(new { action = "set_value", element = "e1", look = 1, text = "hello" }), Ctx, default);

        Assert.False(result.IsError, result.Content);
        Assert.Contains("Look 2.", result.Content);
        Assert.Contains("= \"hello\"", result.Content);
        Assert.Equal("hello", _desktop.Elements[0].Value);
    }

    [Fact]
    public async Task An_element_that_is_gone_is_refused_with_advice()
    {
        NotepadLike();
        await Tool("PcObserve").InvokeAsync(Json(new { }), Ctx, default);
        _desktop.Elements.RemoveAt(1); // the Save button went

        var result = await Tool("PcAct").InvokeAsync(Json(new { action = "invoke", element = "e2" }), Ctx, default);

        Assert.True(result.IsError);
        Assert.Contains("observe again", result.Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_id_from_an_earlier_look_still_works_when_the_element_is_there_and_look_is_not_needed()
    {
        NotepadLike();
        await Tool("PcObserve").InvokeAsync(Json(new { }), Ctx, default);
        await Tool("PcObserve").InvokeAsync(Json(new { }), Ctx, default);

        var result = await Tool("PcAct").InvokeAsync(Json(new { action = "invoke", element = "e2" }), Ctx, default);

        Assert.False(result.IsError, result.Content);
    }

    [Theory]
    [InlineData("{\"action\":\"fly\"}", "Unknown action")]
    [InlineData("{\"action\":\"invoke\"}", "needs an element id")]
    [InlineData("{\"action\":\"set_value\",\"element\":\"e1\",\"look\":1}", "needs text")]
    [InlineData("{\"action\":\"hotkey\",\"text\":\"win+r\"}", "hotkey needs text")]
    [InlineData("{\"action\":\"hotkey\"}", "hotkey needs text")]
    public void Bad_input_to_PcAct_is_explained(string json, string message)
    {
        NotepadLike();

        var ex = Assert.Throws<ToolInputException>(() => Tool("PcAct").Assess(JsonDocument.Parse(json).RootElement, Ctx));

        Assert.Contains(message, ex.Message);
    }

    [Fact]
    public async Task The_stop_key_stops_PcAct_before_it_acts()
    {
        NotepadLike();
        _desktop.Stop = true;

        var result = await Tool("PcAct").InvokeAsync(Json(new { action = "hotkey", text = "ctrl+s" }), Ctx, default);

        Assert.True(result.IsError);
        Assert.Contains("stop key", result.Content);
        Assert.Empty(_desktop.Performed);
    }

    [Fact]
    public async Task Launching_starts_only_a_listed_program_by_its_name()
    {
        var tools = Tools();

        Assert.Throws<ToolInputException>(() => Tool("PcLaunch", tools).Assess(Json(new { application = "calc.exe" }), Ctx));
        var assessment = Tool("PcLaunch", tools).Assess(Json(new { application = @"C:\somewhere\Notepad" }), Ctx);
        var result = await Tool("PcLaunch", tools).InvokeAsync(Json(new { application = @"C:\somewhere\Notepad" }), Ctx, default);

        Assert.Equal("notepad.exe", assessment.Subject);
        Assert.Contains("window w10", result.Content);
    }

    [Fact]
    public async Task PcPursue_without_the_fast_model_says_how_to_continue()
    {
        NotepadLike();

        var result = await Tool("PcPursue").InvokeAsync(Json(new { subgoal = "g", success_criterion = "c" }), Ctx, default);

        Assert.True(result.IsError);
        Assert.Contains("PcObserve and PcAct", result.Content);
    }

    [Fact]
    public async Task PcPursue_runs_the_fast_loop_and_reports_each_step_with_its_timing()
    {
        NotepadLike();
        var model = new ScriptedDecisionModel((req, n) => n == 1 ? Pick(req, Starts("type:")) : Pick(req, Key("done"), achieved: 0.99));
        var tool = Tool("PcPursue", Tools(model: () => model));

        var result = await tool.InvokeAsync(Json(new { subgoal = "Write hello", success_criterion = "The editor shows hello", texts = new { greeting = "hello" }, hotkeys = "ctrl+s" }), Ctx, default);

        Assert.False(result.IsError, result.Content);
        Assert.StartsWith("Result: done", result.Content);
        Assert.Contains("2 step(s)", result.Content);
        Assert.Contains("decided", result.Content);
        Assert.DoesNotContain("The screen as it is now", result.Content); // finished: nothing for the big model to take over
        Assert.Contains(((ChoiceQuestion)model.Requests[0].Questions["next"]).Options, o => o.Key == "hotkey:ctrl+s");
    }

    [Fact]
    public async Task When_the_fast_model_hands_over_the_screen_comes_back_with_valid_ids()
    {
        _desktop.Elements = [El("e1", "button", "Delete everything", UiCapability.Invoke)];
        var model = new ScriptedDecisionModel((req, _) => Pick(req, Starts("invoke:"), confidence: 1.0));

        var result = await Tool("PcPursue", Tools(model: () => model)).InvokeAsync(Json(new { subgoal = "Clean", success_criterion = "Empty" }), Ctx, default);

        Assert.Contains("Result: escalated", result.Content);
        Assert.Contains("The fast model wanted: click button \"Delete everything\"", result.Content);
        Assert.Contains("The screen as it is now (look", result.Content);
        Assert.Contains("[e1] button \"Delete everything\"", result.Content);
        Assert.Empty(_desktop.Performed);
    }

    [Fact]
    public async Task The_step_limit_is_taken_from_the_call()
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

        var result = await Tool("PcPursue", Tools(model: () => model)).InvokeAsync(Json(new { subgoal = "g", success_criterion = "c", max_steps = 3 }), Ctx, default);

        Assert.Contains("Result: steplimit", result.Content);
        Assert.Contains("3 step(s)", result.Content);
    }
}
