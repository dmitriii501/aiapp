using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Kairo.Swarm;

public static class SwarmStatus
{
    public const string Running = "running";
    public const string Done = "done";
    public const string Paused = "paused";
    public const string Stalled = "stalled";
    public const string Stopped = "stopped";
    public const string Failed = "failed";
}

public static class TaskState
{
    public const string Pending = "pending";
    public const string InProgress = "in_progress";
    public const string Done = "done";
    public const string Failed = "failed";

    public static bool IsValid(string status) => status is Pending or InProgress or Done or Failed;
}

/// <summary>Limits that keep a swarm from running away (see docs/specs/swarm.md).</summary>
/// <param name="MaxAgents">Workers plus the lead.</param>
/// <param name="MaxTokens">Input plus output tokens over all model calls of the swarm.</param>
/// <param name="MaxConcurrency">Model requests in flight at once, over all agents.</param>
/// <param name="MaxMinutes">Wall-clock time, 0 for no limit.</param>
public sealed record SwarmLimits(int MaxAgents = 5, long MaxTokens = 3_000_000, int MaxConcurrency = 3, int MaxMinutes = 120);

public sealed record SwarmInfo(
    string Id, string WorkingDirectory, string Goal, string Status, string? Summary,
    SwarmLimits Limits, long TokensUsed, int ModelCalls, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record SwarmAgent(string SwarmId, string Name, string Role, string Status, string SessionId, string WorkingDirectory, string? Branch);

public sealed record SwarmTask(
    string SwarmId, int Seq, string Title, string Description, string Status, string? Assignee, IReadOnlyList<int> DependsOn, string? Result, bool LeadAcknowledged);

public sealed record SwarmMessage(long Id, string SwarmId, string From, string To, string Text, DateTimeOffset CreatedAt);

public sealed record SwarmNote(string Author, string Text, DateTimeOffset CreatedAt);

/// <summary>
/// The swarm's shared state in SQLite: the swarm, its agents, the task board, messages between agents and the shared notes.
/// Everything the scheduler decides is derived from here, so a swarm can be stopped and resumed.
/// </summary>
public sealed class SwarmStore : IDisposable
{
    private readonly SqliteConnection _db;
    private readonly Lock _lock = new();

    public SwarmStore(string databasePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
        _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString());
        _db.Open();
        Execute("""
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS swarms (
                id TEXT PRIMARY KEY, cwd TEXT NOT NULL, goal TEXT NOT NULL, status TEXT NOT NULL, summary TEXT,
                max_agents INTEGER NOT NULL, max_tokens INTEGER NOT NULL, max_concurrency INTEGER NOT NULL, max_minutes INTEGER NOT NULL,
                tokens_used INTEGER NOT NULL DEFAULT 0, model_calls INTEGER NOT NULL DEFAULT 0,
                created_at TEXT NOT NULL, updated_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS swarm_agents (
                swarm_id TEXT NOT NULL REFERENCES swarms(id) ON DELETE CASCADE, name TEXT NOT NULL, role TEXT NOT NULL,
                status TEXT NOT NULL, session_id TEXT NOT NULL, cwd TEXT NOT NULL, branch TEXT,
                PRIMARY KEY (swarm_id, name));
            CREATE TABLE IF NOT EXISTS swarm_tasks (
                swarm_id TEXT NOT NULL REFERENCES swarms(id) ON DELETE CASCADE, seq INTEGER NOT NULL, title TEXT NOT NULL,
                description TEXT NOT NULL, status TEXT NOT NULL, assignee TEXT, depends_on TEXT NOT NULL DEFAULT '',
                result TEXT, lead_ack INTEGER NOT NULL DEFAULT 0, updated_at TEXT NOT NULL,
                PRIMARY KEY (swarm_id, seq));
            CREATE TABLE IF NOT EXISTS swarm_messages (
                id INTEGER PRIMARY KEY AUTOINCREMENT, swarm_id TEXT NOT NULL REFERENCES swarms(id) ON DELETE CASCADE,
                from_agent TEXT NOT NULL, to_agent TEXT NOT NULL, text TEXT NOT NULL, delivered INTEGER NOT NULL DEFAULT 0, created_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS swarm_notes (
                id INTEGER PRIMARY KEY AUTOINCREMENT, swarm_id TEXT NOT NULL REFERENCES swarms(id) ON DELETE CASCADE,
                author TEXT NOT NULL, text TEXT NOT NULL, created_at TEXT NOT NULL);
            """);
    }

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Kairo", "swarms.db");

    // ---- Swarms ----

    public SwarmInfo Create(string workingDirectory, string goal, SwarmLimits limits)
    {
        var id = Guid.NewGuid().ToString("N")[..8];
        var now = Now();
        Execute("""
            INSERT INTO swarms (id, cwd, goal, status, max_agents, max_tokens, max_concurrency, max_minutes, created_at, updated_at)
            VALUES ($id, $cwd, $goal, $status, $a, $t, $c, $m, $now, $now)
            """,
            ("$id", id), ("$cwd", Path.GetFullPath(workingDirectory)), ("$goal", goal), ("$status", SwarmStatus.Paused),
            ("$a", limits.MaxAgents), ("$t", limits.MaxTokens), ("$c", limits.MaxConcurrency), ("$m", limits.MaxMinutes), ("$now", now));
        return Get(id)!;
    }

    public SwarmInfo? Get(string id) => Query(Read, "SELECT * FROM swarms WHERE id = $id", ("$id", id)).FirstOrDefault();

    public IReadOnlyList<SwarmInfo> List(int limit = 20) => Query(Read, "SELECT * FROM swarms ORDER BY updated_at DESC LIMIT $n", ("$n", limit));

    public void SetStatus(string id, string status, string? summary = null) =>
        Execute("UPDATE swarms SET status = $s, summary = COALESCE($sum, summary), updated_at = $now WHERE id = $id",
            ("$id", id), ("$s", status), ("$sum", (object?)summary ?? DBNull.Value), ("$now", Now()));

    public void SetLimits(string id, SwarmLimits limits) =>
        Execute("UPDATE swarms SET max_agents = $a, max_tokens = $t, max_concurrency = $c, max_minutes = $m WHERE id = $id",
            ("$id", id), ("$a", limits.MaxAgents), ("$t", limits.MaxTokens), ("$c", limits.MaxConcurrency), ("$m", limits.MaxMinutes));

    public void AddUsage(string id, long tokens, int calls) =>
        Execute("UPDATE swarms SET tokens_used = tokens_used + $t, model_calls = model_calls + $c WHERE id = $id", ("$id", id), ("$t", tokens), ("$c", calls));

    // ---- Agents ----

    public SwarmAgent AddAgent(string swarmId, string name, string role, string sessionId, string workingDirectory, string? branch)
    {
        Execute("INSERT INTO swarm_agents (swarm_id, name, role, status, session_id, cwd, branch) VALUES ($s, $n, $r, 'idle', $sid, $cwd, $b)",
            ("$s", swarmId), ("$n", name), ("$r", role), ("$sid", sessionId), ("$cwd", workingDirectory), ("$b", (object?)branch ?? DBNull.Value));
        return Agents(swarmId).First(a => a.Name == name);
    }

    public IReadOnlyList<SwarmAgent> Agents(string swarmId) =>
        Query(r => new SwarmAgent(r.GetString(r.GetOrdinal("swarm_id")), r.GetString(r.GetOrdinal("name")), r.GetString(r.GetOrdinal("role")),
            r.GetString(r.GetOrdinal("status")), r.GetString(r.GetOrdinal("session_id")), r.GetString(r.GetOrdinal("cwd")),
            r.IsDBNull(r.GetOrdinal("branch")) ? null : r.GetString(r.GetOrdinal("branch"))),
            "SELECT * FROM swarm_agents WHERE swarm_id = $s ORDER BY rowid", ("$s", swarmId));

    public void SetAgentStatus(string swarmId, string name, string status) =>
        Execute("UPDATE swarm_agents SET status = $st WHERE swarm_id = $s AND name = $n", ("$s", swarmId), ("$n", name), ("$st", status));

    // ---- Task board ----

    public SwarmTask CreateTask(string swarmId, string title, string description, IReadOnlyList<int> dependsOn, string? assignee)
    {
        lock (_lock)
        {
            var seq = Convert.ToInt32(Scalar("SELECT COALESCE(MAX(seq), 0) + 1 FROM swarm_tasks WHERE swarm_id = $s", ("$s", swarmId)), CultureInfo.InvariantCulture);
            Execute("""
                INSERT INTO swarm_tasks (swarm_id, seq, title, description, status, assignee, depends_on, updated_at)
                VALUES ($s, $q, $t, $d, 'pending', $a, $dep, $now)
                """,
                ("$s", swarmId), ("$q", seq), ("$t", title), ("$d", description), ("$a", (object?)assignee ?? DBNull.Value),
                ("$dep", string.Join(',', dependsOn)), ("$now", Now()));
            return GetTask(swarmId, seq)!;
        }
    }

    public SwarmTask? GetTask(string swarmId, int seq) => Tasks(swarmId).FirstOrDefault(t => t.Seq == seq);

    public IReadOnlyList<SwarmTask> Tasks(string swarmId) =>
        Query(r => new SwarmTask(r.GetString(r.GetOrdinal("swarm_id")), r.GetInt32(r.GetOrdinal("seq")), r.GetString(r.GetOrdinal("title")),
            r.GetString(r.GetOrdinal("description")), r.GetString(r.GetOrdinal("status")),
            r.IsDBNull(r.GetOrdinal("assignee")) ? null : r.GetString(r.GetOrdinal("assignee")),
            r.GetString(r.GetOrdinal("depends_on")).Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToList(),
            r.IsDBNull(r.GetOrdinal("result")) ? null : r.GetString(r.GetOrdinal("result")),
            r.GetInt32(r.GetOrdinal("lead_ack")) == 1),
            "SELECT * FROM swarm_tasks WHERE swarm_id = $s ORDER BY seq", ("$s", swarmId));

    /// <summary>Takes a pending task for <paramref name="agent"/>: only when its dependencies are done and nobody else holds or is assigned it.</summary>
    public string? Claim(string swarmId, int seq, string agent)
    {
        lock (_lock)
        {
            var tasks = Tasks(swarmId);
            var task = tasks.FirstOrDefault(t => t.Seq == seq);
            if (task is null)
                return $"There is no task {seq}.";
            if (task.Status != TaskState.Pending)
                return $"Task {seq} is {task.Status}{(task.Assignee is null ? "" : $" ({task.Assignee})")}.";
            if (task.Assignee is not null && task.Assignee != agent)
                return $"Task {seq} is assigned to {task.Assignee}.";
            if (task.DependsOn.Where(d => tasks.FirstOrDefault(t => t.Seq == d)?.Status != TaskState.Done).ToList() is { Count: > 0 } open)
                return $"Task {seq} waits for task(s) {string.Join(", ", open)}.";

            Execute("UPDATE swarm_tasks SET status = 'in_progress', assignee = $a, updated_at = $now WHERE swarm_id = $s AND seq = $q",
                ("$s", swarmId), ("$q", seq), ("$a", agent), ("$now", Now()));
            return null;
        }
    }

    public void UpdateTask(string swarmId, int seq, string status, string? result)
    {
        Execute("""
            UPDATE swarm_tasks SET status = $st, result = COALESCE($r, result), lead_ack = CASE WHEN $st IN ('done', 'failed') THEN 0 ELSE lead_ack END, updated_at = $now
            WHERE swarm_id = $s AND seq = $q
            """,
            ("$s", swarmId), ("$q", seq), ("$st", status), ("$r", (object?)result ?? DBNull.Value), ("$now", Now()));
    }

    /// <summary>Gives a task back to the pool, e.g. when its worker failed.</summary>
    public void Release(string swarmId, int seq) =>
        Execute("UPDATE swarm_tasks SET status = 'pending', assignee = NULL, updated_at = $now WHERE swarm_id = $s AND seq = $q AND status = 'in_progress'",
            ("$s", swarmId), ("$q", seq), ("$now", Now()));

    public void Acknowledge(string swarmId, IEnumerable<int> seqs)
    {
        foreach (var seq in seqs)
            Execute("UPDATE swarm_tasks SET lead_ack = 1 WHERE swarm_id = $s AND seq = $q", ("$s", swarmId), ("$q", seq));
    }

    // ---- Messages ----

    public void Send(string swarmId, string from, string to, string text) =>
        Execute("INSERT INTO swarm_messages (swarm_id, from_agent, to_agent, text, created_at) VALUES ($s, $f, $t, $x, $now)",
            ("$s", swarmId), ("$f", from), ("$t", to), ("$x", text), ("$now", Now()));

    public int UnreadCount(string swarmId, string agent) =>
        Convert.ToInt32(Scalar("SELECT COUNT(*) FROM swarm_messages WHERE swarm_id = $s AND to_agent = $a AND delivered = 0", ("$s", swarmId), ("$a", agent)), CultureInfo.InvariantCulture);

    /// <summary>The agent's unread messages, oldest first. They are marked delivered.</summary>
    public IReadOnlyList<SwarmMessage> TakeInbox(string swarmId, string agent)
    {
        lock (_lock)
        {
            var messages = Query(ReadMessage, "SELECT * FROM swarm_messages WHERE swarm_id = $s AND to_agent = $a AND delivered = 0 ORDER BY id", ("$s", swarmId), ("$a", agent));
            Execute("UPDATE swarm_messages SET delivered = 1 WHERE swarm_id = $s AND to_agent = $a AND delivered = 0", ("$s", swarmId), ("$a", agent));
            return messages;
        }
    }

    public IReadOnlyList<SwarmMessage> Messages(string swarmId, int limit = 200) =>
        Query(ReadMessage, "SELECT * FROM (SELECT * FROM swarm_messages WHERE swarm_id = $s ORDER BY id DESC LIMIT $n) ORDER BY id", ("$s", swarmId), ("$n", limit));

    // ---- Shared notes ----

    public void AddNote(string swarmId, string author, string text) =>
        Execute("INSERT INTO swarm_notes (swarm_id, author, text, created_at) VALUES ($s, $a, $t, $now)", ("$s", swarmId), ("$a", author), ("$t", text), ("$now", Now()));

    public IReadOnlyList<SwarmNote> Notes(string swarmId) =>
        Query(r => new SwarmNote(r.GetString(r.GetOrdinal("author")), r.GetString(r.GetOrdinal("text")),
            DateTimeOffset.Parse(r.GetString(r.GetOrdinal("created_at")), CultureInfo.InvariantCulture)),
            "SELECT * FROM swarm_notes WHERE swarm_id = $s ORDER BY id", ("$s", swarmId));

    public void Dispose() => _db.Dispose();

    // ---- Plumbing ----

    private static string Now() => DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);

    private static SwarmInfo Read(SqliteDataReader r) => new(
        r.GetString(r.GetOrdinal("id")), r.GetString(r.GetOrdinal("cwd")), r.GetString(r.GetOrdinal("goal")), r.GetString(r.GetOrdinal("status")),
        r.IsDBNull(r.GetOrdinal("summary")) ? null : r.GetString(r.GetOrdinal("summary")),
        new SwarmLimits(r.GetInt32(r.GetOrdinal("max_agents")), r.GetInt64(r.GetOrdinal("max_tokens")), r.GetInt32(r.GetOrdinal("max_concurrency")), r.GetInt32(r.GetOrdinal("max_minutes"))),
        r.GetInt64(r.GetOrdinal("tokens_used")), r.GetInt32(r.GetOrdinal("model_calls")),
        DateTimeOffset.Parse(r.GetString(r.GetOrdinal("created_at")), CultureInfo.InvariantCulture),
        DateTimeOffset.Parse(r.GetString(r.GetOrdinal("updated_at")), CultureInfo.InvariantCulture));

    private static SwarmMessage ReadMessage(SqliteDataReader r) => new(
        r.GetInt64(r.GetOrdinal("id")), r.GetString(r.GetOrdinal("swarm_id")), r.GetString(r.GetOrdinal("from_agent")), r.GetString(r.GetOrdinal("to_agent")),
        r.GetString(r.GetOrdinal("text")), DateTimeOffset.Parse(r.GetString(r.GetOrdinal("created_at")), CultureInfo.InvariantCulture));

    private List<T> Query<T>(Func<SqliteDataReader, T> read, string sql, params (string Name, object Value)[] args)
    {
        var result = new List<T>();
        lock (_lock)
        {
            using var cmd = Command(sql, args);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                result.Add(read(r));
        }
        return result;
    }

    private object? Scalar(string sql, params (string Name, object Value)[] args)
    {
        lock (_lock)
        {
            using var cmd = Command(sql, args);
            return cmd.ExecuteScalar();
        }
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
