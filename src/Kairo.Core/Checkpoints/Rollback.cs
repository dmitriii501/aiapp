using System.Text;
using Kairo.Core.Tools;

namespace Kairo.Core.Checkpoints;

/// <summary>
/// What a rollback would do to one file. Restore and Delete are plain; Conflict means the file changed after the agent's
/// last write (by the user or by a command), so rolling back would discard that change too.
/// </summary>
public enum RollbackAction { Restore, Delete, Conflict, Unchanged, Unavailable }

/// <param name="Path">Full path.</param>
/// <param name="Diff">Unified diff from the file as it is now to what a rollback would leave; null when there is nothing to show.</param>
public sealed record RollbackFile(string Path, RollbackAction Action, string? Diff);

public sealed record RollbackResult(int Restored, int Deleted, IReadOnlyList<string> Skipped);

/// <summary>
/// Returns files to their state before a turn. Rolling back to a turn also undoes that turn's later turns: for every file
/// the state before its first change from that turn on is what comes back. Only files the agent changed through Write and
/// Edit are covered; what commands did is not.
/// </summary>
public static class Rollback
{
    public static IReadOnlyList<RollbackFile> Preview(CheckpointStore store, CheckpointInfo checkpoint)
    {
        var result = new List<RollbackFile>();
        foreach (var plan in Plan(store, checkpoint))
        {
            var diff = plan.Action is RollbackAction.Unchanged or RollbackAction.Unavailable ? null : DiffOf(plan);
            result.Add(new RollbackFile(plan.Path, plan.Action, diff));
        }
        return result;
    }

    /// <param name="paths">Files to roll back (full paths from <see cref="Preview"/>); others are left alone.</param>
    /// <param name="overwriteConflicts">Also roll back files that changed after the agent's last write.</param>
    public static RollbackResult Apply(CheckpointStore store, CheckpointInfo checkpoint, IReadOnlyCollection<string> paths, bool overwriteConflicts)
    {
        var chosen = new HashSet<string>(paths.Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);
        int restored = 0, deleted = 0;
        var skipped = new List<string>();

        foreach (var plan in Plan(store, checkpoint).Where(p => chosen.Contains(p.Path)))
        {
            var allowed = plan.Action is RollbackAction.Restore or RollbackAction.Delete
                || (plan.Action == RollbackAction.Conflict && overwriteConflicts);
            if (!allowed)
            {
                if (plan.Action is not RollbackAction.Unchanged)
                    skipped.Add(plan.Path);
                continue;
            }

            try
            {
                if (plan.Original.Existed)
                {
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(plan.Path)!);
                    File.WriteAllBytes(plan.Path, plan.Original.Content!);
                    restored++;
                }
                else
                {
                    if (File.Exists(plan.Path))
                        File.Delete(plan.Path);
                    deleted++;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                skipped.Add(plan.Path);
            }
        }
        return new RollbackResult(restored, deleted, skipped);
    }

    private sealed record Step(string Path, RollbackAction Action, FileSnapshot Original, byte[]? Current);

    private static List<Step> Plan(CheckpointStore store, CheckpointInfo checkpoint)
    {
        var snapshots = store.SnapshotsFrom(checkpoint.SessionId, checkpoint.Turn);

        // The earliest snapshot of a file is how it looked before this turn. Every state the agent left the file in
        // (one per turn) counts as "its own": a file in any of them was not touched by anyone else.
        var originals = new Dictionary<string, FileSnapshot>(StringComparer.OrdinalIgnoreCase);
        var agentStates = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in snapshots)
        {
            originals.TryAdd(s.Path, s);
            if (!agentStates.TryGetValue(s.Path, out var states))
                agentStates[s.Path] = states = [];
            if (s.AfterHash is { } after)
                states.Add(after);
        }

        var steps = new List<Step>();
        foreach (var (path, original) in originals.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            var current = File.Exists(path) ? File.ReadAllBytes(path) : null;
            var currentHash = current is null ? "-" : TurnRecorder.HashOf(current);
            var originalHash = !original.Existed ? "-" : original.Content is null ? null : TurnRecorder.HashOf(original.Content);

            RollbackAction action;
            if (original.TooLarge || (original.Existed && original.Content is null))
                action = RollbackAction.Unavailable;
            else if (currentHash == originalHash)
                action = RollbackAction.Unchanged;
            else if (!agentStates[path].Contains(currentHash))
                action = RollbackAction.Conflict;
            else
                action = original.Existed ? RollbackAction.Restore : RollbackAction.Delete;

            steps.Add(new Step(path, action, original, current));
        }
        return steps;
    }

    private static string? DiffOf(Step step)
    {
        try
        {
            var now = step.Current is null ? "" : new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(step.Current);
            var then = step.Original.Content is null ? "" : new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(step.Original.Content);
            var diff = UnifiedDiff.Create(now, then);
            return diff.Length == 0 ? null : diff;
        }
        catch (DecoderFallbackException)
        {
            return null; // not text
        }
    }
}
