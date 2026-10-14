using System.Text.Json;
using System.Text.Json.Serialization;
using Kairo.Core.Checkpoints;
using Kairo.Core.Models;
using Microsoft.Data.Sqlite;

namespace Kairo.Core.Sessions;

public sealed record SessionInfo(
    string Id,
    string WorkingDirectory,
    string Model,
    string Title,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(ChatMessage))]
internal sealed partial class SessionJsonContext : JsonSerializerContext;

/// <summary>Sessions and their append-only message history in SQLite (<c>%APPDATA%\Kairo\data.db</c>).</summary>
public sealed class SessionStore : IDisposable
{
    private readonly SqliteConnection _db;
    private readonly Lock _lock = new();

    /// <summary>Snapshots of files the agent changed, for rolling back.</summary>
    public CheckpointStore Checkpoints { get; }

    public SessionStore(string databasePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
        _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString());
        _db.Open();
        Execute("""
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS sessions (
                id          TEXT PRIMARY KEY,
                cwd         TEXT NOT NULL,
                model       TEXT NOT NULL,
                title       TEXT NOT NULL,
                created_at  TEXT NOT NULL,
                updated_at  TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS messages (
                session_id  TEXT NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
                seq         INTEGER NOT NULL,
                json        TEXT NOT NULL,
                created_at  TEXT,
                PRIMARY KEY (session_id, seq)
            );
            CREATE INDEX IF NOT EXISTS sessions_by_cwd ON sessions(cwd, updated_at);
            CREATE TABLE IF NOT EXISTS session_events (
                session_id  TEXT NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
                at          TEXT NOT NULL,
                kind        TEXT NOT NULL,
                text        TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS session_events_by_session ON session_events(session_id, at);
            CREATE TABLE IF NOT EXISTS workspaces (
                path        TEXT PRIMARY KEY COLLATE NOCASE,
                last_used   TEXT NOT NULL
            );
            """);
        // Databases made before messages were timed get the column; their old rows stay untimed (null).
        if (!HasColumn("messages", "created_at"))
            Execute("ALTER TABLE messages ADD COLUMN created_at TEXT");
        Execute(CheckpointStore.Schema);
        Checkpoints = new CheckpointStore(_db, _lock);
    }

    private bool HasColumn(string table, string column)
    {
        lock (_lock)
        {
            using var cmd = Command($"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = $name", ("$name", column));
            return Convert.ToInt64(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) > 0;
        }
    }

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Kairo", "data.db");

    public SessionInfo Create(string workingDirectory, string model, string title)
    {
        var now = DateTimeOffset.UtcNow;
        var info = new SessionInfo(Guid.NewGuid().ToString("N")[..12], Path.GetFullPath(workingDirectory), model, title, now, now);
        Execute(
            "INSERT INTO sessions (id, cwd, model, title, created_at, updated_at) VALUES ($id, $cwd, $model, $title, $now, $now)",
            ("$id", info.Id), ("$cwd", info.WorkingDirectory), ("$model", model), ("$title", title), ("$now", now.ToString("O")));
        TouchWorkspace(info.WorkingDirectory);
        return info;
    }

    /// <summary>Remembers a folder the user chose to work in.</summary>
    public void TouchWorkspace(string path) =>
        Execute(
            "INSERT INTO workspaces (path, last_used) VALUES ($path, $now) ON CONFLICT(path) DO UPDATE SET last_used = $now",
            ("$path", Path.GetFullPath(path)), ("$now", DateTimeOffset.UtcNow.ToString("O")));

    /// <summary>Folders the user picked or ran sessions in, most recent first.</summary>
    public IReadOnlyList<string> ListWorkspaces(int limit = 10)
    {
        var result = new List<string>();
        lock (_lock)
        {
            using var cmd = Command("""
                SELECT path FROM (
                    SELECT path, last_used AS t FROM workspaces
                    UNION ALL
                    SELECT cwd, updated_at FROM sessions)
                GROUP BY path COLLATE NOCASE ORDER BY MAX(t) DESC LIMIT $limit
                """, ("$limit", limit));
            using var r = cmd.ExecuteReader();
            while (r.Read())
                result.Add(r.GetString(0));
        }
        return result;
    }

    public bool IsKnownWorkspace(string path)
    {
        var full = Path.GetFullPath(path);
        lock (_lock)
        {
            using var cmd = Command(
                "SELECT EXISTS(SELECT 1 FROM workspaces WHERE path = $p COLLATE NOCASE) OR EXISTS(SELECT 1 FROM sessions WHERE cwd = $p COLLATE NOCASE)",
                ("$p", full));
            return Convert.ToInt64(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) == 1;
        }
    }

    public void Append(string sessionId, ChatMessage message)
    {
        var json = JsonSerializer.Serialize(message, SessionJsonContext.Default.ChatMessage);
        Execute("""
            INSERT INTO messages (session_id, seq, json, created_at)
                VALUES ($id, (SELECT COALESCE(MAX(seq), 0) + 1 FROM messages WHERE session_id = $id), $json, $now);
            UPDATE sessions SET updated_at = $now WHERE id = $id;
            """,
            ("$id", sessionId), ("$json", json), ("$now", DateTimeOffset.UtcNow.ToString("O")));
    }

    /// <summary>The messages of a session with the time each was written (null for messages from before they were timed).</summary>
    public IReadOnlyList<TimedMessage> LoadTimedMessages(string sessionId)
    {
        var result = new List<TimedMessage>();
        lock (_lock)
        {
            using var cmd = Command("SELECT seq, json, created_at FROM messages WHERE session_id = $id ORDER BY seq", ("$id", sessionId));
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var at = reader.IsDBNull(2) ? (DateTimeOffset?)null
                    : DateTimeOffset.Parse(reader.GetString(2), System.Globalization.CultureInfo.InvariantCulture);
                result.Add(new TimedMessage(reader.GetInt32(0), at, JsonSerializer.Deserialize(reader.GetString(1), SessionJsonContext.Default.ChatMessage)!));
            }
        }
        return result;
    }

    /// <summary>
    /// Notes what happened to a turn that left no message: a provider error, a cancel. They are not part of the conversation (the model
    /// never sees them); <c>kairo sessions timeline</c> uses them to explain a long gap.
    /// </summary>
    public void AddEvent(string sessionId, string kind, string text) =>
        Execute("INSERT INTO session_events (session_id, at, kind, text) VALUES ($id, $at, $kind, $text)",
            ("$id", sessionId), ("$at", DateTimeOffset.UtcNow.ToString("O")), ("$kind", kind), ("$text", text.Length > 600 ? text[..600] : text));

    public IReadOnlyList<SessionEvent> LoadEvents(string sessionId)
    {
        var result = new List<SessionEvent>();
        lock (_lock)
        {
            using var cmd = Command("SELECT at, kind, text FROM session_events WHERE session_id = $id ORDER BY at", ("$id", sessionId));
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                result.Add(new SessionEvent(DateTimeOffset.Parse(reader.GetString(0), System.Globalization.CultureInfo.InvariantCulture), reader.GetString(1), reader.GetString(2)));
        }
        return result;
    }

    public void SetTitle(string sessionId, string title) =>
        Execute("UPDATE sessions SET title = $title WHERE id = $id", ("$id", sessionId), ("$title", title));

    public IReadOnlyList<ChatMessage> LoadMessages(string sessionId)
    {
        var result = new List<ChatMessage>();
        lock (_lock)
        {
            using var cmd = Command("SELECT json FROM messages WHERE session_id = $id ORDER BY seq", ("$id", sessionId));
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                result.Add(JsonSerializer.Deserialize(reader.GetString(0), SessionJsonContext.Default.ChatMessage)!);
        }
        return result;
    }

    public SessionInfo? Get(string sessionId) =>
        Query("SELECT * FROM sessions WHERE id = $id", ("$id", sessionId)).FirstOrDefault();

    public SessionInfo? Latest(string workingDirectory) =>
        Query("SELECT * FROM sessions WHERE cwd = $cwd ORDER BY updated_at DESC LIMIT 1", ("$cwd", Path.GetFullPath(workingDirectory))).FirstOrDefault();

    public IReadOnlyList<SessionInfo> List(int limit = 20) =>
        Query("SELECT * FROM sessions ORDER BY updated_at DESC LIMIT $limit", ("$limit", limit));

    public void Dispose() => _db.Dispose();

    private List<SessionInfo> Query(string sql, params (string Name, object Value)[] args)
    {
        var result = new List<SessionInfo>();
        lock (_lock)
        {
            using var cmd = Command(sql, args);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                result.Add(new SessionInfo(
                    r.GetString(r.GetOrdinal("id")),
                    r.GetString(r.GetOrdinal("cwd")),
                    r.GetString(r.GetOrdinal("model")),
                    r.GetString(r.GetOrdinal("title")),
                    DateTimeOffset.Parse(r.GetString(r.GetOrdinal("created_at")), System.Globalization.CultureInfo.InvariantCulture),
                    DateTimeOffset.Parse(r.GetString(r.GetOrdinal("updated_at")), System.Globalization.CultureInfo.InvariantCulture)));
            }
        }
        return result;
    }

    private void Execute(string sql, params (string Name, object Value)[] args)
    {
        lock (_lock)
        {
            using var cmd = Command(sql, args);
            cmd.ExecuteNonQuery();
        }
    }

    private SqliteCommand Command(string sql, params (string Name, object Value)[] args)
    {
        var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args)
            cmd.Parameters.AddWithValue(name, value);
        return cmd;
    }
}
