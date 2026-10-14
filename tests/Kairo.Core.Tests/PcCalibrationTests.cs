using Kairo.Core.Tests.Fakes;
using Kairo.PcControl;
using static Kairo.Core.Tests.Fakes.FakeDesktop;
using static Kairo.Core.Tests.Fakes.ScriptedDecisionModel;

namespace Kairo.Core.Tests;

public sealed class DecisionHistoryTests
{
    private static DecisionRecord Taken(string app, double confidence, string result, string kind = "invoke") =>
        new(1_800_000_000, app, kind, confidence, true, result);

    private static List<DecisionRecord> Steps(string app, double confidence, int worked, int failed) =>
        [.. Enumerable.Repeat(Taken(app, confidence, "changed"), worked), .. Enumerable.Repeat(Taken(app, confidence, "unchanged"), failed)];

    [Fact]
    public void Too_few_steps_say_nothing_so_the_users_bar_stays()
    {
        Assert.Equal(0.7, DecisionHistory.Threshold(Steps("a.exe", 0.75, 5, 15), 0.7));
    }

    [Fact]
    public void A_program_where_steps_work_keeps_the_bar()
    {
        Assert.Equal(0.7, DecisionHistory.Threshold(Steps("a.exe", 0.75, 58, 2), 0.7));
    }

    [Fact]
    public void A_program_where_steps_at_the_bar_keep_failing_gets_a_higher_bar_where_they_work()
    {
        var records = Steps("a.exe", 0.72, 10, 30)  // the band just over the bar fails
            .Concat(Steps("a.exe", 0.88, 29, 1))   // the confident band works
            .ToList();

        var bar = DecisionHistory.Threshold(records, 0.7);

        Assert.InRange(bar, 0.75, 0.9);
        Assert.True(bar > 0.72);
    }

    [Fact]
    public void When_nothing_works_the_bar_goes_to_the_ceiling_but_never_above()
    {
        var bar = DecisionHistory.Threshold(Steps("a.exe", 0.9, 10, 40), 0.7);

        Assert.Equal(0.95, bar);
    }

    [Fact]
    public void The_bar_is_never_lowered_below_the_users_own_even_when_everything_works()
    {
        Assert.Equal(0.8, DecisionHistory.Threshold(Steps("a.exe", 0.85, 100, 0), 0.8));
        // Steps below the bar were never taken, so there is nothing to base a lower one on.
        Assert.Equal(0.8, DecisionHistory.Threshold([.. Steps("a.exe", 0.5, 100, 0)], 0.8));
    }

    [Fact]
    public void Handovers_do_not_count_as_failures()
    {
        var records = Steps("a.exe", 0.9, 40, 0).Concat(Enumerable.Repeat(new DecisionRecord(1, "a.exe", "invoke", 0.9, false, "risky"), 100)).ToList();

        Assert.Equal(0.7, DecisionHistory.Threshold(records, 0.7));
    }

    [Fact]
    public void The_log_survives_a_restart_and_ignores_a_torn_line()
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory("kairo-log-").FullName, "pc-decisions.jsonl");
        var log = new DecisionHistory(path);
        log.Append(Taken("notepad.exe", 0.91, "changed"));
        log.Append(new DecisionRecord(2, "notepad.exe", "hotkey", 0.8, false, "low-confidence"));
        File.AppendAllText(path, "{\"t\":3,\"app\":\"no\n");   // a write that was cut off

        var again = new DecisionHistory(path);

        Assert.Equal(2, again.Count);
        Assert.Equal(["changed", "low-confidence"], again.Snapshot().Select(r => r.Result));
    }

    [Fact]
    public void The_log_holds_no_titles_or_text()
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory("kairo-log-").FullName, "pc-decisions.jsonl");
        new DecisionHistory(path).Append(Taken("notepad.exe", 0.91, "changed", "settext"));

        var line = File.ReadAllText(path);

        Assert.Contains("notepad.exe", line);
        Assert.DoesNotContain("title", line, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"conf\":0.910", line);
    }

    [Fact]
    public void The_file_is_trimmed_to_the_latest_lines()
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory("kairo-log-").FullName, "pc-decisions.jsonl");
        var log = new DecisionHistory(path);
        for (var i = 0; i < 6100; i++)
            log.Append(new DecisionRecord(i, "a.exe", "invoke", 0.9, true, "changed"));

        Assert.True(File.ReadAllLines(path).Length <= 5100);
        Assert.Equal(6099, new DecisionHistory(path).Snapshot()[^1].At);
    }

    [Fact]
    public void The_bar_comes_from_the_programs_own_records_and_is_cached_per_count()
    {
        var log = new DecisionHistory(null, Steps("bad.exe", 0.9, 10, 40).Concat(Steps("good.exe", 0.9, 50, 0)));

        Assert.Equal(0.95, log.MinConfidenceFor("bad.exe", 0.7));
        Assert.Equal(0.7, log.MinConfidenceFor("good.exe", 0.7));
        Assert.Equal(0.7, log.MinConfidenceFor("unknown.exe", 0.7));
    }

    [Fact]
    public void Stats_list_programs_bands_and_handovers()
    {
        var log = new DecisionHistory(null, [.. Steps("a.exe", 0.75, 8, 2), .. Steps("a.exe", 0.95, 10, 0), new DecisionRecord(1, "a.exe", "invoke", 0.5, false, "low-confidence")]);

        var stats = Assert.Single(log.Stats(0.7));

        Assert.Equal(20, stats.Executed);
        Assert.Equal(18 / 20.0, stats.SuccessRate, 3);
        Assert.Equal(1, stats.Handovers["low-confidence"]);
        Assert.Contains(stats.Bands, b => b.Band == "0.7–0.8" && b.Count == 10);
    }
}

