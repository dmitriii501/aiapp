using Kairo.Core.Tests.Fakes;
using Kairo.Core.Tools;
using Kairo.Tools;

namespace Kairo.Core.Tests;

public class ToolTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly ToolContext _ctx;

    public ToolTests() => _ctx = new ToolContext(_dir.Path);

    public void Dispose() => _dir.Dispose();

    private Task<ToolResult> Invoke(ITool tool, string json) => tool.InvokeAsync(Json.Of(json), _ctx, CancellationToken.None);

    [Fact]
    public async Task Read_numbers_lines_and_pages()
    {
        _dir.File("f.txt", "a\nb\nc\nd");

        var all = await Invoke(new ReadTool(), """{"file_path":"f.txt"}""");
        var page = await Invoke(new ReadTool(), """{"file_path":"f.txt","offset":2,"limit":2}""");

        Assert.Contains("     1\ta\n", all.Content);
        Assert.Contains("     4\td\n", all.Content);
        Assert.StartsWith("     2\tb\n     3\tc\n", page.Content);
        Assert.Contains("use offset=4", page.Content);
    }

    [Fact]
    public async Task Read_rejects_binary_files()
    {
        await File.WriteAllBytesAsync(Path.Combine(_dir.Path, "b.bin"), [1, 0, 2, 3]);

        var ex = await Assert.ThrowsAsync<ToolInputException>(() => Invoke(new ReadTool(), """{"file_path":"b.bin"}"""));
        Assert.Contains("binary", ex.Message);
    }

    [Fact]
    public async Task Edit_requires_a_prior_read()
    {
        _dir.File("f.txt", "x");

        var result = await Invoke(new EditTool(), """{"file_path":"f.txt","old_string":"x","new_string":"y"}""");

        Assert.True(result.IsError);
        Assert.Contains("Read it first", result.Content);
    }

    [Fact]
    public async Task Edit_refuses_ambiguous_matches_unless_replace_all()
    {
        var path = _dir.File("f.txt", "foo foo");
        await Invoke(new ReadTool(), """{"file_path":"f.txt"}""");

        var ambiguous = await Invoke(new EditTool(), """{"file_path":"f.txt","old_string":"foo","new_string":"bar"}""");
        var all = await Invoke(new EditTool(), """{"file_path":"f.txt","old_string":"foo","new_string":"bar","replace_all":true}""");

        Assert.True(ambiguous.IsError);
        Assert.Contains("2 times", ambiguous.Content);
        Assert.False(all.IsError);
        Assert.Equal("bar bar", File.ReadAllText(path));
    }

    [Fact]
    public async Task Edit_preserves_crlf_line_endings()
    {
        var path = _dir.File("f.cs", "class A\r\n{\r\n    int x;\r\n}\r\n");
        await Invoke(new ReadTool(), """{"file_path":"f.cs"}""");

        var result = await Invoke(new EditTool(), """{"file_path":"f.cs","old_string":"{\n    int x;","new_string":"{\n    int y;"}""");

        Assert.False(result.IsError, result.Content);
        Assert.Equal("class A\r\n{\r\n    int y;\r\n}\r\n", File.ReadAllText(path));
    }

    [Fact]
    public async Task Write_creates_directories_but_will_not_blindly_overwrite()
    {
        _dir.File("exists.txt", "old");

        var created = await Invoke(new WriteTool(), """{"file_path":"sub/dir/new.txt","content":"hi"}""");
        var blind = await Invoke(new WriteTool(), """{"file_path":"exists.txt","content":"new"}""");

        Assert.False(created.IsError);
        Assert.Equal("hi", File.ReadAllText(Path.Combine(_dir.Path, "sub", "dir", "new.txt")));
        Assert.True(blind.IsError);
        Assert.Equal("old", File.ReadAllText(Path.Combine(_dir.Path, "exists.txt")));
    }

    [Fact]
    public async Task Glob_finds_files_and_skips_build_output()
    {
        _dir.File("src/A.cs", "");
        _dir.File("src/sub/B.cs", "");
        _dir.File("bin/Debug/C.cs", "");
        _dir.File("readme.md", "");

        var result = await Invoke(new GlobTool(), """{"pattern":"**/*.cs"}""");

        var files = result.Content.Split('\n').Order().ToArray();
        Assert.Equal(["src/A.cs", "src/sub/B.cs"], files);
    }

    [Fact]
    public async Task Grep_lists_files_or_matching_lines()
    {
        _dir.File("a.txt", "alpha\nTODO: fix\n");
        _dir.File("b.txt", "nothing here");

        var files = await Invoke(new GrepTool(), """{"pattern":"todo","case_insensitive":true}""");
        var lines = await Invoke(new GrepTool(), """{"pattern":"TODO","output_mode":"content"}""");
        var bad = await Invoke(new GrepTool(), """{"pattern":"(unclosed"}""");

        Assert.Equal("a.txt", files.Content);
        Assert.Equal("a.txt:2: TODO: fix", lines.Content);
        Assert.True(bad.IsError);
    }

    [Fact]
    public async Task Shell_runs_powershell_in_the_working_directory_with_utf8()
    {
        var result = await Invoke(new ShellTool(), """{"command":"Write-Output 'Привет'; (Get-Location).Path"}""");

        Assert.False(result.IsError, result.Content);
        Assert.Contains("Привет", result.Content);
        Assert.Contains(Path.GetFileName(_dir.Path), result.Content);
    }

    [Fact]
    public async Task Shell_reports_nonzero_exit_codes()
    {
        var result = await Invoke(new ShellTool(), """{"command":"Write-Error 'boom'; exit 3"}""");

        Assert.True(result.IsError);
        Assert.StartsWith("Exit code 3", result.Content);
    }

    [Fact]
    public async Task Shell_times_out()
    {
        var result = await Invoke(new ShellTool(), """{"command":"Start-Sleep -Seconds 30","timeout_ms":1000}""");

        Assert.True(result.IsError);
        Assert.Contains("timed out", result.Content);
    }
}
