using System.Text.Json;
using Kairo.Core.Tools;
using Kairo.Swarm;

namespace Kairo.Core.Tests;

public sealed class GitReadToolTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("kairo-gitread-").FullName;
    private readonly string _repo;
    private readonly string _worktree;

    public GitReadToolTests()
    {
        _repo = Path.Combine(_root, "main");
        _worktree = Path.Combine(_root, "wt");
        Directory.CreateDirectory(_repo);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private async Task<GitReadTool> InitAsync()
    {
        foreach (var args in new[] { new[] { "init", "-q" }, ["config", "user.name", "t"], ["config", "user.email", "t@t"] })
            await GitWorkspace.RunAsync(_repo, args);
        File.WriteAllText(Path.Combine(_repo, "README.md"), "# demo\n");
        await GitWorkspace.RunAsync(_repo, ["add", "-A"]);
        var (code, output) = await GitWorkspace.RunAsync(_repo, ["commit", "-q", "-m", "init"]);
        Assert.True(code == 0, output);
        Assert.Null(await GitWorkspace.AddWorktreeAsync(_repo, _worktree, "kairo/x/w"));
        return new GitReadTool(_repo, _worktree);
    }

    private static JsonElement Command(string command) => JsonSerializer.SerializeToElement(new { command });

    private ToolContext Ctx => new(_worktree);

    // ---- what is allowed ----

    [Theory]
    [InlineData("status")]
    [InlineData("git status --short")]
    [InlineData("diff --stat")]
    [InlineData("diff --cached --name-only")]
    [InlineData("diff HEAD~1 -- src/a.cs")]
    [InlineData("diff main..kairo/x/w --stat")]
    [InlineData("log --oneline -n 10")]
    [InlineData("log -5 --stat")]
    [InlineData("log --author=\"Ann Lee\" -p -- src/a.cs")]
    [InlineData("log --since=2.weeks --format=%h")]
    [InlineData("log --pretty=format:\"%h %s\"")]
    [InlineData("show HEAD:README.md")]
    [InlineData("show HEAD~2")]
    [InlineData("blame -L1,3 README.md")]
    [InlineData("ls-files --others --exclude-standard")]
    [InlineData("branch -a")]
    [InlineData("rev-parse --abbrev-ref HEAD")]
    public void Reading_commands_are_accepted(string command)
    {
        var tool = new GitReadTool(_repo, _worktree);

        Assert.NotNull(tool.Assess(Command(command), Ctx));
    }

    // ---- what is not ----

    [Theory]
    [InlineData("commit -m x", "not available")]
    [InlineData("push origin main", "not available")]
    [InlineData("checkout .", "not available")]
    [InlineData("reset --hard", "not available")]
    [InlineData("clean -fd", "not available")]
    [InlineData("stash", "not available")]
    [InlineData("config --list", "not available")]
    [InlineData("config core.fsmonitor x", "not available")]
    [InlineData("apply patch.diff", "not available")]
    [InlineData("branch -D other", "not allowed")]
    [InlineData("branch -d other", "not allowed")]
    [InlineData("branch -m other", "not allowed")]
    [InlineData("diff --no-index a b", "not allowed")]
    [InlineData("diff --ext-diff", "not allowed")]
    [InlineData("diff --textconv", "not allowed")]
    [InlineData("blame --contents other.txt README.md", "not allowed")]
    [InlineData("log --output=out.txt", "not allowed")]
    [InlineData("log -c core.pager=evil", "not allowed")]
    [InlineData("log --exec-path=x", "not allowed")]
    [InlineData("show --open-files-in-pager", "not allowed")]
    [InlineData("status --git-dir=x", "not allowed")]
    [InlineData("ls-files --exclude-from=secrets.txt", "not allowed")]
    [InlineData("status ../other", "above your worktree")]
    [InlineData("diff -- ../other", "above your worktree")]
    [InlineData("show HEAD:../secret", "above your worktree")]
    [InlineData("show C:/Users/me/.ssh/id_rsa", "outside the repository")]
    [InlineData("log -- C:\\Users\\me\\x", "outside the repository")]
    [InlineData("log -- /etc/passwd", "outside the repository")]
    [InlineData("diff :(top)x", "Pathspec magic")]
    [InlineData("log \"unclosed", "not closed")]
    [InlineData("", "Give a git command")]
    public void Anything_that_changes_or_reaches_out_is_refused_with_the_reason(string command, string reason)
    {
        var tool = new GitReadTool(_repo, _worktree);

        var ex = Assert.Throws<ToolInputException>(() => tool.Assess(Command(command), Ctx));

        Assert.Contains(reason, ex.Message);
    }

    [Fact]
    public void A_command_cannot_carry_line_breaks_or_run_long()
    {
        var tool = new GitReadTool(_repo, _worktree);

        Assert.Throws<ToolInputException>(() => tool.Assess(Command("status\nrm -rf x"), Ctx));
        Assert.Throws<ToolInputException>(() => tool.Assess(Command("log " + new string('a', 700)), Ctx));
    }

    // ---- running ----

    [Fact]
    public async Task It_shows_status_log_and_a_file_as_committed()
    {
        var tool = await InitAsync();
        File.WriteAllText(Path.Combine(_worktree, "new.txt"), "hello\n");
        File.WriteAllText(Path.Combine(_worktree, "README.md"), "# changed\n");

        var status = await tool.InvokeAsync(Command("status --short"), Ctx, default);
        var log = await tool.InvokeAsync(Command("log --oneline"), Ctx, default);
        var show = await tool.InvokeAsync(Command("show HEAD:README.md"), Ctx, default);
        var diff = await tool.InvokeAsync(Command("diff"), Ctx, default);

        Assert.Contains("new.txt", status.Content);
        Assert.Contains("README.md", status.Content);
        Assert.Contains("init", log.Content);
        Assert.Contains("# demo", show.Content);
        Assert.Contains("+# changed", diff.Content);
        Assert.False(status.IsError, status.Content);
    }

    [Fact]
    public async Task A_git_error_comes_back_as_an_error_result()
    {
        var tool = await InitAsync();

        var result = await tool.InvokeAsync(Command("show nosuchrevision"), Ctx, default);

        Assert.True(result.IsError);
        Assert.Contains("git exited with", result.Content);
    }

    [Fact]
    public async Task It_works_when_the_worker_rewrote_its_git_file_and_the_hostile_config_never_runs()
    {
        var tool = await InitAsync();
        var evil = Path.Combine(_worktree, "evil");
        Directory.CreateDirectory(Path.Combine(evil, "objects"));
        Directory.CreateDirectory(Path.Combine(evil, "refs"));
        File.WriteAllText(Path.Combine(evil, "HEAD"), "ref: refs/heads/evil\n");
        var marker = Path.Combine(_root, "PWNED.txt");
        File.WriteAllText(Path.Combine(evil, "config"), $"[core]\n\trepositoryformatversion = 0\n\tfsmonitor = echo pwned > \"{marker.Replace('\\', '/')}\"\n");
        File.SetAttributes(Path.Combine(_worktree, ".git"), FileAttributes.Normal);
        File.WriteAllText(Path.Combine(_worktree, ".git"), $"gitdir: {evil.Replace('\\', '/')}\n");

        var status = await tool.InvokeAsync(Command("status --short"), Ctx, default);
        var log = await tool.InvokeAsync(Command("log --oneline"), Ctx, default);

        Assert.False(File.Exists(marker), "the worker's config ran outside the sandbox");
        Assert.False(status.IsError, status.Content);
        Assert.Contains("init", log.Content); // it read the real repository, not the worker's folder
    }

    [Fact]
    public void Its_risk_is_read_and_it_names_the_command()
    {
        var tool = new GitReadTool(_repo, _worktree);

        var assessment = tool.Assess(Command("diff --stat"), Ctx);

        Assert.Equal(RiskLevel.Read, assessment.Risk);
        Assert.Equal("git diff --stat", assessment.Summary);
    }
}
