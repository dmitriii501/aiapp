using System.Text.Json;
using Kairo.Core.Tools;
using Kairo.Tools;

namespace Kairo.Core.Tests;

public sealed class ShellToolTests
{
    private static async Task<ToolResult> Run(string command)
    {
        var input = JsonSerializer.SerializeToElement(new Dictionary<string, object> { ["command"] = command, ["timeout_ms"] = 60_000 });
        return await new ShellTool().InvokeAsync(input, new ToolContext(Path.GetTempPath()), default);
    }

    [Fact]
    public async Task Output_comes_back_as_text_with_cyrillic_intact()
    {
        var result = await Run("'привет'; 'мир'");

        Assert.False(result.IsError);
        Assert.Equal("привет" + Environment.NewLine + "мир", result.Content);
    }

    [Fact]
    public async Task Errors_are_plain_text_not_CLIXML()
    {
        var result = await Run("Write-Error 'boom'; Get-Item 'C:/definitely-not-here'; 'after'");

        Assert.DoesNotContain("CLIXML", result.Content);
        Assert.DoesNotContain("<Objs", result.Content);
        Assert.Contains("boom", result.Content);
        Assert.Contains("definitely-not-here", result.Content);
        Assert.Contains("after", result.Content);
    }

    [Fact]
    public async Task A_throw_is_reported_as_text_and_fails_the_call()
    {
        var result = await Run("throw 'bad thing'");

        Assert.True(result.IsError);
        Assert.Contains("bad thing", result.Content);
        Assert.DoesNotContain("CLIXML", result.Content);
    }

    [Theory]
    [InlineData("cmd /c exit 5", 5)]
    [InlineData("exit 7", 7)]
    [InlineData("Get-Item 'C:/definitely-not-here'", 1)]
    public async Task The_exit_code_is_the_one_the_command_gives(string command, int code)
    {
        var result = await Run(command);

        Assert.True(result.IsError);
        Assert.StartsWith($"Exit code {code}", result.Content);
    }

    [Fact]
    public async Task Line_numbers_in_messages_are_the_commands_own()
    {
        var result = await Run("'one'\nGet-Item 'C:/definitely-not-here'");

        Assert.Matches(@"(строка|line):2", result.Content.Replace(" ", ""));
    }

}