public sealed class PursueLogAndGatesTests
{
    private readonly FakeDesktop _desktop = new();
    private readonly PcGuard _guard = new(["notepad.exe"]);
    private static readonly PursuePlan Plan = new("Open it", "It is open");

    private PursueLoop Loop(ScriptedDecisionModel model, PursueOptions? options = null) => new(_desktop, _guard, model, options ?? new PursueOptions(ReactWaitMs: 1));

    /// <summary>A window whose button changes the screen each time it is pressed.</summary>
    private void Changing()
    {
        var n = 0;
        _desktop.Elements = [El("e1", "button", "Open", UiCapability.Invoke)];
        _desktop.OnPerform = _ =>
        {
            n++;
            _desktop.Elements = [El("e1", "button", $"Open {n}", UiCapability.Invoke)];
            return ActionOutcome.Success();
        };
    }

    private sealed class Capture : IDecisionLog
    {
        public List<DecisionRecord> Records { get; } = [];

        public void Append(DecisionRecord record) => Records.Add(record);
    }

    [Fact]
    public async Task A_taken_step_is_logged_with_what_the_screen_did_and_a_handover_with_its_reason()
    {
        Changing();
        var log = new Capture();
        var model = new ScriptedDecisionModel((req, n) => n == 1 ? Pick(req, Starts("invoke:"), confidence: 0.9) : Pick(req, Key("done"), achieved: 0.95));

        await Loop(model, new PursueOptions(ReactWaitMs: 1, Log: log)).RunAsync(0x10, Plan, default);

        Assert.Equal([("invoke", true, "changed"), ("done", false, "done")], log.Records.Select(r => (r.Kind, r.Executed, r.Result)));
        Assert.All(log.Records, r => Assert.Equal("notepad.exe", r.App));
        Assert.Equal(0.9, log.Records[0].Confidence, 3);
    }

    [Fact]
    public async Task A_step_that_changed_nothing_is_logged_as_unchanged()
    {
        _desktop.Elements = [El("e1", "button", "Open", UiCapability.Invoke)];
        var log = new Capture();
        var model = new ScriptedDecisionModel((req, n) => n == 1 ? Pick(req, Starts("invoke:")) : Pick(req, Key("stuck")));

        await Loop(model, new PursueOptions(ReactWaitMs: 1, Log: log)).RunAsync(0x10, Plan, default);

        Assert.Contains(log.Records, r => r is { Executed: true, Result: "unchanged" });
        Assert.Contains(log.Records, r => r is { Executed: false, Result: "stuck" });
    }

    [Fact]
    public async Task A_program_with_a_higher_bar_hands_over_a_step_the_default_bar_would_take()
    {
        Changing();
        var log = new Capture();
        var model = new ScriptedDecisionModel((req, _) => Pick(req, Starts("invoke:"), confidence: 0.8));

        var normal = await Loop(model, new PursueOptions(MaxSteps: 1, ReactWaitMs: 1)).RunAsync(0x10, Plan, default);
        var strict = await Loop(model, new PursueOptions(ReactWaitMs: 1, ConfidenceFor: _ => 0.9, Log: log)).RunAsync(0x10, Plan, default);

        Assert.Equal(PursueStatus.StepLimit, normal.Status); // took the step, ran out of steps
        Assert.Equal(PursueStatus.Escalated, strict.Status);
        Assert.Contains("this program needs", strict.Reason);
        Assert.Contains(log.Records, r => r.Result == "low-confidence");
    }

