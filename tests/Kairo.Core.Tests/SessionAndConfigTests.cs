using Kairo.Core.Config;
using Kairo.Core.Context;
using Kairo.Core.Models;
using Kairo.Core.Permissions;
using Kairo.Core.Sessions;
using Kairo.Core.Tests.Fakes;

namespace Kairo.Core.Tests;

public class SessionAndConfigTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Sessions_round_trip_every_message_part()
    {
        IReadOnlyList<ChatMessage> messages =
        [
            ChatMessage.UserText("привет"),
            new(ChatRole.Assistant, [new ThinkingPart("t", "sig"), new RedactedThinkingPart("r"), new TextPart("ok"), new ToolCallPart("c", "Read", Json.Of("""{"file_path":"a"}"""))]),
            new(ChatRole.User, [new ToolResultPart("c", "content", IsError: true)]),
        ];

        string id;
        using (var store = new SessionStore(Path.Combine(_dir.Path, "data.db")))
        {
            var session = store.Create(_dir.Path, "model-x", "title");
            id = session.Id;
            foreach (var m in messages)
                store.Append(id, m);
        }

        using var reopened = new SessionStore(Path.Combine(_dir.Path, "data.db"));
        var loaded = reopened.LoadMessages(id);

        Assert.Equal(messages.Select(m => m.Role), loaded.Select(m => m.Role));
        Assert.Equal(messages[0].Parts, loaded[0].Parts);
        Assert.Equal(messages[1].Parts.Take(3), loaded[1].Parts.Take(3));
        Assert.Equal("a", ((ToolCallPart)loaded[1].Parts[3]).Input.GetProperty("file_path").GetString());
        Assert.Equal(messages[2].Parts, loaded[2].Parts);
        Assert.Equal(id, reopened.Latest(_dir.Path)!.Id);
        Assert.Equal("model-x", reopened.Get(id)!.Model);
    }

    [Fact]
    public void Config_merges_user_and_project_files()
    {
        var user = Path.Combine(_dir.Path, "user");
        Directory.CreateDirectory(user);
        File.WriteAllText(Path.Combine(user, "config.toml"), """
            provider = "anthropic"
            model = "claude-opus-5-5"

            [permissions]
            allow = ["Shell(git status)"]
            """);
        _dir.File(".kairo/config.toml", """
            model = "claude-sonnet-5-5"
            effort = "high"

            [permissions]
            mode = "acceptEdits"
            deny = ["Shell(git push:*)"]

            [openai_compatible]
            base_url = "http://localhost:11434/v1"
            """);

        var config = KairoConfig.Load(_dir.Path, user);
        var rules = config.BuildRules();

        Assert.Equal("anthropic", config.Provider);
        Assert.Equal("claude-sonnet-5-5", config.Model);
        Assert.Equal("high", config.Effort);
        Assert.Equal(PermissionMode.AcceptEdits, config.Mode);
        Assert.Equal("http://localhost:11434/v1", config.BaseUrl);
        Assert.Contains(PermissionRule.Parse("Shell(git status)"), rules.Allow);
        Assert.Contains(PermissionRule.Parse("Shell(git push:*)"), rules.Deny);
        Assert.Equal(2, config.Sources.Count);
    }

    [Fact]
    public void Invalid_config_reports_the_file()
    {
        _dir.File(".kairo/config.toml", "model = ");

        var ex = Assert.Throws<FormatException>(() => KairoConfig.Load(_dir.Path, null));
        Assert.Contains("config.toml", ex.Message);
    }

    [Fact]
    public void Instruction_files_are_collected_outermost_first()
    {
        _dir.File("AGENTS.md", "root rules");
        _dir.File("app/CLAUDE.md", "app rules");
        var app = Path.Combine(_dir.Path, "app");

        var files = SystemPrompt.FindInstructionFiles(app, userConfigDirectory: null)
            .Where(f => f.Path.StartsWith(_dir.Path, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var prompt = SystemPrompt.Build(app, files, DateTimeOffset.Parse("2026-10-13"));

        Assert.Equal(["root rules", "app rules"], files.Select(f => f.Content));
        Assert.True(prompt.IndexOf("root rules", StringComparison.Ordinal) < prompt.IndexOf("app rules", StringComparison.Ordinal));
        Assert.Contains("Date: 2026-10-13", prompt);
    }
}
