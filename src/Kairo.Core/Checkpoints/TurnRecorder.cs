using System.Security.Cryptography;
using Kairo.Core.Tools;

namespace Kairo.Core.Checkpoints;

/// <summary>
/// Records files for the checkpoint of one turn. Write and Edit call <see cref="Track"/> before they change a file;
/// the checkpoint itself is created only when the first file is recorded, so turns that change nothing leave no trace.
/// </summary>
public sealed class TurnRecorder(CheckpointStore store, string sessionId, int turn) : IFileHistory
{
    /// <summary>Files larger than this are not kept: rolling them back is reported as unavailable.</summary>
    public const int MaxFileBytes = 2 * 1024 * 1024;

    private readonly Lock _gate = new();
    private CheckpointInfo? _checkpoint;

    /// <summary>Raised after a file was recorded, with the checkpoint and how many files it holds now.</summary>
    public event Action<CheckpointInfo>? Recorded;

    public IDisposable Track(string path)
    {
        var full = Path.GetFullPath(path);
        CheckpointInfo checkpoint;
        lock (_gate)
        {
            _checkpoint ??= store.GetOrCreate(sessionId, turn);
            checkpoint = _checkpoint;
        }

        if (File.Exists(full))
        {
            var length = new FileInfo(full).Length;
            if (length > MaxFileBytes)
                store.AddSnapshot(checkpoint.Id, full, existed: true, content: null, tooLarge: true);
            else
                store.AddSnapshot(checkpoint.Id, full, existed: true, content: File.ReadAllBytes(full), tooLarge: false);
        }
        else
        {
            store.AddSnapshot(checkpoint.Id, full, existed: false, content: null, tooLarge: false);
        }

        return new Scope(this, checkpoint.Id, full);
    }

    /// <summary>Hash of a file's bytes, or "-" when it does not exist.</summary>
    public static string HashOf(string fullPath) =>
        File.Exists(fullPath) ? HashOf(File.ReadAllBytes(fullPath)) : "-";

    public static string HashOf(byte[] content) => Convert.ToHexString(SHA256.HashData(content));

    private sealed class Scope(TurnRecorder owner, string checkpointId, string path) : IDisposable
    {
        public void Dispose()
        {
            owner.FinishWrite(checkpointId, path);
        }
    }

    private void FinishWrite(string checkpointId, string path)
    {
        // The write is done: remember what the file looks like now, so a later change by someone else shows up as a conflict.
        store.SetAfterHash(checkpointId, path, HashOf(path));
        var info = store.Get(checkpointId);
        if (info is not null)
            Recorded?.Invoke(info);
    }
}
