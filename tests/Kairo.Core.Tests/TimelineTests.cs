using System.Text.Json;
using Kairo.Core.Models;
using Kairo.Core.Sessions;
using Microsoft.Data.Sqlite;

namespace Kairo.Core.Tests;

public sealed class TimelineTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);
    private readonly string _dir = Directory.CreateTempSubdirectory("kairo-timeline-").FullName;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static ChatMessage User(string text) => new(ChatRole.User, [new TextPart(text)]);

    private static ChatMessage Assistant(params string[] tools) =>
        new(ChatRole.Assistant, [new TextPart("ok"), .. tools.Select((t, i) => new ToolCallPart($"c{i}", t, JsonSerializer.SerializeToElement(new { })))]);

    private static ChatMessage Results(params string[] ids) =>
        new(ChatRole.User, [.. ids.Select(id => new ToolResultPart(id, "done", false))]);

    private static TimedMessage At(int seq, double seconds, ChatMessage message) => new(seq, T0.AddSeconds(seconds), message);

    [Fact]
    public void Time_is_split_into_the_model_the_tools_and_the_user()
    {
        var report = Timeline.Build(
        [
            At(1, 0, User("do it")),
            At(2, 8, Assistant("PcObserve")),   // the model took 8 s
            At(3, 10, Results("c0")),           // the tool took 2 s
            At(4, 15, Assistant("PcAct")),      // the model took 5 s
            At(5, 75, Results("c0")),           // the tool took 60 s (a wait for a yes, say)
            At(6, 80, Assistant()),             // the model took 5 s and answered
            At(7, 380, User("thanks")),         // the user took 300 s
        ]);

        Assert.Equal(TimeSpan.FromSeconds(18), report.Model);
        Assert.Equal(TimeSpan.FromSeconds(62), report.Tools);
        Assert.Equal(TimeSpan.FromSeconds(300), report.User);
        Assert.Equal(TimeSpan.FromSeconds(380), report.End!.Value - report.Start!.Value);
        Assert.Equal(["PcAct", "PcObserve"], report.ByTool.Select(t => t.Tool));
        Assert.Equal(TimeSpan.FromSeconds(60), report.ByTool[0].Longest);
    }

    [Fact]
    public void The_longest_stretches_come_first_and_say_what_they_were()
    {
        var report = Timeline.Build(
        [
            At(1, 0, User("go")),
            At(2, 3, Assistant("Shell")),
            At(3, 200, Results("c0")),
            At(4, 204, Assistant()),
        ]);

        var longest = report.Longest[0];

        Assert.Equal((TimeKind.Tools, 3, "Shell"), (longest.Kind, longest.Seq, longest.Label));
        Assert.Equal(TimeSpan.FromSeconds(197), longest.Length);
    }

    [Fact]
    public void A_turn_with_several_calls_is_split_evenly_between_them()
    {
        var report = Timeline.Build(
        [
            At(1, 0, User("go")),
            At(2, 1, Assistant("Read", "Grep")),
            At(3, 11, Results("c0", "c1")),
        ]);

        Assert.All(report.ByTool, t => Assert.Equal(TimeSpan.FromSeconds(5), t.Total));
        Assert.Equal(2, report.ByTool.Count);
    }

    [Fact]
    public void Messages_without_a_time_are_counted_and_left_out()
    {
        var report = Timeline.Build(
        [
            new TimedMessage(1, null, User("old")),
            new TimedMessage(2, null, Assistant()),
            At(3, 0, User("new")),
            At(4, 4, Assistant()),
        ]);

        Assert.Equal(2, report.Untimed);
        Assert.Equal(TimeSpan.FromSeconds(4), report.Model);
        Assert.Equal(4, report.Messages);
    }

    [Fact]
    public void A_session_with_no_times_gives_an_empty_report()
    {
        var report = Timeline.Build([new TimedMessage(1, null, User("old"))]);

        Assert.Null(report.Start);
        Assert.Equal(TimeSpan.Zero, report.Model + report.Tools + report.User);
    }

    // ---- storage ----

    [Fact]
    public void New_messages_are_stored_with_their_time()
    {
        using var store = new SessionStore(Path.Combine(_dir, "data.db"));
        var session = store.Create(_dir, "m", "t");
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);

        store.Append(session.Id, User("hello"));
        store.Append(session.Id, Assistant());
        var timed = store.LoadTimedMessages(session.Id);

        Assert.Equal(2, timed.Count);
        Assert.All(timed, m => Assert.InRange(m.At!.Value, before, DateTimeOffset.UtcNow.AddSeconds(1)));
        Assert.Equal(2, store.LoadMessages(session.Id).Count); // the plain loader is unchanged
    }

    [Fact]
    public void A_database_made_before_messages_were_timed_is_upgraded_and_keeps_its_rows_untimed()
    {
        var path = Path.Combine(_dir, "old.db");
        using (var old = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            old.Open();
            using var cmd = old.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE sessions (id TEXT PRIMARY KEY, cwd TEXT NOT NULL, model TEXT NOT NULL, title TEXT NOT NULL, created_at TEXT NOT NULL, updated_at TEXT NOT NULL);
                CREATE TABLE messages (session_id TEXT NOT NULL REFERENCES sessions(id) ON DELETE CASCADE, seq INTEGER NOT NULL, json TEXT NOT NULL, PRIMARY KEY (session_id, seq));
                INSERT INTO sessions VALUES ('s1', 'C:/x', 'm', 't', '2026-10-13T10:00:00.0000000+00:00', '2026-10-13T10:00:00.0000000+00:00');
                """;
            cmd.ExecuteNonQuery();
            cmd.CommandText = "INSERT INTO messages (session_id, seq, json) VALUES ('s1', 1, $json)";
            cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(User("from before"), SessionJsonContext.Default.ChatMessage));
            cmd.ExecuteNonQuery();
        }

        using var store = new SessionStore(path);
        store.Append("s1", User("after"));
        var timed = store.LoadTimedMessages("s1");

        Assert.Equal([null, true], timed.Select(m => m.At is null ? (bool?)null : true));
        Assert.Equal(2, store.LoadMessages("s1").Count);
    }
}
