using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Kairo.Core.Models;
using Kairo.Core.Tools;

namespace Kairo.PcControl;

public enum RunState
{
    Queued,
    Running,
    /// <summary>Finished and the success criterion was met.</summary>
    Done,
    /// <summary>Handed back, stuck, failed or ran out of steps: the screen needs the big model.</summary>
    Failed,
    Cancelled,
    /// <summary>Not started, because an earlier run did not finish and these steps depend on the state it left.</summary>
    Skipped,
}

/// <summary>One background run of the fast loop (a sub-goal or a policy).</summary>
public sealed class PcRun
{
    internal PcRun(int id, string title, Func<CancellationToken, Task<(bool Ok, string Text)>> work, CancellationTokenSource cts)
    {
        Id = id;
        Title = title;
        Work = work;
        Cts = cts;
    }

    public int Id { get; }
    public string Title { get; }
    public RunState State { get; internal set; } = RunState.Queued;
    public string? Text { get; internal set; }
    public long ElapsedMs { get; internal set; }
    internal bool Delivered { get; set; }
    internal Func<CancellationToken, Task<(bool Ok, string Text)>> Work { get; }
    internal CancellationTokenSource Cts { get; }

    public bool Finished => State is RunState.Done or RunState.Failed or RunState.Cancelled or RunState.Skipped;
}

/// <summary>
/// Runs of the fast loop that go on while the big model thinks about what comes next. They run one after another, in the order they were
/// queued, and the first one that does not finish cancels the ones behind it: they were planned for a screen that is not there.
/// Only one thing may drive the keyboard and the mouse at a time, so other tools that act on the screen refuse while a run is pending.
/// </summary>
public sealed class PcRunQueue(Func<IDisposable> banner)
{
    private readonly object _gate = new();
    private readonly List<PcRun> _runs = [];
    private readonly Queue<PcRun> _waiting = new();
    private bool _working;
    private int _nextId;

    /// <summary>True while a run is queued or running (judged by the runs, not by the worker thread, which may still be winding down).</summary>
    public bool Busy
    {
        get
        {
            lock (_gate)
                return _runs.Any(r => !r.Finished);
        }
    }

    public int Enqueue(string title, Func<CancellationToken, Task<(bool Ok, string Text)>> work, CancellationToken turn)
    {
        PcRun run;
        lock (_gate)
        {
            run = new PcRun(++_nextId, title, work, CancellationTokenSource.CreateLinkedTokenSource(turn));
            _runs.Add(run);
            while (_runs.Count > 40 && _runs.FindIndex(r => r.Finished && r.Delivered) is var old and >= 0)
                _runs.RemoveAt(old);
            _waiting.Enqueue(run);
            if (!_working)
            {
                // The banner goes up with the first run, not when the worker thread gets round to it.
                _working = true;
                var shown = banner();
                _ = Task.Run(() => Work(shown));
            }
        }
        return run.Id;
    }

    private async Task Work(IDisposable shown)
    {
        using var _ = shown;
        while (true)
        {
            PcRun? run;
            lock (_gate)
            {
                if (!_waiting.TryDequeue(out run))
                {
                    _working = false;
                    return;
                }
                if (run.State != RunState.Queued)
                    continue;
                run.State = RunState.Running;
            }

            var clock = Stopwatch.StartNew();
            RunState end;
            string text;
            try
            {
                var (ok, result) = await run.Work(run.Cts.Token).ConfigureAwait(false);
                (end, text) = (ok ? RunState.Done : run.Cts.IsCancellationRequested ? RunState.Cancelled : RunState.Failed, result);
            }
            catch (OperationCanceledException)
            {
                (end, text) = (RunState.Cancelled, "cancelled");
            }
            catch (Exception ex)
            {
                (end, text) = (RunState.Failed, $"the run failed: {ex.Message}");
            }

            lock (_gate)
            {
                run.ElapsedMs = clock.ElapsedMilliseconds;
                run.Text = text;
                run.State = end;
                if (end != RunState.Done)
                {
                    foreach (var behind in _waiting.Where(r => r.State == RunState.Queued))
                    {
                        behind.State = RunState.Skipped;
                        behind.Text = $"skipped: run r{run.Id} ended {end.ToString().ToLowerInvariant()}, and this one was planned for the screen it would have left";
                    }
                }
            }
        }
    }

    public IReadOnlyList<PcRun> Snapshot()
    {
        lock (_gate)
            return [.. _runs];
    }

