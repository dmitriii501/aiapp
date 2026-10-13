using System.Text.Json;
using Kairo.Core.Tools;
using Kairo.Core.Tests.Fakes;
using Kairo.Tools;

namespace Kairo.Core.Tests;

public class DiffTests
{
    [Fact]
    public void Identical_texts_have_no_diff()
    {
        Assert.Equal("", UnifiedDiff.Create("a\nb\n", "a\r\nb\r\n"));
    }

    [Fact]
    public void A_changed_line_is_shown_with_context_and_line_numbers()
    {
        var old = string.Join('\n', Enumerable.Range(1, 10).Select(i => $"line{i}"));
        var updated = old.Replace("line5", "LINE5");

        var diff = UnifiedDiff.Create(old, updated);

        Assert.Equal("@@ -2,7 +2,7 @@\n line2\n line3\n line4\n-line5\n+LINE5\n line6\n line7\n line8", diff);
    }

    [Fact]
    public void Distant_changes_become_separate_hunks_and_near_ones_merge()
    {
        var old = string.Join('\n', Enumerable.Range(1, 30).Select(i => $"l{i}"));

        var far = UnifiedDiff.Create(old, old.Replace("l3\n", "X\n").Replace("l28", "Y"));
        var near = UnifiedDiff.Create(old, old.Replace("l3\n", "X\n").Replace("l8\n", "Y\n"));

        Assert.Equal(2, far.Split('\n').Count(l => l.StartsWith("@@")));
        Assert.Single(near.Split('\n'), l => l.StartsWith("@@"));
    }

    [Fact]
    public void A_new_file_is_all_additions()
    {
        var diff = UnifiedDiff.Create("", "one\ntwo\n");

        Assert.Equal("@@ -1,0 +1,2 @@\n+one\n+two", diff);
    }

    [Fact]
    public void Huge_diffs_are_cut_off()
    {
        var diff = UnifiedDiff.Create("", string.Join('\n', Enumerable.Range(0, 1000).Select(i => $"row{i}")));

        var lines = diff.Split('\n');
        Assert.True(lines.Length <= UnifiedDiff.MaxLines + 1);
        Assert.Equal("…", lines[^1]);
    }

    [Fact]
    public async Task Edit_previews_and_returns_the_same_diff()
    {
        using var dir = new TempDir();
        var ctx = new ToolContext(dir.Path);
        var file = Path.Combine(dir.Path, "a.txt");
        File.WriteAllText(file, "alpha\nbeta\ngamma\n");
        ctx.ReadFiles.Add(file);
        var input = JsonDocument.Parse("""{"file_path":"a.txt","old_string":"beta","new_string":"BETA"}""").RootElement;
        var tool = new EditTool();

        var preview = tool.Preview(input, ctx);
        var result = await tool.InvokeAsync(input, ctx, CancellationToken.None);

        Assert.Contains("-beta", preview);
        Assert.Contains("+BETA", preview);
        Assert.Equal(preview, result.Diff);
        Assert.Null(tool.Preview(input, ctx)); // already applied: old_string is gone
    }

    [Fact]
    public async Task Write_diffs_against_the_previous_content()
    {
        using var dir = new TempDir();
        var ctx = new ToolContext(dir.Path);
        var file = Path.Combine(dir.Path, "a.txt");
        File.WriteAllText(file, "one\ntwo\n");
        ctx.ReadFiles.Add(file);
        var input = JsonDocument.Parse("""{"file_path":"a.txt","content":"one\nTWO\n"}""").RootElement;
        var tool = new WriteTool();

        var preview = tool.Preview(input, ctx);
        var result = await tool.InvokeAsync(input, ctx, CancellationToken.None);

        Assert.Equal("@@ -1,2 +1,2 @@\n one\n-two\n+TWO", preview);
        Assert.Equal(preview, result.Diff);
        Assert.DoesNotContain("@@", result.Content); // the model's result stays plain text
    }

    [Fact]
    public void Preview_of_a_missing_file_for_edit_is_null_and_does_not_throw()
    {
        using var dir = new TempDir();
        var input = JsonDocument.Parse("""{"file_path":"nope.txt","old_string":"a","new_string":"b"}""").RootElement;

        Assert.Null(new EditTool().Preview(input, new ToolContext(dir.Path)));
    }
}
