using System.Text.Json;
using Kairo.Core.Checkpoints;
using Kairo.Core.Sessions;
using Kairo.Core.Tests.Fakes;
using Kairo.Core.Tools;
using Kairo.Tools;

namespace Kairo.Core.Tests;

public class CheckpointTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly SessionStore _store;
    private readonly string _session;

    public CheckpointTests()
    {
        _store = new SessionStore(Path.Combine(_dir.Path, "data.db"));
        _session = _store.Create(_dir.Path, "m", "t").Id;
    }

    public void Dispose()
    {
        _store.Dispose();
        _dir.Dispose();
    }

    private string P(string name) => Path.Combine(_dir.Path, name);

    /// <summary>What the agent's Write does: record, then change.</summary>
    private void AgentWrites(TurnRecorder turn, string name, string content)
    {
        using (turn.Track(P(name)))
            File.WriteAllText(P(name), content);
    }

    private TurnRecorder Turn(int n) => new(_store.Checkpoints, _session, n);

    private CheckpointInfo Checkpoint(int turn) => _store.Checkpoints.Find(_session, turn)!;

    [Fact]
    public void A_turn_that_changes_nothing_leaves_no_checkpoint()
    {
        _ = Turn(1);

        Assert.Empty(_store.Checkpoints.List(_session));
    }

    [Fact]
    public void Rolling_back_a_turn_restores_changed_files_and_deletes_created_ones()
    {
        File.WriteAllText(P("old.txt"), "original");
        var turn = Turn(1);

        AgentWrites(turn, "old.txt", "changed");
        AgentWrites(turn, "new.txt", "created");
        AgentWrites(turn, "old.txt", "changed again"); // the second change must not replace the saved original

        var preview = Rollback.Preview(_store.Checkpoints, Checkpoint(1));
        Assert.Equal([RollbackAction.Delete, RollbackAction.Restore], preview.Select(f => f.Action)); // new.txt, old.txt
        Assert.Contains("+original", preview.Single(f => f.Path == P("old.txt")).Diff);

        var result = Rollback.Apply(_store.Checkpoints, Checkpoint(1), preview.Select(f => f.Path).ToList(), overwriteConflicts: false);

        Assert.Equal((1, 1), (result.Restored, result.Deleted));
        Assert.Equal("original", File.ReadAllText(P("old.txt")));
        Assert.False(File.Exists(P("new.txt")));
    }

    [Fact]
    public void Rolling_back_an_earlier_turn_also_undoes_later_turns_but_a_later_one_keeps_the_earlier()
    {
        File.WriteAllText(P("a.txt"), "v0");
        AgentWrites(Turn(1), "a.txt", "v1");
        AgentWrites(Turn(2), "a.txt", "v2");
        AgentWrites(Turn(2), "b.txt", "b2");

        // Back to before turn 2: turn 1's change stays.
        var second = Checkpoint(2);
        Rollback.Apply(_store.Checkpoints, second, Rollback.Preview(_store.Checkpoints, second).Select(f => f.Path).ToList(), false);
        Assert.Equal("v1", File.ReadAllText(P("a.txt")));
        Assert.False(File.Exists(P("b.txt")));

        // Back to before turn 1: everything is undone.
        var first = Checkpoint(1);
        var preview = Rollback.Preview(_store.Checkpoints, first);
        Assert.Equal(RollbackAction.Restore, Assert.Single(preview, f => f.Action != RollbackAction.Unchanged).Action);
        Rollback.Apply(_store.Checkpoints, first, preview.Select(f => f.Path).ToList(), false);
        Assert.Equal("v0", File.ReadAllText(P("a.txt")));
    }

    [Fact]
    public void Only_the_chosen_files_are_rolled_back()
    {
        File.WriteAllText(P("keep.txt"), "k0");
        File.WriteAllText(P("undo.txt"), "u0");
        var turn = Turn(1);
        AgentWrites(turn, "keep.txt", "k1");
        AgentWrites(turn, "undo.txt", "u1");

        Rollback.Apply(_store.Checkpoints, Checkpoint(1), [P("undo.txt")], false);

        Assert.Equal("k1", File.ReadAllText(P("keep.txt")));
        Assert.Equal("u0", File.ReadAllText(P("undo.txt")));
    }

    [Fact]
    public void A_file_changed_after_the_agent_is_a_conflict_that_is_skipped_unless_overwriting_is_allowed()
    {
        File.WriteAllText(P("a.txt"), "v0");
        AgentWrites(Turn(1), "a.txt", "agent");
        File.WriteAllText(P("a.txt"), "the user edited this afterwards");
        var checkpoint = Checkpoint(1);

        var preview = Rollback.Preview(_store.Checkpoints, checkpoint);
        var skipped = Rollback.Apply(_store.Checkpoints, checkpoint, [P("a.txt")], overwriteConflicts: false);

        Assert.Equal(RollbackAction.Conflict, Assert.Single(preview).Action);
        Assert.Equal([P("a.txt")], skipped.Skipped);
        Assert.Equal("the user edited this afterwards", File.ReadAllText(P("a.txt")));

        var forced = Rollback.Apply(_store.Checkpoints, checkpoint, [P("a.txt")], overwriteConflicts: true);

        Assert.Equal(1, forced.Restored);
        Assert.Equal("v0", File.ReadAllText(P("a.txt")));
    }

    [Fact]
    public void Rolling_back_twice_is_harmless()
    {
        File.WriteAllText(P("a.txt"), "v0");
        AgentWrites(Turn(1), "a.txt", "v1");
        var checkpoint = Checkpoint(1);
        Rollback.Apply(_store.Checkpoints, checkpoint, [P("a.txt")], false);

        var again = Rollback.Preview(_store.Checkpoints, checkpoint);
        var result = Rollback.Apply(_store.Checkpoints, checkpoint, [P("a.txt")], false);

        Assert.Equal(RollbackAction.Unchanged, Assert.Single(again).Action);
        Assert.Equal((0, 0), (result.Restored, result.Deleted));
        Assert.Empty(result.Skipped);
        Assert.Equal("v0", File.ReadAllText(P("a.txt")));
    }

    [Fact]
    public void Files_too_large_to_keep_are_reported_as_unavailable()
    {
        File.WriteAllBytes(P("big.bin"), new byte[TurnRecorder.MaxFileBytes + 1]);
        AgentWrites(Turn(1), "big.bin", "small now");

        var preview = Rollback.Preview(_store.Checkpoints, Checkpoint(1));

        Assert.Equal(RollbackAction.Unavailable, Assert.Single(preview).Action);
        Assert.Equal("small now", File.ReadAllText(P("big.bin")));
        Rollback.Apply(_store.Checkpoints, Checkpoint(1), [P("big.bin")], true);
        Assert.Equal("small now", File.ReadAllText(P("big.bin"))); // never touched
    }

    [Fact]
    public void Recording_reports_each_changed_file_count()
    {
        var turn = Turn(1);
        var counts = new List<int>();
        turn.Recorded += c => counts.Add(c.Files);

        AgentWrites(turn, "a.txt", "1");
        AgentWrites(turn, "a.txt", "2");
        AgentWrites(turn, "b.txt", "1");

        Assert.Equal([1, 1, 2], counts);
        Assert.Equal(2, Assert.Single(_store.Checkpoints.List(_session)).Files);
    }

    [Fact]
    public async Task The_write_and_edit_tools_record_what_they_change()
    {
        File.WriteAllText(P("a.txt"), "hello world");
        var turn = Turn(1);
        var ctx = new ToolContext(_dir.Path) { History = turn };
        ctx.ReadFiles.Add(P("a.txt"));

        await new EditTool().InvokeAsync(JsonDocument.Parse("""{"file_path":"a.txt","old_string":"world","new_string":"Kairo"}""").RootElement, ctx, default);
        await new WriteTool().InvokeAsync(JsonDocument.Parse("""{"file_path":"sub/new.txt","content":"x"}""").RootElement, ctx, default);

        Assert.Equal("hello Kairo", File.ReadAllText(P("a.txt")));
        var checkpoint = Checkpoint(1);
        var preview = Rollback.Preview(_store.Checkpoints, checkpoint);
        Assert.Equal([RollbackAction.Restore, RollbackAction.Delete], preview.Select(f => f.Action));

        Rollback.Apply(_store.Checkpoints, checkpoint, preview.Select(f => f.Path).ToList(), false);

        Assert.Equal("hello world", File.ReadAllText(P("a.txt")));
        Assert.False(File.Exists(P("sub/new.txt")));
    }
}
