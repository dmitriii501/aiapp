using Kairo.Core.Decisions;
using Kairo.Core.Tests.Fakes;
using Kairo.PcControl;
using static Kairo.Core.Tests.Fakes.FakeDesktop;
using static Kairo.Core.Tests.Fakes.ScriptedDecisionModel;

namespace Kairo.Core.Tests;

public sealed class ObservationDiffTests
{
    private static Observation Look(string title, params UiElement[] elements) => new(1, new WindowInfo(1, 1, "notepad.exe", title, true, false), elements, false);

    [Fact]
    public void The_same_screen_has_no_diff_and_ids_do_not_count()
    {
        var a = Look("t", El("e1", "button", "Save", UiCapability.Invoke));
        var b = Look("t", El("e9", "button", "Save", UiCapability.Invoke, depth: 2));

        Assert.Null(Observation.Diff(a, b));
    }

    [Fact]
    public void What_appeared_and_what_went_are_listed()
    {
        var before = Look("t", El("e1", "menu item", "View", UiCapability.Invoke), El("e2", "edit", "Text", UiCapability.SetValue, value: "a"));
        var after = Look("t", El("e1", "menu item", "View", UiCapability.Invoke), El("e2", "edit", "Text", UiCapability.SetValue, value: "ab"),
            El("e3", "menu item", "Word wrap", UiCapability.Invoke));

        var diff = Observation.Diff(before, after)!;

        Assert.Contains("+ menu item \"Word wrap\"", diff);
        Assert.Contains("+ edit \"Text\"", diff);
        Assert.Contains("= \"ab\"", diff);
        Assert.Contains("- edit \"Text\"", diff);
        Assert.DoesNotContain("View", diff);
    }

    [Fact]
    public void A_long_diff_is_cut_with_a_count()
    {
        var before = Look("t");
        var after = Look("t", [.. Enumerable.Range(1, 20).Select(i => El($"e{i}", "button", $"B{i}", UiCapability.Invoke))]);

        var diff = Observation.Diff(before, after, max: 5)!;

        Assert.Contains("(and 15 more)", diff);
    }
}

public sealed class PursuePipelineTests
{
    private readonly FakeDesktop _desktop = new();
    private readonly PcGuard _guard = new(["notepad.exe"]);
    private static readonly PursueOptions Quick = new(ReactWaitMs: 1);

    private PursueLoop Loop(ScriptedDecisionModel model, PursueOptions? options = null) => new(_desktop, _guard, model, options ?? Quick);

    [Fact]
    public async Task A_slow_app_is_given_another_look_instead_of_being_judged_on_the_old_screen()
    {
        _desktop.Elements = [El("e1", "menu item", "View", UiCapability.Invoke)];
        var actedAt = -1;
        _desktop.OnPerform = _ =>
        {
            actedAt = _desktop.Observations;
            return ActionOutcome.Success();
        };
        // The menu shows up only on the second look after the click.
        _desktop.OnObserve = looks =>
        {
            if (actedAt >= 0 && looks >= actedAt + 2)
                _desktop.Elements = [El("e1", "menu item", "View", UiCapability.Invoke), El("e2", "menu item", "Word wrap", UiCapability.Invoke)];
        };
        var model = new ScriptedDecisionModel((req, n) => n == 1 ? Pick(req, Key("invoke:e1")) : Pick(req, Key("done"), achieved: 0.95));

        var report = await Loop(model).RunAsync(0x10, new PursuePlan("Open View", "The menu is open"), default);

        Assert.Equal(PursueStatus.Done, report.Status);
        var second = model.Requests[1].State.Value;
        Assert.Contains("Word wrap", second.GetProperty("screen").GetString());
        Assert.Contains("+ menu item \"Word wrap\"", second.GetProperty("changes_since_previous_action").GetString());
    }

    [Fact]
    public async Task A_screen_that_stays_the_same_is_reported_as_unchanged_after_the_waits()
    {
        _desktop.Elements = [El("e1", "button", "Open", UiCapability.Invoke)];
        var model = new ScriptedDecisionModel((req, n) => n == 1 ? Pick(req, Key("invoke:e1")) : Pick(req, Key("stuck")));

        await Loop(model, new PursueOptions(ReactWaitMs: 1, ReactRetries: 2)).RunAsync(0x10, new PursuePlan("Open", "Opened"), default);

        var second = model.Requests[1].State.Value;
        Assert.Equal("nothing on the screen changed", second.GetProperty("changes_since_previous_action").GetString());
        Assert.Contains("no visible change", second.GetProperty("recent_steps").GetString());
        Assert.Equal(4, _desktop.Observations); // the first look, the look after the action and two more while waiting
    }

    [Fact]
    public async Task The_first_request_has_no_history()
    {
        _desktop.Elements = [El("e1", "button", "Open", UiCapability.Invoke)];
        var model = new ScriptedDecisionModel((req, _) => Pick(req, Key("stuck")));

        await Loop(model).RunAsync(0x10, new PursuePlan("Open", "Opened"), default);

        var state = model.Requests[0].State.Value;
        Assert.Equal(System.Text.Json.JsonValueKind.Null, state.GetProperty("recent_steps").ValueKind);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, state.GetProperty("changes_since_previous_action").ValueKind);
    }
}

public sealed class PipelineCandidateTests
{
    private static Observation Screen(params UiElement[] elements) => new(7, new WindowInfo(1, 1, "notepad.exe", "t", true, false), elements, false);

    [Fact]
    public void An_opened_menu_is_not_cut_off_by_a_big_window()
    {
        var elements = Enumerable.Range(1, 300).Select(i => El($"e{i}", "button", $"B{i}", UiCapability.Invoke)).ToList();
        elements.Add(El("e301", "popup menu", "", depth: 0));
        elements.Add(El("e302", "menu item", "Word wrap", UiCapability.Invoke, depth: 1));

        var list = Candidates.Build(Screen([.. elements]), new PursuePlan("g", "c"));

        Assert.Contains(list, c => c.Key == "invoke:e302");
        Assert.Contains(list, c => c.Key == "done");
        Assert.Contains(list, c => c.Key == "stuck");
        Assert.True(list.Count <= 255);
    }

    [Fact]
    public void A_named_control_without_any_pattern_can_be_clicked_by_mouse_but_text_cannot()
    {
        var list = Candidates.Build(Screen(
            El("e1", "list item", "Report.docx"),
            El("e2", "text", "Some label"),
            El("e3", "button", "", UiCapability.None)), new PursuePlan("g", "c"));

        var click = Assert.Single(list, c => c.Key.StartsWith("click:", StringComparison.Ordinal));
        Assert.Equal("click:e1", click.Key);
        Assert.Equal(DesktopActionKind.Invoke, click.Action!.Kind);
        Assert.Contains("by mouse", click.Description);
    }

    [Fact]
    public void A_control_with_a_pattern_is_not_offered_twice()
    {
        var list = Candidates.Build(Screen(El("e1", "button", "Save", UiCapability.Invoke)), new PursuePlan("g", "c"));

        Assert.DoesNotContain(list, c => c.Key.StartsWith("click:", StringComparison.Ordinal));
    }
}
