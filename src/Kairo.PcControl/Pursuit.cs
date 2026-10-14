using System.Diagnostics;
using System.Text.Json;
using Kairo.Core.Decisions;

namespace Kairo.PcControl;

/// <summary>
/// What the big model hands to the fast one: one sub-goal with a way to tell it is done, and every piece of text the fast model may
/// type (it picks among options, it never writes anything itself).
/// </summary>
/// <param name="Texts">Name → text. The fast model can choose "put [name] into this field" but cannot invent the text.</param>
/// <param name="Hotkeys">Shortcuts it may press, such as <c>ctrl+s</c>.</param>
public sealed record PursuePlan(string Subgoal, string SuccessCriterion, IReadOnlyDictionary<string, string>? Texts = null, IReadOnlyList<string>? Hotkeys = null);

/// <param name="MinConfidence">A choice the model is less sure of than this is handed to the big model.</param>
/// <param name="MinEffect">The probability the previous step did what was intended; below it counts toward "no effect".</param>
/// <param name="StallLimit">Steps in a row after which the screen looks exactly the same: the loop gives up.</param>
public sealed record PursueOptions(int MaxSteps = 25, double MinConfidence = 0.7, double MinEffect = 0.35, int StallLimit = 3, int MaxElements = 250);

public enum PursueStatus
{
    /// <summary>The sub-goal is met.</summary>
    Done,
    /// <summary>The fast model should not or cannot decide this step alone: the big model (and the user) take over.</summary>
    Escalated,
    /// <summary>Nothing changes on screen, or the model says no option helps.</summary>
    Stuck,
    /// <summary>The user pressed the stop key or cancelled.</summary>
    Aborted,
    StepLimit,
    /// <summary>The window is gone or off limits, or the model's key was refused.</summary>
    Failed,
}

public sealed record PursueStep(int Number, string Action, double Confidence, long ObserveMs, long DecideMs, long ActMs, bool Executed, string Note);

/// <param name="Pending">The step the fast model wanted that was not taken (escalation), described for the big model.</param>
public sealed record PursueReport(PursueStatus Status, string Reason, IReadOnlyList<PursueStep> Steps, Observation? Last, string? Pending = null)
{
    public long TotalMs => Steps.Sum(s => s.ObserveMs + s.DecideMs + s.ActMs);
}

/// <summary>An option of the choice question, with the action behind it.</summary>
public sealed record Candidate(string Key, string Description, DesktopAction? Action, string? Risk);

public static class Candidates
{
    public const string Done = "done";
    public const string Stuck = "stuck";
    private const int MaxOptions = 255 - 2;

    /// <summary>
    /// Everything the fast model may pick from. Only actions the tree really offers: a button that can be invoked, a field that takes
    /// text, a prepared text or shortcut from the plan. Risk is decided here, by the core, not by the model.
    /// </summary>
    public static List<Candidate> Build(Observation observation, PursuePlan plan)
    {
        var list = new List<Candidate>();
        var v = observation.Version;

        foreach (var e in observation.Elements)
        {
            if (!e.Enabled || e.Password && !e.Can.HasFlag(UiCapability.Focus))
                continue;

            void Add(string key, string verb, DesktopActionKind kind, string? text = null, string? extra = null)
            {
                var action = new DesktopAction(kind, v, e.Id, text);
                list.Add(new Candidate(key, $"{verb} {e.Role} \"{Clip(e.Name, 60)}\"{extra}", action, PcGuard.Risk(e, action)));
            }

            // A menu item that can be both invoked and expanded is one thing to the user: opening it. Two options for it would split the
            // model's certainty between them and push a clear choice under the confidence gate.
            var opensMenu = e.Role == "menu item" && e.Can.HasFlag(UiCapability.Invoke) && e.Can.HasFlag(UiCapability.Expand);
            if (e.Can.HasFlag(UiCapability.Invoke))
                Add($"invoke:{e.Id}", opensMenu ? "open menu item" : "click", DesktopActionKind.Invoke);
            if (e.Can.HasFlag(UiCapability.Toggle))
                Add($"toggle:{e.Id}", "toggle", DesktopActionKind.Toggle, extra: e.Checked is { } c ? (c ? " (now on)" : " (now off)") : null);
            if (e.Can.HasFlag(UiCapability.Expand) && !opensMenu)
                Add(e.Expanded == true ? $"collapse:{e.Id}" : $"expand:{e.Id}", e.Expanded == true ? "collapse" : "expand",
                    e.Expanded == true ? DesktopActionKind.Collapse : DesktopActionKind.Expand);
            if (e.Can.HasFlag(UiCapability.Select))
                Add($"select:{e.Id}", "select", DesktopActionKind.Select);
            if (e.Can.HasFlag(UiCapability.Scroll))
            {
                Add($"scroll-down:{e.Id}", "scroll down in", DesktopActionKind.ScrollDown);
                Add($"scroll-up:{e.Id}", "scroll up in", DesktopActionKind.ScrollUp);
            }
            if (e.Can.HasFlag(UiCapability.SetValue) && !e.ReadOnly && !e.Password)
            {
                foreach (var (name, text) in plan.Texts ?? new Dictionary<string, string>())
                    Add($"type:{e.Id}:{name}", $"put the text [{name}] = \"{Clip(text, 60)}\" into", DesktopActionKind.SetValue, text);
            }
        }

        foreach (var keys in plan.Hotkeys ?? [])
        {
            if (!PcGuard.IsValidHotkey(keys))
                continue;
            var action = new DesktopAction(DesktopActionKind.Hotkey, v, null, keys.Trim().ToLowerInvariant());
            list.Add(new Candidate($"hotkey:{action.Text}", $"press {keys.Trim()}", action, PcGuard.Risk(null, action)));
        }

        if (list.Count > MaxOptions)
            list.RemoveRange(MaxOptions, list.Count - MaxOptions);

        list.Add(new Candidate(Done, "nothing more to do: the sub-goal is already achieved", null, null));
        list.Add(new Candidate(Stuck, "none of the other options moves toward the goal", null, null));
        return list;
    }

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..max] + "…";
}

