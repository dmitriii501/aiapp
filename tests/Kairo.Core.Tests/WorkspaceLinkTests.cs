using System.Diagnostics;
using Kairo.Core.Tools;

namespace Kairo.Core.Tests;

/// <summary>
/// A sandboxed command can make a junction inside its folder (no rights are needed) that leads anywhere. The file tools run outside the
/// sandbox with the user's rights, so "inside the folder" has to mean where the path really goes.
/// </summary>
public sealed class WorkspaceLinkTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("kairo-links-").FullName;
    private readonly string _work;
    private readonly string _outside;

    public WorkspaceLinkTests()
    {
        _work = Path.Combine(_root, "work");
        _outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(Path.Combine(_work, "src"));
        Directory.CreateDirectory(_outside);
        File.WriteAllText(Path.Combine(_outside, "secret.txt"), "secret");
    }

    public void Dispose()
    {
        // Remove the junction itself first: deleting through it would delete what it points at.
        var link = Path.Combine(_work, "link");
        if (Directory.Exists(link))
            Directory.Delete(link);
        try { Directory.Delete(_root, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private void MakeJunction(string link, string target)
    {
        var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using var process = Process.Start(psi)!;
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, process.StandardError.ReadToEnd());
    }

    [Fact]
    public void A_plain_path_inside_the_folder_is_inside_and_a_path_that_does_not_exist_yet_is_too()
    {
        var ctx = new ToolContext(_work);

        Assert.True(ctx.IsInsideWorkspace(Path.Combine(_work, "src", "a.cs")));
        Assert.True(ctx.IsInsideWorkspace(Path.Combine(_work, "new", "deeper", "b.cs")));
        Assert.True(ctx.IsInsideWorkspace(_work));
        Assert.False(ctx.IsInsideWorkspace(Path.Combine(_outside, "secret.txt")));
        Assert.False(ctx.IsInsideWorkspace(_work + "-sibling"));
    }

    [Fact]
    public void A_junction_inside_the_folder_that_leads_out_is_outside()
    {
        MakeJunction(Path.Combine(_work, "link"), _outside);
        var ctx = new ToolContext(_work);

        Assert.False(ctx.IsInsideWorkspace(Path.Combine(_work, "link", "secret.txt")));
        Assert.False(ctx.IsInsideWorkspace(Path.Combine(_work, "link", "new-file.txt"))); // writing a new file through it, too
        Assert.False(ctx.IsInsideWorkspace(Path.Combine(_work, "link")));
        Assert.True(ctx.IsInsideWorkspace(Path.Combine(_work, "src", "a.cs")));
    }

    [Fact]
    public void A_junction_that_leads_to_another_place_inside_the_folder_stays_inside()
    {
        MakeJunction(Path.Combine(_work, "link"), Path.Combine(_work, "src"));
        var ctx = new ToolContext(_work);

        Assert.True(ctx.IsInsideWorkspace(Path.Combine(_work, "link", "a.cs")));
    }

    [Fact]
    public void The_sandboxs_writable_folders_are_judged_by_the_real_path_as_well()
    {
        MakeJunction(Path.Combine(_work, "link"), _outside);
        var ctx = new ToolContext(_work) { Sandbox = new FakeBox([_work]) };

        Assert.Null(ctx.SandboxDenial(Path.Combine(_work, "src", "a.cs")));
        Assert.NotNull(ctx.SandboxDenial(Path.Combine(_work, "link", "secret.txt")));
    }

    [Fact]
    public void A_working_folder_that_is_itself_reached_through_a_junction_still_contains_its_files()
    {
        var alias = Path.Combine(_root, "alias");
        MakeJunction(alias, _work);
        try
        {
            var ctx = new ToolContext(alias);

            Assert.True(ctx.IsInsideWorkspace(Path.Combine(alias, "src", "a.cs")));
            Assert.True(ctx.IsInsideWorkspace(Path.Combine(_work, "src", "a.cs")));
            Assert.False(ctx.IsInsideWorkspace(Path.Combine(_outside, "secret.txt")));
        }
        finally
        {
            Directory.Delete(alias);
        }
    }

    [Fact]
    public void Something_that_is_not_a_path_does_not_throw()
    {
        var ctx = new ToolContext(_work);

        Assert.False(ctx.IsInsideWorkspace("git status --short | foo\0bar"));
    }

    private sealed class FakeBox(IReadOnlyList<string> writable) : IProcessSandbox
    {
        public IReadOnlyList<string> WritablePaths => writable;

        public Task<ProcessOutcome> RunAsync(ProcessLaunch launch, CancellationToken ct) => throw new NotSupportedException();
    }
}
