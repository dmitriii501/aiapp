using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO.Hashing;
using System.Text;
using Kairo.Core.Decisions;

namespace Kairo.PcControl;

public enum GameStatus
{
    /// <summary>A rule said stop: the goal is reached.</summary>
    Done,
    /// <summary>A rule gave the screen back to the big model.</summary>
    Handover,
    /// <summary>Ticks in a row did nothing, or what was done changed nothing on the screen.</summary>
    Stalled,
    TimeLimit,
    TickLimit,
    /// <summary>The user pressed the stop key, or the run was cancelled.</summary>
    Aborted,
    /// <summary>The window is gone, off limits, Jev could not answer, or a rule is wrong.</summary>
    Failed,
}

/// <summary>What Jev said about one picture, as the rules read it.</summary>
/// <param name="Values">Every name a rule may read: find.NAME.seen/conf/x/y/w/h/left/top/right/bottom, check.NAME, choose.NAME, choose.NAME.conf.</param>
/// <param name="ImageHash">Of the picture that was sent: two equal hashes mean the screen did not change.</param>
public sealed record Perception(IReadOnlyDictionary<string, object> Values, Snapshot Shot, long JevMilliseconds, long PrepareMilliseconds, long Tokens, int ImageBytes, ulong ImageHash);

public sealed record GameResult(
    GameStatus Status,
    string Reason,
    int Ticks,
    long ElapsedMs,
    int Actions,
    IReadOnlyList<string> Log,
    Perception? Last,
    GamePolicy Policy,
    long AverageTickMs,
    long AverageJevMs);

/// <param name="DryRun">Look once and say what the rules would do, without doing it.</param>
/// <param name="OnTick">Told about each tick as it happens (the command line prints them).</param>
/// <param name="DumpDirectory">Each picture that goes to Jev is also saved here (look-1.jpg, look-2.jpg …): to see what Jev was asked about.</param>
public sealed record GameRunOptions(bool DryRun = false, Action<string>? OnTick = null, int Seed = 12345, string? DumpDirectory = null);

/// <summary>
/// The loop of a game policy: look (one request to Jev), let the rules decide, act, repeat. The big model is not in it: it wrote the
/// policy and gets the screen back when a rule hands over, when nothing changes, or when a limit is reached. Everything the loop may
/// do is in the policy that was checked and shown; clicks only land on what Jev found and was sure of.
/// </summary>
public static class GameRunner
{
    /// <summary>A click is skipped when Jev is not sure the thing is there (when asked) or not sure of the box.</summary>
    public const double MinSeen = 0.5;

    public const double MinBoxConfidence = 0.35;

    /// <summary>A failed or slow answer is skipped this many times in a row before the run is given up.</summary>
    public const int MaxLookFailures = 3;