    [Fact]
    public async Task A_step_the_fast_model_thinks_is_hard_to_undo_goes_to_the_big_model_even_without_a_scary_word()
    {
        _desktop.Elements = [El("e1", "button", "Go live", UiCapability.Invoke)];
        var log = new Capture();
        var model = new ScriptedDecisionModel((req, _) => Pick(req, Starts("invoke:"), risk: 0.88));

        var report = await Loop(model, new PursueOptions(ReactWaitMs: 1, Log: log)).RunAsync(0x10, Plan, default);

        Assert.Equal(PursueStatus.Escalated, report.Status);
        Assert.Contains("hard to undo", report.Reason);
        Assert.Empty(_desktop.Performed);
        Assert.Contains(log.Records, r => r.Result == "semantic-risk");
    }

    [Fact]
    public async Task A_low_risk_answer_or_no_answer_or_the_switch_off_lets_the_step_through()
    {
        _desktop.Elements = [El("e1", "button", "Go live", UiCapability.Invoke)];

        var low = new ScriptedDecisionModel((req, n) => n == 1 ? Pick(req, Starts("invoke:"), risk: 0.2) : Pick(req, Key("done"), achieved: 0.95));
        await Loop(low).RunAsync(0x10, Plan, default);
        Assert.Single(_desktop.Performed);

        _desktop.Performed.Clear();
        var none = new ScriptedDecisionModel((req, n) => n == 1 ? Pick(req, Starts("invoke:")) : Pick(req, Key("done"), achieved: 0.95));
        await Loop(none).RunAsync(0x10, Plan, default);
        Assert.Single(_desktop.Performed);

        _desktop.Performed.Clear();
        var off = new ScriptedDecisionModel((req, n) => n == 1 ? Pick(req, Starts("invoke:"), risk: 0.99) : Pick(req, Key("done"), achieved: 0.95));
        await Loop(off, new PursueOptions(ReactWaitMs: 1, SemanticChecks: false)).RunAsync(0x10, Plan, default);
        Assert.Single(_desktop.Performed);
        Assert.DoesNotContain(StepQuestions.RiskId, off.Requests[0].Questions.Keys);
    }

    [Fact]
    public async Task Two_answers_in_a_row_that_the_steps_go_in_circles_hand_over_but_one_does_not()
    {
        Changing();
        // Progress is only asked from the third step (two steps are on record by then).
        var once = new ScriptedDecisionModel((req, n) => Pick(req, Starts("invoke:"), progress: n == 3 ? 0.05 : 0.9));
        var report = await Loop(once, new PursueOptions(MaxSteps: 5, ReactWaitMs: 1)).RunAsync(0x10, Plan, default);
        Assert.Equal(PursueStatus.StepLimit, report.Status);

        _desktop.Performed.Clear();
        Changing();
        var twice = new ScriptedDecisionModel((req, _) => Pick(req, Starts("invoke:"), progress: 0.05));
        var circling = await Loop(twice, new PursueOptions(MaxSteps: 10, ReactWaitMs: 1)).RunAsync(0x10, Plan, default);

        Assert.Equal(PursueStatus.Escalated, circling.Status);
        Assert.Contains("repeat or undo", circling.Reason);
        Assert.True(_desktop.Performed.Count <= 4);
    }

    [Fact]
    public async Task The_progress_question_is_not_asked_before_two_steps_are_on_record()
    {
        Changing();
        var model = new ScriptedDecisionModel((req, n) => n < 3 ? Pick(req, Starts("invoke:")) : Pick(req, Key("done"), achieved: 0.95));

        await Loop(model).RunAsync(0x10, Plan, default);

        Assert.DoesNotContain(StepQuestions.ProgressId, model.Requests[0].Questions.Keys);
        Assert.DoesNotContain(StepQuestions.ProgressId, model.Requests[1].Questions.Keys);
        Assert.Contains(StepQuestions.ProgressId, model.Requests[2].Questions.Keys);
    }
}
