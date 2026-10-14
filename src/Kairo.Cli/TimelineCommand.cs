using Kairo.Core.Sessions;

namespace Kairo.Cli;

/// <summary>
/// <c>kairo sessions timeline [id]</c>: where the time of a session went: the model, the tools (and the waits for a "yes"), the user.
/// Works for messages written since sessions were timed; older ones are counted as untimed.
/// </summary>
internal static class TimelineCommand
{
    public static int Run(SessionStore store, string? id)
    {
        var session = id is null ? store.List(1).FirstOrDefault() : store.Get(id);
        if (session is null)
        {
            ConsoleUi.Write(ConsoleColor.Red, id is null ? "No sessions yet.\n" : $"Session '{id}' not found. Run 'kairo sessions'.\n");
            return 1;
        }

        var report = Timeline.Build(store.LoadTimedMessages(session.Id), events: store.LoadEvents(session.Id));
        ConsoleUi.Write(ConsoleColor.Magenta, "Kairo · timeline");
        ConsoleUi.Write(ConsoleColor.DarkGray, $"  {session.Id}  {session.Title}\n\n");
        if (report.Start is null || report.End is null)
        {
            Console.WriteLine($"None of the {report.Messages} messages has a time: the session was written before messages were timed.");
            return 0;
        }

        var span = report.End.Value - report.Start.Value;
        Console.WriteLine($"{report.Start.Value.LocalDateTime:yyyy-MM-dd HH:mm:ss} → {report.End.Value.LocalDateTime:HH:mm:ss}  ({Format(span)}), {report.Messages} messages{(report.Untimed > 0 ? $", {report.Untimed} without a time" : "")}");
        Console.WriteLine($"  model  {Format(report.Model),9}   tools {Format(report.Tools),9} (includes waits for your answer to a question)   you {Format(report.User),9}");

        if (report.ModelTurns is { } turns)
            Console.WriteLine($"  model turns: {turns.Count}, mean {Format(turns.Mean)}, median {Format(turns.Median)}, 90% under {Format(turns.P90)}, slowest {Format(turns.Max)}");

        if (report.ByTool.Count > 0)
        {
            Console.WriteLine("\nBy tool (a turn with several calls is split evenly):");
            foreach (var t in report.ByTool)
                Console.WriteLine($"  {t.Tool,-14} {t.Calls,4} calls  {Format(t.Total),9} in all  longest {Format(t.Longest)}");
        }

        Console.WriteLine("\nLongest stretches:");
        foreach (var i in report.Longest)
            Console.WriteLine($"  {Format(i.Length),9}  {i.Kind.ToString().ToLowerInvariant(),-6} #{i.Seq}  {i.Label}");
        if (report.Events is { Count: > 0 })
        {
            Console.WriteLine("\nErrors and cancels in this session:");
            foreach (var e in report.Events)
                Console.WriteLine($"  {e.At.LocalDateTime:HH:mm:ss}  {e.Kind}: {e.Text.ReplaceLineEndings(" ")}");
        }
        return 0;
    }

    private static string Format(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes:00} min" : t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes} min {t.Seconds:00} s" : $"{t.TotalSeconds:0.0} s";
}
