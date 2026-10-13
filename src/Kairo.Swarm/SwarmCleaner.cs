namespace Kairo.Swarm;

/// <summary>Removes what a finished swarm left in the user's repository: the workers' worktrees and their merged branches.</summary>
public static class SwarmCleaner
{
    /// <summary>
    /// Deletes each worker's worktree and its branch. A branch that was not merged into the current branch is kept, so no work is
    /// lost; the report says so. Returns one line per agent.
    /// </summary>
    public static async Task<IReadOnlyList<string>> CleanAsync(SwarmStore store, string swarmId, CancellationToken ct = default)
    {
        var info = store.Get(swarmId) ?? throw new SwarmException($"There is no swarm '{swarmId}'.");
        if (info.Status == SwarmStatus.Running)
            throw new SwarmException("The swarm is running; stop it first.");

        var report = new List<string>();
        foreach (var agent in store.Agents(swarmId).Where(a => a.Branch is not null))
        {
            if (Directory.Exists(agent.WorkingDirectory))
                await GitWorkspace.RemoveWorktreeAsync(info.WorkingDirectory, agent.WorkingDirectory, ct).ConfigureAwait(false);

            // "-d" refuses to delete a branch with commits that are not merged.
            var (code, output) = await GitWorkspace.RunAsync(info.WorkingDirectory, ["branch", "-d", agent.Branch!], ct).ConfigureAwait(false);
            report.Add(code == 0
                ? $"{agent.Name}: worktree and branch {agent.Branch} removed"
                : $"{agent.Name}: worktree removed; branch {agent.Branch} kept because it is not merged ({FirstLine(output)})");
        }

        await GitWorkspace.RunAsync(info.WorkingDirectory, ["worktree", "prune"], ct).ConfigureAwait(false);
        var root = Path.GetDirectoryName(store.Agents(swarmId).FirstOrDefault(a => a.Branch is not null)?.WorkingDirectory ?? "");
        if (root is { Length: > 0 } && Directory.Exists(root) && !Directory.EnumerateFileSystemEntries(root).Any())
            Directory.Delete(root);
        return report;
    }

    private static string FirstLine(string text) => text.Split('\n', 2)[0].Trim();
}
