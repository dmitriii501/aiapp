using System.Diagnostics;
using System.Text;

namespace Kairo.Swarm;

public sealed record MergeResult(bool Merged, string Output, IReadOnlyList<string> Conflicts);

/// <summary>
/// The git work a swarm needs: one worktree and branch per worker, commits in a worker's branch, merges into the main folder.
/// Plain <c>git</c> from PATH; commits use an identity given on the command line so the user's git config is never changed.
/// </summary>
public static class GitWorkspace
{
    private static readonly string[] Identity = ["-c", "user.name=Kairo", "-c", "user.email=kairo@localhost", "-c", "commit.gpgsign=false"];

    public static async Task<(int Code, string Output)> RunAsync(string workingDirectory, IEnumerable<string> args, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["GIT_EDITOR"] = "true";
        foreach (var a in args)
            psi.ArgumentList.Add(a);

        using var process = new Process { StartInfo = psi };
        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return (-1, "git was not found. Install Git for Windows and make sure it is on PATH.");
        }
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }
        return (process.ExitCode, ((await stdout.ConfigureAwait(false)) + (await stderr.ConfigureAwait(false))).Trim());
    }

    /// <summary>A swarm needs a repository with at least one commit, so that workers can branch from it.</summary>
    public static async Task<string?> CheckRepositoryAsync(string directory, CancellationToken ct = default)
    {
        var (code, output) = await RunAsync(directory, ["rev-parse", "--verify", "HEAD"], ct).ConfigureAwait(false);
        if (code == 0)
            return null;
        return output.Contains("git was not found", StringComparison.Ordinal)
            ? output
            : "A swarm works in git worktrees, so the folder must be a git repository with at least one commit.";
    }

    public static async Task<string?> AddWorktreeAsync(string repository, string path, string branch, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var (code, output) = await RunAsync(repository, ["worktree", "add", "-b", branch, path], ct).ConfigureAwait(false);
        return code == 0 ? null : $"Could not create the worktree: {output}";
    }

    /// <summary>
    /// Switches that keep a repository from running things on git's behalf. Git runs <c>core.fsmonitor</c> and similar settings as commands,
    /// and a worker's worktree holds files the worker (which may be sandboxed) wrote: none of those may decide what runs outside the sandbox.
    /// </summary>
    private static readonly string[] NoCommands = ["-c", "core.fsmonitor=false", "-c", "core.untrackedCache=false", "-c", "core.pager=cat", "-c", "diff.external=", "--no-optional-locks"];

    /// <summary>
    /// The git directory git made for <paramref name="worktree"/>, found from the main repository: <c>.git/worktrees/&lt;name&gt;</c>, whose
    /// <c>gitdir</c> file names the worktree. The worktree's own <c>.git</c> file is not used: it sits in a folder the worker writes, and
    /// pointing it at a folder of the worker's own (with a config of its own) would make every git command here run what that config says.
    /// </summary>
    public static async Task<string?> TrustedGitDirAsync(string repository, string worktree, CancellationToken ct = default)
    {
        var (code, common) = await RunAsync(repository, ["rev-parse", "--git-common-dir"], ct).ConfigureAwait(false);
        if (code != 0)
            return null;
        var commonDir = Path.GetFullPath(Path.IsPathRooted(common.Trim()) ? common.Trim() : Path.Combine(repository, common.Trim()));
        var admin = Path.Combine(commonDir, "worktrees");
        if (!Directory.Exists(admin))
            return null;

        var wanted = Normalize(Path.Combine(worktree, ".git"));
        foreach (var dir in Directory.EnumerateDirectories(admin))
        {
            var pointer = Path.Combine(dir, "gitdir");
            if (File.Exists(pointer) && Normalize(File.ReadAllText(pointer).Trim()) == wanted)
                return dir;
        }
        return null;
    }

    private static string Normalize(string path) => Path.GetFullPath(path.Replace('/', '\\')).TrimEnd('\\').ToLowerInvariant();

    /// <summary>
    /// Runs git on a worktree by its trusted git directory and with the switches above. Throws when the directory cannot be verified:
    /// better a commit that does not happen than a command that runs from the worker's config.
    /// </summary>
    public static async Task<(int Code, string Output)> RunInWorktreeAsync(string repository, string worktree, IEnumerable<string> args, CancellationToken ct = default)
    {
        var gitDir = await TrustedGitDirAsync(repository, worktree, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"the git directory of the worktree {worktree} could not be verified from the main repository");
        return await RunAsync(worktree, [$"--git-dir={gitDir}", $"--work-tree={worktree}", .. NoCommands, .. args], ct).ConfigureAwait(false);
    }

    /// <summary>Commits everything in a worktree. Returns false when there was nothing to commit.</summary>
    public static async Task<bool> CommitAllAsync(string repository, string worktree, string message, CancellationToken ct = default)
    {
        await RunInWorktreeAsync(repository, worktree, ["add", "-A"], ct).ConfigureAwait(false);
        var (clean, _) = await RunInWorktreeAsync(repository, worktree, ["diff", "--cached", "--quiet"], ct).ConfigureAwait(false);
        if (clean == 0)
            return false;
        var (code, _) = await RunInWorktreeAsync(repository, worktree, [.. Identity, "commit", "-m", message], ct).ConfigureAwait(false);
        return code == 0;
    }

    public static async Task<MergeResult> MergeAsync(string repository, string branch, string message, CancellationToken ct = default)
    {
        var (code, output) = await RunAsync(repository, [.. Identity, "merge", "--no-ff", "-m", message, branch], ct).ConfigureAwait(false);
        if (code == 0)
            return new MergeResult(true, output, []);

        var (_, conflicted) = await RunAsync(repository, ["diff", "--name-only", "--diff-filter=U"], ct).ConfigureAwait(false);
        var files = conflicted.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        if (files.Count > 0)
            await RunAsync(repository, ["merge", "--abort"], ct).ConfigureAwait(false);
        return new MergeResult(false, output, files);
    }

    /// <summary>The commits and changed files a branch has over the current branch of <paramref name="repository"/>.</summary>
    public static async Task<string> DiffStatAsync(string repository, string branch, CancellationToken ct = default)
    {
        var (_, output) = await RunAsync(repository, ["diff", "--stat", $"HEAD...{branch}"], ct).ConfigureAwait(false);
        return output;
    }

    public static async Task RemoveWorktreeAsync(string repository, string path, CancellationToken ct = default) =>
        await RunAsync(repository, ["worktree", "remove", "--force", path], ct).ConfigureAwait(false);
}
