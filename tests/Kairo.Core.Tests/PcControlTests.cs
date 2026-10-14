using Kairo.Core.Decisions;
using Kairo.Core.Tests.Fakes;
using Kairo.PcControl;
using static Kairo.Core.Tests.Fakes.FakeDesktop;
using static Kairo.Core.Tests.Fakes.ScriptedDecisionModel;

namespace Kairo.Core.Tests;

public sealed class PcGuardTests
{
    private static WindowInfo Window(string process) => new(1, 1, process, "t", true, false);

    [Fact]
    public void Only_listed_applications_can_be_controlled()
    {
        var guard = new PcGuard(["notepad", "Calc.exe", @"C:\Windows\System32\mspaint.exe"]);

        Assert.Null(guard.Refusal(Window("notepad.exe")));
        Assert.Null(guard.Refusal(Window("CALC.EXE")));
        Assert.Null(guard.Refusal(Window("mspaint.exe")));
        Assert.Contains("not in the list", guard.Refusal(Window("chrome.exe")));
        Assert.Equal(new[] { "calc.exe", "mspaint.exe", "notepad.exe" }, guard.Allowed.Order(StringComparer.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("powershell.exe")]
    [InlineData("cmd.exe")]
    [InlineData("WindowsTerminal.exe")]
    [InlineData("KeePassXC.exe")]
    [InlineData("regedit.exe")]
    [InlineData("Kairo.exe")]
    [InlineData("code")]
    public void Terminals_password_managers_and_system_tools_are_out_of_reach_even_when_listed(string process)
    {
        var guard = new PcGuard([process, "notepad.exe"]);

        Assert.False(guard.IsAllowed(process));
        Assert.Contains("cannot be controlled", guard.Refusal(Window(process.EndsWith(".exe") ? process : process + ".exe")));
    }

    [Theory]
    [InlineData("Delete", true)]
    [InlineData("Удалить файл", true)]
    [InlineData("Send message", true)]
    [InlineData("Отправить", true)]
    [InlineData("Pay now", true)]
    [InlineData("Оплатить заказ", true)]
    [InlineData("Don't save", true)]
    [InlineData("Не сохранять", true)]
    [InlineData("Sign out", true)]
    [InlineData("Close", true)]
    [InlineData("Закрыть", true)]
    [InlineData("Exit", true)]
    [InlineData("Выход", true)]
    [InlineData("Выйти из аккаунта", true)]
    [InlineData("Save", false)]
    [InlineData("Сохранить", false)]
    [InlineData("Open", false)]
    [InlineData("Search", false)]
    [InlineData("Downloads", false)]
    public void Steps_that_are_hard_to_undo_are_recognised_in_English_and_Russian(string name, bool risky)
    {
        var element = El("e1", "button", name, UiCapability.Invoke);

        var risk = PcGuard.Risk(element, new DesktopAction(DesktopActionKind.Invoke, 1, "e1"));

        Assert.Equal(risky, risk is not null);
    }

    [Fact]
    public void A_password_field_is_always_a_step_for_the_user()
    {
        var field = El("e1", "edit", "Password", UiCapability.SetValue, password: true);

        Assert.NotNull(PcGuard.Risk(field, new DesktopAction(DesktopActionKind.SetValue, 1, "e1", "x")));
    }

    [Theory]
    [InlineData("ctrl+s", true)]
    [InlineData("Ctrl+Shift+N", true)]
    [InlineData("f5", true)]
    [InlineData("alt+tab", true)]
    [InlineData("enter", true)]
    [InlineData("win+r", false)]
    [InlineData("ctrl+alt+delete", false)]
    [InlineData("ctrl+", false)]
    [InlineData("ctrl+ctrl", false)]
    [InlineData("rm -rf", false)]
    public void Only_plain_shortcuts_can_be_typed(string keys, bool valid) => Assert.Equal(valid, PcGuard.IsValidHotkey(keys));

    [Theory]
    [InlineData("alt+f4")]
    [InlineData("ctrl+w")]
    [InlineData("ctrl+shift+delete")]
    public void Shortcuts_that_close_or_remove_things_are_risky(string keys) =>
        Assert.NotNull(PcGuard.Risk(null, new DesktopAction(DesktopActionKind.Hotkey, 1, null, keys)));

    [Fact]
    public void An_ordinary_shortcut_is_not_risky() =>
        Assert.Null(PcGuard.Risk(null, new DesktopAction(DesktopActionKind.Hotkey, 1, null, "ctrl+s")));
}

public sealed class ObservationTests
{
    [Fact]
    public void The_screen_is_written_as_compact_text_with_ids_state_and_what_can_be_done()
    {
        var window = new WindowInfo(0x2a, 5, "notepad.exe", "Untitled - Notepad", true, false);
        var observation = new Observation(3, window,
        [
            El("e1", "menu item", "File", UiCapability.Invoke | UiCapability.Expand, expanded: false),
            El("e2", "edit", "Text editor", UiCapability.SetValue, value: "hello", depth: 1),
            El("e3", "check box", "Word wrap", UiCapability.Toggle, isChecked: true, depth: 1),
            El("e4", "button", "Save", UiCapability.Invoke, enabled: false, depth: 2),
        ], Truncated: false);

        var text = observation.ToText();

        Assert.StartsWith("Window w2a · notepad.exe · \"Untitled - Notepad\"", text);
        Assert.Contains("[e1] menu item \"File\" (collapsed) [invoke,expand]", text);
        Assert.Contains("  [e2] edit \"Text editor\" = \"hello\" [setvalue]", text);
        Assert.Contains("  [e3] check box \"Word wrap\" (checked) [toggle]", text);
        Assert.Contains("    [e4] button \"Save\" (disabled) [invoke]", text);
    }

    [Fact]
    public void The_digest_changes_exactly_when_the_screen_does()
    {
        var window = new WindowInfo(1, 1, "a.exe", "t", true, false);
        var a = new Observation(1, window, [El("e1", "edit", "x", value: "one")], false);
        var same = new Observation(2, window, [El("e1", "edit", "x", value: "one")], false);
        var other = new Observation(3, window, [El("e1", "edit", "x", value: "two")], false);

        Assert.Equal(a.Digest, same.Digest);
        Assert.NotEqual(a.Digest, other.Digest);
    }
}

public sealed class CandidateTests
{
    private static Observation Screen(params UiElement[] elements) => new(7, new WindowInfo(1, 1, "notepad.exe", "t", true, false), elements, false);

    [Fact]
    public void Only_what_the_tree_offers_becomes_an_option_and_done_and_stuck_come_last()
    {
        var screen = Screen(
            El("e1", "button", "Save", UiCapability.Invoke),
            El("e2", "button", "Off", UiCapability.Invoke, enabled: false),
            El("e3", "text", "Just a label"),
            El("e4", "check box", "Wrap", UiCapability.Toggle, isChecked: false),
            El("e5", "list", "Files", UiCapability.Scroll));

        var list = Candidates.Build(screen, new PursuePlan("g", "c"));

        Assert.Equal(["invoke:e1", "toggle:e4", "scroll-down:e5", "scroll-up:e5", "done", "stuck"], list.Select(c => c.Key));
        Assert.All(list.Where(c => c.Action is not null), c => Assert.Equal(7, c.Action!.ObservationVersion));
    }

    [Fact]
    public void Text_is_typed_only_from_the_plan_into_fields_that_take_it()
    {
        var screen = Screen(
            El("e1", "edit", "Search", UiCapability.SetValue),
            El("e2", "edit", "Read only", UiCapability.SetValue, readOnly: true),
            El("e3", "edit", "Password", UiCapability.SetValue, password: true));
        var plan = new PursuePlan("g", "c", new Dictionary<string, string> { ["query"] = "audio", ["other"] = "video" });

        var keys = Candidates.Build(screen, plan).Select(c => c.Key).ToList();

        Assert.Equal(["type:e1:query", "type:e1:other", "done", "stuck"], keys);
        Assert.Empty(Candidates.Build(screen, new PursuePlan("g", "c")).Where(c => c.Key.StartsWith("type:")));
    }

    [Fact]
    public void Shortcuts_come_from_the_plan_and_unsafe_ones_are_dropped()
    {
        var list = Candidates.Build(Screen(), new PursuePlan("g", "c", Hotkeys: ["Ctrl+S", "win+r", "nonsense text"]));

        Assert.Equal(["hotkey:ctrl+s", "done", "stuck"], list.Select(c => c.Key));
    }

    [Fact]
    public void Risk_is_decided_by_the_core_for_each_option()
    {
        var list = Candidates.Build(Screen(El("e1", "button", "Delete", UiCapability.Invoke), El("e2", "button", "Open", UiCapability.Invoke)), new PursuePlan("g", "c"));

        Assert.NotNull(list.Single(c => c.Key == "invoke:e1").Risk);
        Assert.Null(list.Single(c => c.Key == "invoke:e2").Risk);
    }

    [Fact]
    public void Never_more_than_255_options()
    {
        var elements = Enumerable.Range(1, 400).Select(i => El($"e{i}", "button", $"B{i}", UiCapability.Invoke)).ToArray();

        var list = Candidates.Build(Screen(elements), new PursuePlan("g", "c"));

        Assert.Equal(255, list.Count);
        Assert.Equal(["done", "stuck"], list.TakeLast(2).Select(c => c.Key));
    }

    [Fact]
    public void An_expanded_item_offers_collapse()
    {
        var list = Candidates.Build(Screen(El("e1", "menu item", "File", UiCapability.Expand, expanded: true)), new PursuePlan("g", "c"));

        Assert.Contains(list, c => c.Key == "collapse:e1");
    }
}

public sealed class PursueLoopTests
{
    private readonly FakeDesktop _desktop = new();
    private readonly PcGuard _guard = new(["notepad.exe"]);
    private static readonly PursuePlan TypeHello = new("Write a greeting", "The editor contains hello", new Dictionary<string, string> { ["body"] = "hello" });

    /// <summary>A notepad-like window: typing sets the editor's value.</summary>
    private void NotepadLike()
    {
        _desktop.Elements =
        [
            El("e1", "edit", "Text editor", UiCapability.SetValue),
            El("e2", "button", "Save", UiCapability.Invoke),
        ];
        _desktop.OnPerform = a =>
        {
            if (a.Kind == DesktopActionKind.SetValue)
                _desktop.Elements[0] = _desktop.Elements[0] with { Value = a.Text };
            return ActionOutcome.Success();
        };
    }

    private PursueLoop Loop(ScriptedDecisionModel model, PursueOptions? options = null) => new(_desktop, _guard, model, options);

    [Fact]
    public async Task It_types_the_prepared_text_then_sees_the_goal_met_with_one_model_request_per_step()
    {
        NotepadLike();
        var model = new ScriptedDecisionModel((req, n) => n == 1
            ? Pick(req, Starts("type:"))
            : Pick(req, Key("done"), achieved: 0.97));

        var report = await Loop(model).RunAsync(0x10, TypeHello, default);

        Assert.Equal(PursueStatus.Done, report.Status);
        Assert.Equal(2, model.Requests.Count);
        var action = Assert.Single(_desktop.Performed);
        Assert.Equal((DesktopActionKind.SetValue, "e1", "hello"), (action.Kind, action.ElementId, action.Text));
        Assert.Equal("hello", _desktop.Elements[0].Value);
        Assert.Equal([true, false], report.Steps.Select(s => s.Executed));
        Assert.NotNull(report.Last);
    }

    [Fact]
    public async Task The_model_gets_the_screen_the_goal_and_only_real_options_in_one_request()
    {
        NotepadLike();
        var model = new ScriptedDecisionModel((req, n) => n == 1 ? Pick(req, Starts("type:")) : Pick(req, Key("done"), achieved: 0.95));

        await Loop(model).RunAsync(0x10, TypeHello, default);

        var first = model.Requests[0];
        var state = first.State.Value;
        Assert.Equal("Write a greeting", state.GetProperty("subgoal").GetString());
        Assert.Equal("The editor contains hello", state.GetProperty("success_criterion").GetString());
        Assert.Equal("notepad.exe", state.GetProperty("app").GetString());
        Assert.Contains("[e1] edit \"Text editor\"", state.GetProperty("screen").GetString());
        Assert.Equal(["next", "achieved"], first.Questions.Keys);
        var options = ((ChoiceQuestion)first.Questions["next"]).Options.Select(o => o.Key).ToList();
        Assert.Equal(["type:e1:body", "invoke:e2", "done", "stuck"], options);
        // From the second step on it is also asked whether the step before worked.
        Assert.Equal(["next", "achieved", "effect"], model.Requests[1].Questions.Keys);
        Assert.Contains("put the text [body]", model.Requests[1].State.Value.GetProperty("previous_action").GetString());
    }

    [Fact]
    public async Task A_choice_it_is_not_sure_of_goes_to_the_big_model_without_acting()
    {
        NotepadLike();
        var model = new ScriptedDecisionModel((req, _) => Pick(req, Starts("type:"), confidence: 0.4));

        var report = await Loop(model).RunAsync(0x10, TypeHello, default);

        Assert.Equal(PursueStatus.Escalated, report.Status);
        Assert.Contains("not sure", report.Reason);
        Assert.Contains("Text editor", report.Pending);
        Assert.Empty(_desktop.Performed);
    }

    [Fact]
    public async Task A_risky_step_is_never_taken_by_the_fast_model_however_sure_it_is()
    {
        _desktop.Elements = [El("e1", "button", "Delete all files", UiCapability.Invoke)];
        var model = new ScriptedDecisionModel((req, _) => Pick(req, Starts("invoke:"), confidence: 1.0));

        var report = await Loop(model).RunAsync(0x10, new PursuePlan("Clean up", "Nothing left"), default);

        Assert.Equal(PursueStatus.Escalated, report.Status);
        Assert.Contains("user's say", report.Reason);
        Assert.Contains("Delete all files", report.Pending);
        Assert.Empty(_desktop.Performed);
    }

    [Fact]
    public async Task A_password_field_is_never_typed_into()
    {
        _desktop.Elements = [El("e1", "edit", "Password", UiCapability.SetValue, password: true), El("e2", "button", "Sign in", UiCapability.Invoke)];
        var model = new ScriptedDecisionModel((req, _) => Pick(req, Key("stuck")));

        await Loop(model).RunAsync(0x10, new PursuePlan("Log in", "Logged in", new Dictionary<string, string> { ["pw"] = "secret" }), default);

        var keys = ((ChoiceQuestion)model.Requests[0].Questions["next"]).Options.Select(o => o.Key);
        Assert.DoesNotContain(keys, k => k.StartsWith("type:"));
        Assert.DoesNotContain("secret", model.Requests[0].State.Value.GetRawText());
    }

    [Fact]
    public async Task Stuck_is_reported_as_stuck()
    {
        NotepadLike();

        var report = await Loop(new ScriptedDecisionModel((req, _) => Pick(req, Key("stuck")))).RunAsync(0x10, TypeHello, default);

        Assert.Equal(PursueStatus.Stuck, report.Status);
    }

    [Fact]
    public async Task A_screen_that_never_changes_ends_the_loop_instead_of_clicking_forever()
    {
        _desktop.Elements = [El("e1", "button", "Open", UiCapability.Invoke)];
        var model = new ScriptedDecisionModel((req, _) => Pick(req, Starts("invoke:")));

        var report = await Loop(model, new PursueOptions(StallLimit: 3)).RunAsync(0x10, new PursuePlan("Open it", "Opened"), default);

        Assert.Equal(PursueStatus.Stuck, report.Status);
        Assert.Contains("did not change", report.Reason);
        Assert.Equal(3, _desktop.Performed.Count);
    }

    [Fact]
    public async Task Two_steps_in_a_row_that_the_model_says_did_nothing_hand_over()
    {
        var toggled = false;
        _desktop.Elements = [El("e1", "check box", "Wrap", UiCapability.Toggle, isChecked: false)];
        _desktop.OnPerform = _ =>
        {
            toggled = !toggled;
            _desktop.Elements[0] = _desktop.Elements[0] with { Checked = toggled };
            return ActionOutcome.Success();
        };
        var model = new ScriptedDecisionModel((req, _) => Pick(req, Starts("toggle:"), effect: 0.05));

        var report = await Loop(model).RunAsync(0x10, new PursuePlan("Turn on wrap", "Wrap is on"), default);

        Assert.Equal(PursueStatus.Escalated, report.Status);
        Assert.Contains("did not have the effect", report.Reason);
        Assert.Equal(2, _desktop.Performed.Count);
    }

    [Fact]
    public async Task It_stops_at_the_step_limit()
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

        var report = await Loop(model, new PursueOptions(MaxSteps: 4)).RunAsync(0x10, new PursuePlan("Flip", "Never"), default);

        Assert.Equal(PursueStatus.StepLimit, report.Status);
        Assert.Equal(4, report.Steps.Count);
    }

    [Fact]
    public async Task The_stop_key_ends_it_before_anything_more_is_done()
    {
        NotepadLike();
        var model = new ScriptedDecisionModel((req, n) =>
        {
            _desktop.Stop = true; // the user presses Pause while the model is thinking
            return Pick(req, Starts("type:"));
        });

        var report = await Loop(model).RunAsync(0x10, TypeHello, default);

        Assert.Equal(PursueStatus.Aborted, report.Status);
        Assert.Contains("stop key", report.Reason);
        Assert.Empty(_desktop.Performed);
    }

    [Fact]
    public async Task Cancelling_aborts()
    {
        NotepadLike();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var report = await Loop(new ScriptedDecisionModel((req, _) => Pick(req, Key("stuck")))).RunAsync(0x10, TypeHello, cts.Token);

        Assert.Equal(PursueStatus.Aborted, report.Status);
        Assert.Empty(_desktop.Performed);
    }

    [Fact]
    public async Task A_window_that_disappears_fails_the_run()
    {
        NotepadLike();
        var model = new ScriptedDecisionModel((req, n) =>
        {
            _desktop.Gone = true;
            return Pick(req, Starts("type:"));
        });

        var report = await Loop(model).RunAsync(0x10, TypeHello, default);

        Assert.Equal(PursueStatus.Failed, report.Status);
        Assert.Contains("gone", report.Reason);
    }

    [Fact]
    public async Task An_application_that_is_not_allowed_is_refused_before_it_is_even_read()
    {
        NotepadLike();
        _desktop.Window = _desktop.Window with { Process = "chrome.exe" };

        var report = await Loop(new ScriptedDecisionModel((req, _) => Pick(req, Key("stuck")))).RunAsync(0x10, TypeHello, default);

        Assert.Equal(PursueStatus.Failed, report.Status);
        Assert.Contains("not in the list", report.Reason);
        Assert.Equal(0, _desktop.Observations);
    }

    [Theory]
    [InlineData(DecisionFailure.Timeout, PursueStatus.Escalated)]
    [InlineData(DecisionFailure.Busy, PursueStatus.Escalated)]
    [InlineData(DecisionFailure.Unavailable, PursueStatus.Escalated)]
    [InlineData(DecisionFailure.Invalid, PursueStatus.Escalated)]
    [InlineData(DecisionFailure.Unauthorized, PursueStatus.Failed)]
    public async Task When_the_fast_model_cannot_answer_the_big_model_takes_over(DecisionFailure kind, PursueStatus expected)
    {
        NotepadLike();
        var model = new ScriptedDecisionModel((_, _) => throw new DecisionException("boom", kind));

        var report = await Loop(model).RunAsync(0x10, TypeHello, default);

        Assert.Equal(expected, report.Status);
        Assert.Contains("boom", report.Reason);
        Assert.Empty(_desktop.Performed);
    }

    [Fact]
    public async Task An_action_that_fails_twice_is_handed_over_with_the_reason()
    {
        NotepadLike();
        _desktop.OnPerform = _ => ActionOutcome.Fail("the element is gone");
        var model = new ScriptedDecisionModel((req, _) => Pick(req, Starts("type:")));

        var report = await Loop(model).RunAsync(0x10, TypeHello, default);

        Assert.Equal(PursueStatus.Escalated, report.Status);
        Assert.Contains("the element is gone", report.Reason);
        Assert.Equal(2, _desktop.Performed.Count);
    }

    [Fact]
    public async Task One_failed_action_is_retried_on_a_fresh_screen()
    {
        NotepadLike();
        var attempts = 0;
        _desktop.OnPerform = a =>
        {
            if (++attempts == 1)
                return ActionOutcome.Fail("busy");
            _desktop.Elements[0] = _desktop.Elements[0] with { Value = a.Text };
            return ActionOutcome.Success();
        };
        var model = new ScriptedDecisionModel((req, n) => n < 3 ? Pick(req, Starts("type:")) : Pick(req, Key("done"), achieved: 0.99));

        var report = await Loop(model).RunAsync(0x10, TypeHello, default);

        Assert.Equal(PursueStatus.Done, report.Status);
        Assert.Equal(2, _desktop.Performed.Count);
        Assert.Equal(3, _desktop.Observations); // every attempt looks at the screen again; ids from the old look are never reused
    }

    [Fact]
    public async Task Done_that_the_model_is_unsure_of_is_checked_by_the_big_model()
    {
        NotepadLike();
        var model = new ScriptedDecisionModel((req, _) => Pick(req, Key("done"), confidence: 0.9, achieved: 0.2));

        var report = await Loop(model).RunAsync(0x10, TypeHello, default);

        Assert.Equal(PursueStatus.Escalated, report.Status);
        Assert.Contains("not sure", report.Reason);
    }

    [Fact]
    public async Task A_screen_that_clearly_shows_the_goal_ends_it_even_if_the_model_picked_another_step()
    {
        NotepadLike();
        var model = new ScriptedDecisionModel((req, _) => Pick(req, Starts("invoke:"), achieved: 0.96));

        var report = await Loop(model).RunAsync(0x10, TypeHello, default);

        Assert.Equal(PursueStatus.Done, report.Status);
        Assert.Empty(_desktop.Performed);
    }

    [Fact]
    public async Task A_window_without_any_elements_needs_a_vision_step()
    {
        _desktop.Elements = [];
        var model = new ScriptedDecisionModel((req, _) => Pick(req, Key("stuck")));

        var report = await Loop(model).RunAsync(0x10, TypeHello, default);

        Assert.Equal(PursueStatus.Escalated, report.Status);
        Assert.Contains("vision", report.Reason);
        Assert.Empty(model.Requests);
    }

    [Fact]
    public async Task Each_step_reports_where_the_time_went()
    {
        NotepadLike();
        var seen = new List<PursueStep>();
        var model = new ScriptedDecisionModel((req, n) => n == 1 ? Pick(req, Starts("type:")) : Pick(req, Key("done"), achieved: 0.99));

        var report = await Loop(model).RunAsync(0x10, TypeHello, default, seen.Add);

        Assert.Equal(report.Steps, seen);
        Assert.All(report.Steps, s => Assert.True(s.ObserveMs >= 0 && s.DecideMs >= 0 && s.ActMs >= 0));
        Assert.True(report.TotalMs >= 0);
    }
}

public sealed class MenuCandidateTests
{
    [Fact]
    public void A_menu_item_that_can_be_invoked_and_expanded_is_one_option_not_two()
    {
        var screen = new Observation(1, new WindowInfo(1, 1, "notepad.exe", "t", true, false),
            [El("e1", "menu item", "File", UiCapability.Invoke | UiCapability.Expand, expanded: false), El("e2", "tree item", "Folder", UiCapability.Invoke | UiCapability.Expand, expanded: false)], false);

        var keys = Candidates.Build(screen, new PursuePlan("g", "c")).Select(c => c.Key).ToList();

        Assert.Equal(1, keys.Count(k => k.EndsWith(":e1")));
        Assert.Contains("invoke:e1", keys);
        // Other expandable things keep both: for a tree item, opening and selecting are different steps.
        Assert.Contains("invoke:e2", keys);
        Assert.Contains("expand:e2", keys);
    }
}
