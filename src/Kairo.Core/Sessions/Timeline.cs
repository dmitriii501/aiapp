using Kairo.Core.Models;

namespace Kairo.Core.Sessions;

/// <summary>A stored message with the time it was written; null for messages from before sessions were timed.</summary>
public sealed record TimedMessage(int Seq, DateTimeOffset? At, ChatMessage Message);

public enum TimeKind
{
    /// <summary>From the message before to an answer of the model: the model reading, thinking and writing.</summary>
    Model,
    /// <summary>From a request for tools to their results: the tools running, and any wait for the user's yes.</summary>
    Tools,
    /// <summary>From the end of an answer to the next thing the user typed.</summary>
    User,
}

/// <param name="Seq">The message that ended the interval.</param>
/// <param name="Label">What it was: the tools that ran, the kind of message.</param>
public sealed record Interval(int Seq, TimeKind Kind, TimeSpan Length, string Label);

/// <summary>Something that happened to a turn without becoming a message: an error from the provider, a cancel.</summary>
public sealed record SessionEvent(DateTimeOffset At, string Kind, string Text);

/// <summary>How long the model's turns took: the mean hides the few very slow ones, so the spread is shown.</summary>
public sealed record TurnSpread(int Count, TimeSpan Mean, TimeSpan Median, TimeSpan P90, TimeSpan Max);

public sealed record ToolTime(string Tool, int Calls, TimeSpan Total, TimeSpan Longest);

public sealed record TimelineReport(
    DateTimeOffset? Start,
    DateTimeOffset? End,
    int Messages,
    int Untimed,
    TimeSpan Model,
    TimeSpan Tools,
    TimeSpan User,
    IReadOnlyList<ToolTime> ByTool,
    IReadOnlyList<Interval> Longest,
    TurnSpread? ModelTurns = null,
    IReadOnlyList<SessionEvent>? Events = null);

/// <summary>
/// Where the time of a session went. Messages are written when they are complete, so the gap before a message is what produced it: the
/// model's answer, a batch of tool results, a user prompt. A tool batch with several calls is split evenly (they run one after another
/// and are stored together), and a wait for the user's permission is part of the tool time.
/// </summary>
public static class Timeline
{
    public static TimelineReport Build(IReadOnlyList<TimedMessage> messages, int longest = 8, IReadOnlyList<SessionEvent>? events = null)
    {
        var intervals = new List<Interval>();
        var perTool = new Dictionary<string, (int Calls, TimeSpan Total, TimeSpan Longest)>();
        TimedMessage? previous = null;
        var untimed = messages.Count(m => m.At is null);

        foreach (var message in messages.Where(m => m.At is not null))
        {
            if (previous is not null)
            {
                var length = message.At!.Value - previous.At!.Value;
                if (length < TimeSpan.Zero)
                    length = TimeSpan.Zero;
                var calls = previous.Message.Parts.OfType<ToolCallPart>().ToList();
                // An error or a cancel inside this stretch explains why it was long.
                var inside = events?.Where(e => e.At > previous.At!.Value && e.At <= message.At!.Value).ToList();
                var because = inside is { Count: > 0 } ? $" — {inside[0].Kind} at {inside[0].At.LocalDateTime:HH:mm:ss}: {Clip(inside[0].Text)}" : "";

                if (message.Message.Role == ChatRole.Assistant)
                {
                    intervals.Add(new Interval(message.Seq, TimeKind.Model, length, "the model answered" + because));
                }
                else if (message.Message.IsSummary)
                {
                    // Compaction is stored as a user message, but the model wrote it: the gap before it is the model summarizing.
                    intervals.Add(new Interval(message.Seq, TimeKind.Model, length, "the model summarized the context" + because));
                }
                else if (message.Message.Parts.OfType<ToolResultPart>().Any() && calls.Count > 0)
                {
                    var each = length / calls.Count;
                    foreach (var call in calls)
                    {
                        var stat = perTool.GetValueOrDefault(call.Name);
                        perTool[call.Name] = (stat.Calls + 1, stat.Total + each, each > stat.Longest ? each : stat.Longest);
                    }
                    intervals.Add(new Interval(message.Seq, TimeKind.Tools, length, string.Join(", ", calls.Select(c => c.Name)) + because));
                }
                else
                {
                    intervals.Add(new Interval(message.Seq, TimeKind.User, length, "the user wrote" + because));
                }
            }
            previous = message;
        }

        var timed = messages.Where(m => m.At is not null).ToList();
        return new TimelineReport(
            timed.FirstOrDefault()?.At,
            timed.LastOrDefault()?.At,
            messages.Count,
            untimed,
            Sum(intervals, TimeKind.Model),
            Sum(intervals, TimeKind.Tools),
            Sum(intervals, TimeKind.User),
            [.. perTool.OrderByDescending(p => p.Value.Total).Select(p => new ToolTime(p.Key, p.Value.Calls, p.Value.Total, p.Value.Longest))],
            [.. intervals.OrderByDescending(i => i.Length).Take(longest)],
            Spread(intervals.Where(i => i.Kind == TimeKind.Model).Select(i => i.Length).ToList()),
            events ?? []);
    }

    private static string Clip(string text) => text.ReplaceLineEndings(" ") is { Length: > 140 } flat ? flat[..140] + "…" : text.ReplaceLineEndings(" ");

    private static TurnSpread? Spread(List<TimeSpan> turns)
    {
        if (turns.Count == 0)
            return null;
        turns.Sort();
        return new TurnSpread(turns.Count, TimeSpan.FromTicks((long)turns.Average(t => t.Ticks)), turns[turns.Count / 2], turns[Math.Min(turns.Count - 1, (int)Math.Ceiling(turns.Count * 0.9) - 1)], turns[^1]);
    }

    private static TimeSpan Sum(List<Interval> intervals, TimeKind kind) =>
        intervals.Where(i => i.Kind == kind).Aggregate(TimeSpan.Zero, (sum, i) => sum + i.Length);
}