    public static async Task<GameResult> RunAsync(IDesktop desktop, PcGuard guard, IDecisionModel model, GamePolicy policy, long window, GameRunOptions? options = null, CancellationToken ct = default)
    {
        options ??= new GameRunOptions();
        // The first request after a quiet spell pays for DNS and TLS (measured: 2.3 s against 0.5 s): open the connection before the clock runs.
        if (!options.DryRun && model is IWarmUp warm)
            await warm.WarmUpAsync(ct).ConfigureAwait(false);
        var clock = Stopwatch.StartNew();
        var log = new List<string>();
        // The maps of the policy are copied: a run must not leave its notes in the policy, and a second run starts clean.
        var vars = policy.Vars.ToDictionary(v => v.Key, v => v.Value is Dictionary<string, object> map ? new Dictionary<string, object>(map, StringComparer.Ordinal) : v.Value, StringComparer.Ordinal);
        var random = new Random(options.Seed);
        var lastFired = new Dictionary<string, int>();
        var fired = new HashSet<string>();
        Perception? last = null;
        ulong previousHash = 0;
        var tick = 0;
        var actions = 0;
        var idle = 0;
        var unchanged = 0;
        var failures = 0;
        var actedLastTick = false;
        long jevTotal = 0;
        long tickTotal = 0;

        void Note(string line)
        {
            if (log.Count < 400)
                log.Add(line);
            options.OnTick?.Invoke(line);
        }

        // Pipeline: the looks that are still in flight when the run ends are let go (their failures are nobody's business any more).
        var flight = new Queue<(Task<Perception> Task, long Started)>();
        var ledger = new ClickLedger();
        var latestShot = 0; // the newest picture TAKEN (desktops accept actions only against that one), even if its look is still in flight
        var pipeline = policy.Limits.Pipeline && !options.DryRun;

        GameResult Finish(GameStatus status, string reason)
        {
            foreach (var item in flight)
                _ = item.Task.ContinueWith(t => t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            return Result(status, reason);
        }

        GameResult Result(GameStatus status, string reason) =>
            new(status, reason, tick, clock.ElapsedMilliseconds, actions, log, last, policy, tick == 0 ? 0 : tickTotal / tick, tick == 0 ? 0 : jevTotal / tick);

        while (true)
        {
            if (ct.IsCancellationRequested || desktop.StopRequested())
                return Finish(GameStatus.Aborted, ct.IsCancellationRequested ? "cancelled" : "the user pressed the stop key (Pause)");
            if (tick >= policy.Limits.MaxTicks)
                return Finish(GameStatus.TickLimit, $"{policy.Limits.MaxTicks} ticks");
            if (clock.Elapsed.TotalSeconds >= policy.Limits.MaxSeconds)
                return Finish(GameStatus.TimeLimit, $"{policy.Limits.MaxSeconds} s");

            var info = desktop.Window(window);
            if (info is null)
                return Finish(GameStatus.Failed, "the window is gone");
            if (guard.Refusal(info) is { } refusal)
                return Finish(GameStatus.Failed, refusal);

            var tickStart = clock.ElapsedMilliseconds;
            Perception look;
            long capturedAt;
            try
            {
                if (pipeline)
                {
                    void Start()
                    {
                        var number = tick + flight.Count + 1;
                        var picturePath = options.DumpDirectory is { } d ? Path.Combine(d, $"look-{number}.jpg") : null;
                        flight.Enqueue((Task.Run(() => LookAsync(desktop, model, window, policy, ct, picturePath, id => { lock (ledger) latestShot = Math.Max(latestShot, id); }), ct), clock.ElapsedMilliseconds));
                    }

                    if (flight.Count == 0)
                        Start();
                    // The second look starts half a look after the first (a look takes what the last ones took).
                    var stagger = Math.Max(150, (tick == 0 ? 600 : jevTotal / tick + 150) / 2);
                    var wait = stagger - (clock.ElapsedMilliseconds - flight.Peek().Started);
                    if (flight.Count == 1 && wait > 0)
                        await Task.WhenAny(flight.Peek().Task, Task.Delay((int)wait, ct)).ConfigureAwait(false);
                    if (flight.Count == 1 && !flight.Peek().Task.IsCompleted)
                        Start();
                    var head = flight.Dequeue();
                    capturedAt = head.Started;
                    look = await head.Task.ConfigureAwait(false);
                }
                else
                {
                    capturedAt = clock.ElapsedMilliseconds;
                    look = await LookAsync(desktop, model, window, policy, ct, options.DumpDirectory is { } dump ? Path.Combine(dump, $"look-{tick + 1}.jpg") : null).ConfigureAwait(false);
                }
                failures = 0;
            }
            catch (DecisionException ex) when (ex.Kind is DecisionFailure.Timeout or DecisionFailure.Busy && ++failures < MaxLookFailures)
            {
                Note($"tick {tick + 1}: Jev did not answer in time ({ex.Message}); skipped");
                await Task.Delay(200, ct).ConfigureAwait(false);
                continue;
            }
            catch (DecisionException ex)
            {
                tick++;
                return Finish(GameStatus.Failed, $"Jev could not answer: {ex.Message}");
            }
            catch (InvalidOperationException ex)
            {
                tick++;
                return Finish(GameStatus.Failed, ex.Message);
            }
            tick++;
            last = look;
            jevTotal += look.JevMilliseconds;

            // The screen after what the last tick did: if it is the same picture again and again, the clicks do nothing.
            if (actedLastTick && look.ImageHash == previousHash)
                unchanged++;
            else if (actedLastTick)
                unchanged = 0;
            previousHash = look.ImageHash;

            object? Lookup(string name) => name switch
            {
                "tick" => (double)tick,
                "elapsed" => clock.Elapsed.TotalSeconds,
                "idle" => (double)idle,
                _ => vars.TryGetValue(name, out var v) ? v : look.Values.GetValueOrDefault(name),
            };

            GameRule? match = null;
            var heldBack = new List<string>();
            try
            {
                foreach (var rule in policy.Rules)
                {
                    var onCooldown = rule.Cooldown > 0 && lastFired.TryGetValue(rule.Name, out var at) && tick - at < rule.Cooldown;
                    if (rule.Once && fired.Contains(rule.Name) || onCooldown)
                    {
                        // A rule that is true but held back is the usual reason for "nothing happens": say which.
                        if (rule.When.EvalBool(Lookup, random.NextDouble))
                            heldBack.Add($"{rule.Name} ({(onCooldown ? $"cooldown {rule.Cooldown}" : "once, already fired")})");
                        continue;
                    }
                    if (rule.When.EvalBool(Lookup, random.NextDouble))
                    {
                        match = rule;
                        break;
                    }
                }
            }
            catch (GameExprException ex)
            {
                return Finish(GameStatus.Failed, $"a rule could not be evaluated: {ex.Message}");
            }

            var did = new List<string>();
            var acted = false;
            if (match is null)
            {
                idle++;
                if (heldBack.Count > 0)
                    did.Add("true but held back: " + string.Join(", ", heldBack));
            }
            else
            {
                fired.Add(match.Name);
                lastFired[match.Name] = tick;
                foreach (var action in match.Do)
                {
                    try
                    {
                        var (text, stop, did1) = await ExecuteAsync(action, look, desktop, window, vars, Lookup, random, options.DryRun, pipeline ? new ClickContext(ledger, capturedAt, clock, () => { lock (ledger) return latestShot; }) : null, ct).ConfigureAwait(false);
                        did.Add(text);
                        if (stop is { } stopAction)
                        {
                            Note(Line(tick, clock.ElapsedMilliseconds - tickStart, look, match.Name, did));
                            tickTotal += clock.ElapsedMilliseconds - tickStart;
                            return Finish(stopAction.Handover ? GameStatus.Handover : GameStatus.Done, stopAction.Reason);
                        }
                        if (did1)
                        {
                            acted = true;
                            actions++;
                        }
                    }
                    catch (GameExprException ex)
                    {
                        return Finish(GameStatus.Failed, $"rule {match.Name}: {ex.Message}");
                    }
                }
                idle = acted ? 0 : idle + 1;
            }
            actedLastTick = acted;
            var took = clock.ElapsedMilliseconds - tickStart;
            tickTotal += took;
            Note(Line(tick, took, look, match?.Name, did));

            if (options.DryRun)
                return Finish(GameStatus.Done, "dry run: one look, nothing was done");
            if (idle >= policy.Limits.StallTicks)
                return Finish(GameStatus.Stalled, $"{idle} ticks in a row where no rule had anything to do" + (heldBack.Count > 0 ? $" (a rule was true but held back: {string.Join(", ", heldBack)}; drop its cooldown or once)" : ""));
            if (unchanged >= policy.Limits.StallTicks)
                return Finish(GameStatus.Stalled, $"the screen did not change in {unchanged} ticks in a row after clicking");

            if (policy.Limits.TickMs > 0 && took < policy.Limits.TickMs)
                await Task.Delay((int)(policy.Limits.TickMs - took), ct).ConfigureAwait(false);
        }
    }

    private static string Line(int tick, long ms, Perception look, string? rule, List<string> did) =>
        $"tick {tick}: {ms} ms (Jev {look.JevMilliseconds} ms, picture {look.ImageBytes / 1024} KB) " +
        (rule is null ? "no rule" : $"rule {rule}: {(did.Count == 0 ? "nothing" : string.Join("; ", did))}");

    // ---- Looking ----

    /// <summary>One look: the picture (the part the policy asks for, no wider than it says), every question of the policy in one request.</summary>
    public static async Task<Perception> LookAsync(IDesktop desktop, IDecisionModel model, long window, GamePolicy policy, CancellationToken ct, string? savePicture = null, Action<int>? onCaptured = null)
    {
        var clock = Stopwatch.StartNew();
        var shot = desktop.Capture(window);
        onCaptured?.Invoke(shot.Id);
        var p = shot.Picture;
        var (image, regionLeft, regionTop, regionWidth, regionHeight) = Vision.Prepare(p, policy.Region, policy.Width);
        var prepared = clock.ElapsedMilliseconds;
        if (savePicture is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(savePicture))!);
            await File.WriteAllBytesAsync(savePicture, image.Data, ct).ConfigureAwait(false);
        }