    /// <summary>Waits until the runs (all, or the ones named) have finished, up to <paramref name="timeout"/>.</summary>
    public async Task<IReadOnlyList<PcRun>> AwaitAsync(IReadOnlyCollection<int>? ids, TimeSpan timeout, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            var mine = Pick(ids);
            if (mine.All(r => r.Finished) || clock.Elapsed >= timeout || ct.IsCancellationRequested)
                return mine;
            try
            {
                await Task.Delay(40, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return Pick(ids);
            }
        }
    }

    /// <summary>Stops the runs named (or all): the one that is going is cancelled, the ones behind it never start.</summary>
    public int Cancel(IReadOnlyCollection<int>? ids)
    {
        var count = 0;
        lock (_gate)
        {
            foreach (var run in _runs.Where(r => !r.Finished && (ids is null || ids.Contains(r.Id))))
            {
                run.Cts.Cancel();
                if (run.State == RunState.Queued)
                {
                    run.State = RunState.Cancelled;
                    run.Text = "cancelled before it started";
                }
                count++;
            }
        }
        return count;
    }

    private List<PcRun> Pick(IReadOnlyCollection<int>? ids)
    {
        lock (_gate)
            return _runs.Where(r => ids is null || ids.Contains(r.Id)).ToList();
    }
}

internal sealed class PcRunsTool(PcSession session) : ITool
{
    public ToolSpec Spec { get; } = new(
        "PcRuns",
        "Collects the runs you started with background=true (PcPursue / PcRunPolicy) while you went on planning. action: await (default: wait " +
        "until they have finished, up to timeout seconds, then give each result), status (what is going, no waiting) or cancel (stop them; " +
        "the ones behind never start). Runs go one after another; the first that does not finish cancels the rest, and its result tells " +
        "you where the screen stands. While runs are pending, PcAct, PcLaunch and foreground PcPursue / PcRunPolicy are refused: the keyboard " +
        "and the mouse can serve one thing at a time. PcObserve, PcWindows and PcCheck stay available.",
        ToolInput.Schema(
            ("action", "string", "await, status or cancel. Default await.", false),
            ("run", "integer", "The run number from the answer of the call that queued it (r3 is 3). Default: all runs.", false),
            ("timeout", "number", "For await: most seconds to wait (default 20, at most 120).", false)));

    public ToolAssessment Assess(JsonElement input, ToolContext ctx) => new(RiskLevel.Read, ctx.WorkingDirectory, "PcRuns");

    public async Task<ToolResult> InvokeAsync(JsonElement input, ToolContext ctx, CancellationToken ct)
    {
        var action = (ToolInput.OptionalString(input, "action") ?? "await").Trim().ToLowerInvariant();
        var ids = ToolInput.OptionalInt(input, "run") is { } id ? new[] { id } : null;
        var queue = session.Runs;
        if (queue.Snapshot().Count == 0)
            return new ToolResult("There are no background runs. Start one with PcPursue or PcRunPolicy and background=true.");

        switch (action)
        {
            case "cancel":
                var cancelled = queue.Cancel(ids);
                await queue.AwaitAsync(ids, TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
                return new ToolResult($"Cancelled {cancelled} run(s).\n" + Report(queue.Snapshot().Where(r => ids is null || ids.Contains(r.Id)).ToList(), final: true));
            case "status":
                return new ToolResult(Report(queue.Snapshot().Where(r => ids is null || ids.Contains(r.Id)).ToList(), final: false));
            case "await":
                var seconds = Math.Clamp(ToolInput.OptionalNumber(input, "timeout") ?? 20, 0, 120);
                var runs = await queue.AwaitAsync(ids, TimeSpan.FromSeconds(seconds), ct).ConfigureAwait(false);
                return new ToolResult(Report(runs, final: true));
            default:
                throw new ToolInputException($"Unknown action '{action}'. Use await, status or cancel.");
        }
    }

    private string Report(IReadOnlyList<PcRun> runs, bool final)
    {
        var sb = new StringBuilder();
        foreach (var run in runs)
        {
            sb.Append('r').Append(run.Id).Append(" [").Append(run.State.ToString().ToLowerInvariant()).Append("] ").Append(run.Title);
            if (run.Finished && run.ElapsedMs > 0)
                sb.Append(" (").Append((run.ElapsedMs / 1000.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)).Append(" s)");
            sb.Append('\n');
            if (run.Finished && run.Text is { Length: > 0 } text)
            {
                if (!run.Delivered || !final)
                    sb.Append(text.TrimEnd()).Append("\n\n");
                else
                    sb.Append("(result already given)\n");
                if (final)
                    run.Delivered = true;
            }
        }
        if (runs.Any(r => !r.Finished))
            sb.Append("Not finished yet: ").Append(string.Join(", ", runs.Where(r => !r.Finished).Select(r => "r" + r.Id))).Append(". Call PcRuns again to keep waiting.\n");
        return sb.ToString();
    }
}
