using System.Globalization;
using System.Text.Json;

namespace Kairo.PcControl;

/// <summary>
/// One decision of the fast loop, as the log keeps it. Numbers and names of programs only: no window titles, element names or typed
/// text, so the file says how the fast model does, not what the user was doing.
/// </summary>
/// <param name="At">Unix seconds.</param>
/// <param name="App">The program the step was in (notepad.exe).</param>
/// <param name="Kind">What kind of step: invoke, settext, hotkey, toggle…</param>
/// <param name="Confidence">The fast model's confidence in the choice, 0 to 1.</param>
/// <param name="Executed">The step was taken (else it was handed over).</param>
/// <param name="Result">Taken steps: changed, unchanged or failed (what the screen did). Handed over: low-confidence, risky, semantic-risk, circles, no-effect, unsure-done, stuck. Or done.</param>
public sealed record DecisionRecord(long At, string App, string Kind, double Confidence, bool Executed, string Result);

/// <summary>Where the loop writes what it decided.</summary>
public interface IDecisionLog
{
    void Append(DecisionRecord record);
}

public sealed record AppStats(string App, int Executed, double SuccessRate, IReadOnlyDictionary<string, int> Handovers, IReadOnlyList<(string Band, int Count, double SuccessRate)> Bands, double Threshold);

/// <summary>
/// The decision log, on disk and in memory, and what the loop learns from it: how often a step taken at a given confidence really changed
/// the screen, per program. A program the fast model keeps getting wrong gets a higher bar (more handed to the big model). The bar is
/// never lowered below the user's: only steps that were taken have an outcome, so nothing here can show that a lower bar would have been
/// safe.
/// </summary>
public sealed class DecisionHistory : IDecisionLog
{
    public const int KeepLines = 5000;
    private const int TrimAt = 6000;
    private const int WindowPerApp = 200;

    private readonly object _gate = new();
    private readonly string? _path;
    private readonly List<DecisionRecord> _records;
    private int _lines;
    private readonly Dictionary<string, (int Count, double Value)> _cache = new(StringComparer.OrdinalIgnoreCase);

    public DecisionHistory(string? path = null, IEnumerable<DecisionRecord>? records = null)
    {
        _path = path;
        _records = records?.ToList() ?? [];
        if (path is not null && File.Exists(path))
            Load(path);
        _lines = _records.Count;
    }

    /// <summary>The log in the user's config folder, or an in-memory one when the file cannot be used.</summary>
    public static DecisionHistory Open(string configDirectory) => new(Path.Combine(configDirectory, "pc-decisions.jsonl"));

    public int Count
    {
        get
        {
            lock (_gate)
                return _records.Count;
        }
    }

    public IReadOnlyList<DecisionRecord> Snapshot()
    {
        lock (_gate)
            return [.. _records];
    }

    public void Append(DecisionRecord record)
    {
        lock (_gate)
        {
            _records.Add(record);
            _lines++;
            if (_path is null)
                return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                File.AppendAllText(_path, Serialize(record) + "\n");
                if (_lines > TrimAt)
                {
                    var keep = _records.Skip(Math.Max(0, _records.Count - KeepLines)).ToList();
                    File.WriteAllLines(_path, keep.Select(Serialize));
                    _records.Clear();
                    _records.AddRange(keep);
                    _lines = keep.Count;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The log is a convenience: it must never get in the way of controlling the PC.
            }
        }
    }

    /// <summary>The confidence this program needs: the user's own bar, or a higher one when steps at that bar kept failing here.</summary>
    public double MinConfidenceFor(string app, double baseline)
    {
        lock (_gate)
        {
            var mine = _records.Where(r => string.Equals(r.App, app, StringComparison.OrdinalIgnoreCase)).TakeLast(WindowPerApp * 3).ToList();
            var key = $"{app}|{baseline:0.00}";
            if (_cache.TryGetValue(key, out var hit) && hit.Count == mine.Count)
                return hit.Value;
            var value = Threshold(mine, baseline);
            _cache[key] = (mine.Count, value);
            return value;
        }
    }

    /// <summary>
    /// The bar for one program from its taken steps (the latest ones). Needs enough of them to say anything; raises the bar to the lowest
    /// confidence from which the steps worked often enough, and to the ceiling when none does.
    /// </summary>
    public static double Threshold(IReadOnlyList<DecisionRecord> records, double baseline, double target = 0.9, int minSamples = 30, int minSubset = 10, double ceiling = 0.95)
    {
        var steps = records
            .Where(r => r.Executed && r.Result is "changed" or "unchanged" or "failed" && r.Confidence >= baseline - 1e-9)
            .TakeLast(WindowPerApp)
            .ToList();
        if (steps.Count < minSamples || Rate(steps) >= target)
            return baseline;
        for (var t = baseline + 0.05; t <= ceiling + 1e-9; t += 0.05)
        {
            var subset = steps.Where(r => r.Confidence >= t - 1e-9).ToList();
            if (subset.Count < minSubset)
                break;
            if (Rate(subset) >= target)
                return Math.Round(t, 2);
        }
        return Math.Max(baseline, ceiling);
    }

    private static double Rate(IReadOnlyCollection<DecisionRecord> steps) => steps.Count == 0 ? 1 : steps.Count(r => r.Result == "changed") / (double)steps.Count;

    /// <summary>What the log says, per program, for <c>kairo pc stats</c>.</summary>
    public IReadOnlyList<AppStats> Stats(double baseline)
    {
        var all = Snapshot();
        var result = new List<AppStats>();
        foreach (var group in all.GroupBy(r => r.App, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()))
        {
            var taken = group.Where(r => r.Executed && r.Result is "changed" or "unchanged" or "failed").ToList();
            var handovers = group.Where(r => !r.Executed && r.Result != "done").GroupBy(r => r.Result).ToDictionary(g => g.Key, g => g.Count());
            var bands = new List<(string, int, double)>();
            foreach (var (low, high) in new[] { (0.0, 0.7), (0.7, 0.8), (0.8, 0.9), (0.9, 1.01) })
            {
                var inBand = taken.Where(r => r.Confidence >= low && r.Confidence < high).ToList();
                if (inBand.Count > 0)
                    bands.Add(($"{low.ToString("0.0", CultureInfo.InvariantCulture)}–{Math.Min(high, 1).ToString("0.0", CultureInfo.InvariantCulture)}", inBand.Count, Rate(inBand)));
            }
            result.Add(new AppStats(group.Key, taken.Count, Rate(taken), handovers, bands, MinConfidenceFor(group.Key, baseline)));
        }
        return result;
    }

    private void Load(string path)
    {
        try
        {
            foreach (var line in File.ReadLines(path).TakeLast(KeepLines))
            {
                if (TryParse(line) is { } record)
                    _records.Add(record);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static string Serialize(DecisionRecord r) =>
        $"{{\"t\":{r.At},\"app\":{JsonSerializer.Serialize(r.App)},\"kind\":{JsonSerializer.Serialize(r.Kind)},\"conf\":{r.Confidence.ToString("0.000", CultureInfo.InvariantCulture)},\"ran\":{(r.Executed ? "true" : "false")},\"result\":{JsonSerializer.Serialize(r.Result)}}}";

    private static DecisionRecord? TryParse(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            return new DecisionRecord(
                root.GetProperty("t").GetInt64(),
                root.GetProperty("app").GetString() ?? "",
                root.GetProperty("kind").GetString() ?? "",
                root.GetProperty("conf").GetDouble(),
                root.GetProperty("ran").GetBoolean(),
                root.GetProperty("result").GetString() ?? "");
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return null; // a line from a newer version or a torn write
        }
    }
}