        const string key = "screen";
        var questions = new Dictionary<string, DecisionQuestion>();
        for (var i = 0; i < policy.Finds.Count; i++)
        {
            questions[$"f{i}"] = new BoxQuestion($"{policy.Finds[i].Description} in `{key}`");
            if (policy.Finds[i].Presence)
                questions[$"v{i}"] = new YesNoQuestion($"Is there {policy.Finds[i].Description} in `{key}`?");
        }
        for (var i = 0; i < policy.Checks.Count; i++)
            questions[$"c{i}"] = new YesNoQuestion($"Looking at `{key}`: {policy.Checks[i].Statement}");
        for (var i = 0; i < policy.Chooses.Count; i++)
            questions[$"h{i}"] = new ChoiceQuestion($"Looking at `{key}`: {policy.Chooses[i].Question}", [.. policy.Chooses[i].Options.Select(o => new ChoiceOption(o.Key, string.IsNullOrEmpty(o.Value) ? null : o.Value))]);

        var request = new DecisionRequest(DecisionState.Json("{\"screen\":\"screen\"}"), questions, new Dictionary<string, DecisionImage> { [key] = image });
        var jev = Stopwatch.StartNew();
        var answer = await model.DecideAsync(request, ct).ConfigureAwait(false);
        var jevMs = jev.ElapsedMilliseconds;

