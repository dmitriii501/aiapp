using System.Diagnostics;
using Kairo.Core.Decisions;

namespace Kairo.PcControl;

public sealed record PolicyStepResult(string Id, string Goal, PursueReport Report);

/// <param name="FailedStep">The step the run stopped at when it did not finish; the big model continues from there.</param>
public sealed record PolicyReport(
    PursueStatus Status,
    string Reason,
    IReadOnlyList<PolicyStepResult> Steps,
    int Completed,
    int Total,
    int ReflexFires,
    long ElapsedMs,
    string? FailedStep,
    Observation? Last,
    string? Pending);

/// <summary>
/// Runs a checked policy: its steps in order, each by the fast loop, with the policy's reflexes answering known windows without a model.
/// The first step that does not finish stops the run and hands the screen back; nothing here calls the big model.
/// </summary>
public sealed class PolicyRunner(IDesktop desktop, PcGuard guard, IDecisionModel model, PursueOptions? options = null)
{
    public async Task<PolicyReport> RunAsync(PcPolicy policy, long window, CancellationToken ct, Action<PolicyStepResult>? onStep = null)
    {
        var results = new List<PolicyStepResult>();
        var clock = Stopwatch.StartNew();
        var reflexes = new ReflexSet(policy.Reflexes);
        Observation? last = null;

        PolicyReport Finish(PursueStatus status, string reason, string? failed = null, string? pending = null) =>
            new(status, reason, results, results.Count(r => r.Report.Status == PursueStatus.Done), policy.Steps.Count, reflexes.TotalFired, clock.ElapsedMilliseconds, failed, last, pending);

        // The policy names one program; the window it is run on must be that program, whatever the model passed.
        var info = desktop.Window(window);
        if (info is null)
            return Finish(PursueStatus.Failed, "the window is gone");
        if (!PcGuard.Normalize(info.Process).Equals(policy.App, StringComparison.OrdinalIgnoreCase))
            return Finish(PursueStatus.Failed, $"the policy is for {policy.App}, but the window belongs to {info.Process}");

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(policy.Limits.MaxSeconds));
        var budget = policy.Limits.MaxSteps;

        foreach (var step in policy.Steps)
        {
            if (budget <= 0)
                return Finish(PursueStatus.StepLimit, $"the policy used all {policy.Limits.MaxSteps} steps", step.Id);

            var texts = step.Texts.ToDictionary(t => t, t => policy.Texts[t]);
            var loopOptions = (options ?? new PursueOptions()) with { MaxSteps = Math.Min(step.MaxSteps, budget) };
            var report = await new PursueLoop(desktop, guard, model, loopOptions, reflexes)
                .RunAsync(window, new PursuePlan(step.Goal, step.Until, texts, step.Hotkeys), deadline.Token).ConfigureAwait(false);

            var result = new PolicyStepResult(step.Id, step.Goal, report);
            results.Add(result);
            onStep?.Invoke(result);
            budget -= report.Steps.Count;
            last = report.Last ?? last;

            if (report.Status != PursueStatus.Done)
            {
                var timedOut = deadline.IsCancellationRequested && !ct.IsCancellationRequested;
                return Finish(
                    timedOut ? PursueStatus.StepLimit : report.Status,
                    timedOut ? $"the policy's time limit of {policy.Limits.MaxSeconds} s ran out in step \"{step.Id}\"" : $"step \"{step.Id}\": {report.Reason}",
                    step.Id,
                    report.Pending);
            }
        }
        return Finish(PursueStatus.Done, $"all {policy.Steps.Count} steps are done");
    }
}
