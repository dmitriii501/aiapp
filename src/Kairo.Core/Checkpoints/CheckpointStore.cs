using Microsoft.Data.Sqlite;

namespace Kairo.Core.Checkpoints;

/// <summary>One checkpoint: the files the agent changed while answering one prompt (turn) of a session.</summary>
public sealed record CheckpointInfo(string Id, string SessionId, int Turn, int Files);

/// <summary>
/// How a file looked before the agent first changed it in a turn. <see cref="Content"/> is null when the file did not
/// exist, or when it was too large to keep (<see cref="TooLarge"/>). <see cref="AfterHash"/> is the file's hash after the
/// agent's last write in that turn, used to notice later changes made by someone else.
/// </summary>
public sealed record FileSnapshot(string CheckpointId, int Turn, string Path, bool Existed, byte[]? Content, bool TooLarge, string? AfterHash);

/// <summary>Snapshots in the sessions database (<c>checkpoints</c> and <c>checkpoint_files</c> tables).</summary>
public sealed class CheckpointStore
{
    private readonly SqliteConnection _db;
    private readonly Lock _lock;

    internal CheckpointStore(SqliteConnection db, Lock sharedLock)
    {
        _db = db;
        _lock = sharedLock;
    }

    internal const string Schema = """
        CREATE TABLE IF NOT EXISTS checkpoints (
            id          TEXT PRIMARY KEY,
            session_id  TEXT NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
            turn        INTEGER NOT NULL,
            created_at  TEXT NOT NULL
        );
        CREATE UNIQUE INDEX IF NOT EXISTS checkpoints_by_turn ON checkpoints(session_id, turn);
        CREATE TABLE IF NOT EXISTS checkpoint_files (
            checkpoint_id TEXT NOT NULL REFERENCES checkpoints(id) ON DELETE CASCADE,
            path          TEXT NOT NULL COLLATE NOCASE,
            existed       INTEGER NOT NULL,
            content       BLOB,
            too_large     INTEGER NOT NULL DEFAULT 0,
            after_hash    TEXT,
            PRIMARY KEY (checkpoint_id, path)
        );
        """;

    /// <summary>The checkpoint of a turn; created on first use.</summary>
    public CheckpointInfo GetOrCreate(string sessionId, int turn)
    {
        lock (_lock)
        {
            using (var insert = Command(
                "INSERT OR IGNORE INTO checkpoints (id, session_id, turn, created_at) VALUES ($id, $session, $turn, $now)",
                ("$id", Guid.NewGuid().ToString("N")[..12]), ("$session", sessionId), ("$turn", turn), ("$now", DateTimeOffset.UtcNow.ToString("O"))))
            {
                insert.ExecuteNonQuery();
            }
            return Find(sessionId, turn)!;
        }
    }

    public CheckpointInfo? Get(string checkpointId)
    {
        lock (_lock)
            return Query("WHERE c.id = $id", ("$id", checkpointId)).FirstOrDefault();
    }

    public CheckpointInfo? Find(string sessionId, int turn)
    {
        lock (_lock)
            return Query("WHERE c.session_id = $session AND c.turn = $turn", ("$session", sessionId), ("$turn", turn)).FirstOrDefault();
    }

    /// <summary>Checkpoints that recorded at least one file, oldest first.</summary>
    public IReadOnlyList<CheckpointInfo> List(string sessionId)
    {
        lock (_lock)
            return Query("WHERE c.session_id = $session", ("$session", sessionId)).Where(c => c.Files > 0).ToList();
    }

    /// <summary>Remembers the file as it is now, unless this turn already did. Returns true when a snapshot was added.</summary>
    public bool AddSnapshot(string checkpointId, string path, bool existed, byte[]? content, bool tooLarge)
    {
        lock (_lock)
        {
            using var cmd = Command("""
                INSERT OR IGNORE INTO checkpoint_files (checkpoint_id, path, existed, content, too_large)
                VALUES ($cp, $path, $existed, $content, $tooLarge)
                """,
                ("$cp", checkpointId), ("$path", path), ("$existed", existed ? 1 : 0), ("$content", (object?)content ?? DBNull.Value), ("$tooLarge", tooLarge ? 1 : 0));
            return cmd.ExecuteNonQuery() == 1;
        }
    }

    public void SetAfterHash(string checkpointId, string path, string hash)
    {
        lock (_lock)
        {
            using var cmd = Command("UPDATE checkpoint_files SET after_hash = $hash WHERE checkpoint_id = $cp AND path = $path",
                ("$hash", hash), ("$cp", checkpointId), ("$path", path));
            cmd.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<FileSnapshot> SnapshotsOf(string checkpointId)
    {
        lock (_lock)
            return Snapshots("WHERE f.checkpoint_id = $cp", ("$cp", checkpointId));
    }

    /// <summary>All snapshots of the session from the given turn on, oldest turn first.</summary>
    public IReadOnlyList<FileSnapshot> SnapshotsFrom(string sessionId, int turn)
    {
        lock (_lock)
            return Snapshots("WHERE c.session_id = $session AND c.turn >= $turn", ("$session", sessionId), ("$turn", turn));
    }

    private List<CheckpointInfo> Query(string where, params (string, object)[] args)
    {
        var result = new List<CheckpointInfo>();
        using var cmd = Command($"""
            SELECT c.id, c.session_id, c.turn, (SELECT COUNT(*) FROM checkpoint_files f WHERE f.checkpoint_id = c.id)
            FROM checkpoints c {where} ORDER BY c.turn
            """, args);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            result.Add(new CheckpointInfo(r.GetString(0), r.GetString(1), r.GetInt32(2), r.GetInt32(3)));
        return result;
    }

    private List<FileSnapshot> Snapshots(string where, params (string, object)[] args)
    {
        var result = new List<FileSnapshot>();
        using var cmd = Command($"""
            SELECT f.checkpoint_id, c.turn, f.path, f.existed, f.content, f.too_large, f.after_hash
            FROM checkpoint_files f JOIN checkpoints c ON c.id = f.checkpoint_id {where} ORDER BY c.turn, f.rowid
            """, args);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            result.Add(new FileSnapshot(
                r.GetString(0), r.GetInt32(1), r.GetString(2), r.GetInt32(3) == 1,
                r.IsDBNull(4) ? null : (byte[])r[4], r.GetInt32(5) == 1, r.IsDBNull(6) ? null : r.GetString(6)));
        }
        return result;
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