/// <summary>
/// The fast loop of docs/specs/pc-control.md: look, let the decision model choose among what the screen offers, act if it is sure and
/// the step is safe, look again. One model request per step. Whatever it is unsure of, whatever is risky and whatever does not work
/// goes back to the big model with the screen as it is.
/// </summary>
public sealed class PursueLoop(IDesktop desktop, PcGuard guard, IDecisionModel model, PursueOptions? options = null, ReflexSet? reflexes = null)
{
    private readonly PursueOptions _options = options ?? new PursueOptions();

    public async Task<PursueReport> RunAsync(long window, PursuePlan plan, CancellationToken ct, Action<PursueStep>? onStep = null)
    {
        var steps = new List<PursueStep>();
        Observation? last = null;
        string? previous = null;          // description of the action taken in the step before
        string? digestBeforeAction = null;
        var (stalled, noEffect, failures) = (0, 0, 0);

        PursueReport Finish(PursueStatus status, string reason, string? pending = null) => new(status, reason, steps, last, pending);

        for (var number = 1; number <= _options.MaxSteps; number++)
        {
            if (ct.IsCancellationRequested || desktop.StopRequested())
                return Finish(PursueStatus.Aborted, ct.IsCancellationRequested ? "cancelled" : "the user pressed the stop key");

            var info = desktop.Window(window);
            if (info is null)
                return Finish(PursueStatus.Failed, "the window is gone");
            if (guard.Refusal(info) is { } refusal)
                return Finish(PursueStatus.Failed, refusal);

            var clock = Stopwatch.StartNew();
            Observation observation;
            try
            {
                observation = desktop.Observe(window);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return Finish(PursueStatus.Failed, $"the window could not be read: {ex.Message}");
            }
            last = observation;
            var observeMs = clock.ElapsedMilliseconds;

            // The screen looks the same as before the last action: it did nothing, or has not reacted yet.
            if (digestBeforeAction is not null && observation.Digest == digestBeforeAction)
                stalled++;
            else
                stalled = 0;
            if (stalled >= _options.StallLimit)
                return Finish(PursueStatus.Stuck, $"the screen did not change in {stalled} steps");

            // Reflexes first: a window the policy already knows how to answer needs no model at all.
            if (reflexes?.Match(observation) is { } hit)
            {
                clock.Restart();
                digestBeforeAction = observation.Digest;
                var reflexOutcome = SafePerform(window, hit.Action);
                var reflexStep = new PursueStep(number, $"reflex {hit.Reflex.Name}: {hit.Action}", 1.0, observeMs, 0, clock.ElapsedMilliseconds, reflexOutcome.Ok, reflexOutcome.Message);
                steps.Add(reflexStep);
                onStep?.Invoke(reflexStep);
                previous = null;
                continue;
            }

            var candidates = Candidates.Build(observation, plan);
            if (candidates.Count <= 2 && observation.Elements.Count == 0)
                return Finish(PursueStatus.Escalated, "the window has no elements UI Automation can see (the app needs a vision step)");

            clock.Restart();
            DecisionResult decision;
            try
            {
                try
                {
                    decision = await model.DecideAsync(Request(observation, plan, candidates, previous), ct).ConfigureAwait(false);
                }
                catch (DecisionException ex) when (ex.Kind is DecisionFailure.Timeout or DecisionFailure.Busy or DecisionFailure.Unavailable)
                {
                    // One lost answer (a dead connection, a hiccup) should not end a run that was going well: ask again once, on a fresh look.
                    decision = await model.DecideAsync(Request(observation, plan, candidates, previous), ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Stopping while the fast model is still thinking is an ordinary way for a run to end.
                return Finish(PursueStatus.Aborted, "cancelled");
            }
            catch (DecisionException ex)
            {
                return Finish(ex.Kind == DecisionFailure.Unauthorized ? PursueStatus.Failed : PursueStatus.Escalated, $"the fast model could not decide: {ex.Message}");
            }
            var decideMs = clock.ElapsedMilliseconds;

            var choice = decision.Choice("next");
            var achieved = decision.YesNo("achieved").Probability;
            var picked = candidates.First(c => c.Key == choice.Choice);

            void Record(bool executed, string note, long actMs = 0)
            {
                var step = new PursueStep(number, picked.Description, choice.Confidence, observeMs, decideMs, actMs, executed, note);
                steps.Add(step);
                onStep?.Invoke(step);
            }

            // Did the step before work? Two misses in a row mean the model is steering blind.
            if (previous is not null && decision.Answers.TryGetValue("effect", out var effectAnswer) && effectAnswer is YesNoAnswer effect)
                noEffect = effect.Probability < _options.MinEffect ? noEffect + 1 : 0;
            if (noEffect >= 2)
            {
                Record(false, "the last steps had no effect");
                return Finish(PursueStatus.Escalated, "the last two steps did not have the effect that was intended", picked.Description);
            }

            if (picked.Key == Candidates.Done || achieved >= 0.9)
            {
                if (achieved >= 0.5 && (picked.Key == Candidates.Done ? choice.Confidence >= _options.MinConfidence : true))
                {
                    Record(false, $"done (achieved {achieved:0.00})");
                    return Finish(PursueStatus.Done, $"the success criterion is met (probability {achieved:0.00})");
                }
                Record(false, "said done, not sure");
                return Finish(PursueStatus.Escalated, $"the model thinks the goal is met but is not sure ({achieved:0.00}); check the screen", picked.Description);
            }

            if (picked.Key == Candidates.Stuck)
            {
                Record(false, "stuck");
                return Finish(PursueStatus.Stuck, "none of the options moves toward the goal");
            }

            if (choice.Confidence < _options.MinConfidence)
            {
                Record(false, $"not sure ({choice.Confidence:0.00})");
                return Finish(PursueStatus.Escalated, $"the model is not sure which step is right (confidence {choice.Confidence:0.00})", picked.Description);
            }

            if (picked.Risk is { } risk)
            {
                Record(false, $"risky: {risk}");
                return Finish(PursueStatus.Escalated, $"a step that needs the user's say: {risk}", picked.Description);
            }

            // The stop key is looked at once more right before acting: the model may have taken a while to answer.
            if (desktop.StopRequested())
                return Finish(PursueStatus.Aborted, "the user pressed the stop key");

            clock.Restart();
            digestBeforeAction = observation.Digest;
            var outcome = SafePerform(window, picked.Action!);
            var actMs = clock.ElapsedMilliseconds;

            if (!outcome.Ok)
            {
                Record(false, $"failed: {outcome.Message}", actMs);
                if (++failures >= 2)
                    return Finish(PursueStatus.Escalated, $"the action failed twice: {outcome.Message}", picked.Description);
                previous = null; // a failed step is not something to judge the effect of
                continue;
            }

            failures = 0;
            previous = picked.Description;
            Record(true, outcome.Message, actMs);
        }

        return Finish(PursueStatus.StepLimit, $"{_options.MaxSteps} steps without reaching the goal");
    }

    private ActionOutcome SafePerform(long window, DesktopAction action)
    {
        try
        {
            return desktop.Perform(window, action);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ActionOutcome.Fail(ex.Message);
        }
    }

    private static DecisionRequest Request(Observation observation, PursuePlan plan, List<Candidate> candidates, string? previous)
    {
        var stateJson = JsonSerializer.Serialize(new Dictionary<string, string?>
        {
            ["subgoal"] = plan.Subgoal,
            ["success_criterion"] = plan.SuccessCriterion,
            ["app"] = observation.Window.Process,
            ["window_title"] = observation.Window.Title,
            ["previous_action"] = previous,
            ["screen"] = observation.ToText(),
        }, PcJson.Default.DictionaryStringString);

        var questions = new Dictionary<string, DecisionQuestion>
        {
            ["next"] = new ChoiceQuestion(
                "Which option is the best next step toward the sub-goal on the current screen?",
                candidates.Select(c => new ChoiceOption(c.Key, c.Description)).ToList()),
            ["achieved"] = new YesNoQuestion(
                "The success criterion is already met on the current screen.",
                WhenYes: "the screen shows the success criterion is met",
                WhenNo: "the sub-goal is not achieved yet"),
        };
        if (previous is not null)
        {
            questions["effect"] = new YesNoQuestion(
                $"The previous action ({previous}) had the effect that was intended: the screen changed the way it should have.",
                WhenYes: "the previous action worked",
                WhenNo: "nothing happened or something else happened");
        }
        return new DecisionRequest(DecisionState.Json(stateJson), questions);
    }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(Dictionary<string, string?>))]
internal sealed partial class PcJson : System.Text.Json.Serialization.JsonSerializerContext;