        var values = new Dictionary<string, object>(StringComparer.Ordinal);
        for (var i = 0; i < policy.Finds.Count; i++)
        {
            var f = policy.Finds[i];
            var box = answer.Box($"f{i}");
            var seen = f.Presence ? answer.YesNo($"v{i}").Probability : 1.0;
            var left = regionLeft + box.Left * regionWidth;
            var top = regionTop + box.Top * regionHeight;
            var right = regionLeft + box.Right * regionWidth;
            var bottom = regionTop + box.Bottom * regionHeight;
            values[$"find.{f.Name}.seen"] = seen;
            values[$"find.{f.Name}.conf"] = box.Confidence ?? 0.0;
            values[$"find.{f.Name}.left"] = Math.Round(left);
            values[$"find.{f.Name}.top"] = Math.Round(top);
            values[$"find.{f.Name}.right"] = Math.Round(right);
            values[$"find.{f.Name}.bottom"] = Math.Round(bottom);
            values[$"find.{f.Name}.x"] = Math.Round((left + right) / 2);
            values[$"find.{f.Name}.y"] = Math.Round((top + bottom) / 2);
            values[$"find.{f.Name}.w"] = Math.Round(right - left);
            values[$"find.{f.Name}.h"] = Math.Round(bottom - top);
        }
        for (var i = 0; i < policy.Checks.Count; i++)
            values[$"check.{policy.Checks[i].Name}"] = answer.YesNo($"c{i}").Probability;
        for (var i = 0; i < policy.Chooses.Count; i++)
        {
            var choice = answer.Choice($"h{i}");
            values[$"choose.{policy.Chooses[i].Name}"] = choice.Choice;
            values[$"choose.{policy.Chooses[i].Name}.conf"] = choice.Confidence;
        }
        return new Perception(values, shot, jevMs, prepared, answer.InputTokens, image.Data.Length, XxHash3.HashToUInt64(image.Data));
    }

    // ---- Acting ----

    private static async Task<(string Text, StopAction? Stop, bool Acted)> ExecuteAsync(
        GameAction action, Perception look, IDesktop desktop, long window, Dictionary<string, object> vars,
        Func<string, object?> lookup, Random random, bool dryRun, ClickContext? pipeline, CancellationToken ct)
    {
        switch (action)
        {
            case ClickAction click:
            {
                var button = click.Button switch { "right" => DesktopActionKind.RightClickAt, "double" => DesktopActionKind.DoubleClickAt, _ => DesktopActionKind.ClickAt };
                double x, y;
                string where;
                if (click.Target is { } target)
                {
                    if (Num(look, $"find.{target}.seen") < MinSeen)
                        return ($"click {target}: skipped (not seen)", null, false);
                    if (Num(look, $"find.{target}.conf") < MinBoxConfidence)
                        return ($"click {target}: skipped (box doubtful)", null, false);
                    x = Num(look, $"find.{target}.x");
                    y = Num(look, $"find.{target}.y");
                    where = target;
                }
                else
                {
                    x = click.X!.EvalNumber(lookup, random.NextDouble);
                    y = click.Y!.EvalNumber(lookup, random.NextDouble);
                    where = "point";
                }
                x = Math.Clamp(Math.Round(x + click.OffsetX), 0, look.Shot.Picture.Width - 1);
                y = Math.Clamp(Math.Round(y + click.OffsetY), 0, look.Shot.Picture.Height - 1);
                var text = $"click {where} @ ({x:0},{y:0})";
                if (dryRun)
                    return (text + " [dry run]", null, false);
                // Pipeline: this picture is up to one look old. A click near one made since it was taken would hit the same thing twice.
                if (pipeline is not null && pipeline.Ledger.ClickedSince(pipeline.PictureTakenAt, x, y, look.Shot.Picture.Width))
                    return ($"click {where}: skipped (clicked there since this picture was taken)", null, false);
                var outcome = desktop.Perform(window, new DesktopAction(button, pipeline?.LatestShot() ?? look.Shot.Id, null, null, (int)x, (int)y), TimeSpan.FromMilliseconds(15));
                if (outcome.Ok)
                    pipeline?.Ledger.Add(pipeline.Clock.ElapsedMilliseconds, x, y);
                return (outcome.Ok ? text : $"click {where}: skipped ({outcome.Message})", null, outcome.Ok);
            }
            case DragAction drag:
            {
                if (Num(look, $"find.{drag.From}.seen") < MinSeen || Num(look, $"find.{drag.To}.seen") < MinSeen
                    || Num(look, $"find.{drag.From}.conf") < MinBoxConfidence || Num(look, $"find.{drag.To}.conf") < MinBoxConfidence)
                    return ($"drag {drag.From} to {drag.To}: skipped (not seen or doubtful)", null, false);
                var text = $"drag {drag.From} to {drag.To}";
                if (dryRun)
                    return (text + " [dry run]", null, false);
                var step = new DesktopAction(DesktopActionKind.DragAt, look.Shot.Id, null, null,
                    (int)Num(look, $"find.{drag.From}.x"), (int)Num(look, $"find.{drag.From}.y"), (int)Num(look, $"find.{drag.To}.x"), (int)Num(look, $"find.{drag.To}.y"));
                var outcome = desktop.Perform(window, step, TimeSpan.FromMilliseconds(15));
                return (outcome.Ok ? text : $"drag: skipped ({outcome.Message})", null, outcome.Ok);
            }
            case KeyAction key:
            {
                if (dryRun)
                    return ($"press {key.Keys} [dry run]", null, false);
                var outcome = desktop.Perform(window, new DesktopAction(DesktopActionKind.Hotkey, 0, null, key.Keys), TimeSpan.FromMilliseconds(15));
                return (outcome.Ok ? $"press {key.Keys}" : $"press {key.Keys}: skipped ({outcome.Message})", null, outcome.Ok);
            }
            case SetAction set:
            {
                var value = set.Value.Eval(lookup, random.NextDouble);
                vars[set.Variable] = value; // also in a dry run: the notes are a copy, and the next action of the rule reads them
                return ($"set {set.Variable} = {Show(value)}", null, false);
            }
            case PutAction put:
            {
                var key = GameExpr.Key(put.Key.Eval(lookup, random.NextDouble));
                var value = put.Value.Eval(lookup, random.NextDouble);
                ((Dictionary<string, object>)vars[put.Map])[key] = value;
                return ($"put {put.Map}[{key}] = {Show(value)}", null, false);
            }
            case DelAction del:
            {
                var key = GameExpr.Key(del.Key.Eval(lookup, random.NextDouble));
                ((Dictionary<string, object>)vars[del.Map]).Remove(key);
                return ($"del {del.Map}[{key}]", null, false);
            }
            case ClearAction clear:
                ((Dictionary<string, object>)vars[clear.Map]).Clear();
                return ($"clear {clear.Map}", null, false);
            case WaitAction wait:
                if (!dryRun)
                    await Task.Delay(wait.Milliseconds, ct).ConfigureAwait(false);
                return ($"wait {wait.Milliseconds} ms", null, false);
            case StopAction stop:
                return (stop.Handover ? $"hand over ({stop.Reason})" : $"stop ({stop.Reason})", stop, false);
            default:
                return (action.ToString() ?? "", null, false);
        }
    }

    private sealed record ClickContext(ClickLedger Ledger, long PictureTakenAt, Stopwatch Clock, Func<int> LatestShot);

    /// <summary>The clicks of a pipelined run, so that two looks that both see the same target do not both click it.</summary>
    internal sealed class ClickLedger
    {
        private readonly List<(long At, double X, double Y)> _clicks = [];

        public void Add(long at, double x, double y)
        {
            _clicks.Add((at, x, y));
            if (_clicks.Count > 64)
                _clicks.RemoveAt(0);
        }

        /// <summary>A click since <paramref name="since"/> within about 4% of the picture width of the point.</summary>
        public bool ClickedSince(long since, double x, double y, int pictureWidth)
        {
            var radius = Math.Max(12, pictureWidth * 0.04);
            return _clicks.Any(c => c.At >= since && Math.Abs(c.X - x) <= radius && Math.Abs(c.Y - y) <= radius);
        }
    }

    private static double Num(Perception look, string name) => look.Values.TryGetValue(name, out var v) && v is double d ? d : 0;

    private static string Show(object value) => value switch
    {
        double d => d.ToString("0.###", CultureInfo.InvariantCulture),
        bool b => b ? "true" : "false",
        _ => value.ToString() ?? "",
    };

    /// <summary>What the big model gets when the run ends: how it went and what Jev last saw, with the pixel centres to click.</summary>
    public static string Describe(GameResult result)
    {
        var sb = new StringBuilder();
        sb.Append(result.Status switch
        {
            GameStatus.Done => "Finished: ",
            GameStatus.Handover => "Handed back to you: ",
            GameStatus.Stalled => "Stopped, nothing was happening: ",
            GameStatus.TimeLimit => "Stopped at the time limit: ",
            GameStatus.TickLimit => "Stopped at the tick limit: ",
            GameStatus.Aborted => "Stopped by the user: ",
            _ => "Failed: ",
        }).Append(result.Reason).Append('\n');
        sb.Append(result.Ticks).Append(" ticks in ").Append((result.ElapsedMs / 1000.0).ToString("0.0", CultureInfo.InvariantCulture)).Append(" s");
        if (result.Ticks > 0)
            sb.Append(" (").Append((result.Ticks * 60000.0 / Math.Max(1, result.ElapsedMs)).ToString("0", CultureInfo.InvariantCulture)).Append(" a minute; a tick takes ").Append(result.AverageTickMs).Append(" ms, Jev ").Append(result.AverageJevMs).Append(" ms)");
        sb.Append(", ").Append(result.Actions).Append(" clicks or keys.\n");
        var tail = result.Log.Skip(Math.Max(0, result.Log.Count - 12)).ToList();
        if (tail.Count > 0)
            sb.Append("Last ticks:\n").Append(string.Join('\n', tail.Select(l => "  " + l))).Append('\n');
        if (result.Last is { } last)
        {
            sb.Append("What Jev saw last (picture ").Append(last.Shot.Id).Append(", pixels of the window picture, use look=").Append(last.Shot.Id).Append("):\n");
            foreach (var f in result.Policy.Finds)
            {
                var seen = Value(last, $"find.{f.Name}.seen");
                sb.Append("  ").Append(f.Name).Append(": ").Append(seen >= MinSeen ? $"at ({Value(last, $"find.{f.Name}.x"):0}, {Value(last, $"find.{f.Name}.y"):0})" : "not seen")
                  .Append(" (").Append((seen * 100).ToString("0", CultureInfo.InvariantCulture)).Append("% there, ").Append((Value(last, $"find.{f.Name}.conf") * 100).ToString("0", CultureInfo.InvariantCulture)).Append("% sure of the box)\n");
            }
            foreach (var c in result.Policy.Checks)
                sb.Append("  ").Append(c.Name).Append(": ").Append((Value(last, $"check.{c.Name}") * 100).ToString("0", CultureInfo.InvariantCulture)).Append("%\n");
            foreach (var c in result.Policy.Chooses)
                sb.Append("  ").Append(c.Name).Append(": ").Append(last.Values.GetValueOrDefault($"choose.{c.Name}")).Append('\n');
        }
        return sb.ToString().TrimEnd();
    }

    private static double Value(Perception look, string name) => Num(look, name);
}
