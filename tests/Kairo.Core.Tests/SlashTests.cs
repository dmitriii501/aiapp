using System.Collections.Concurrent;
using Kairo.Core.Context;
using Kairo.Core.Models;
using Kairo.Core.Permissions;
using Kairo.Core.Rpc;
using Kairo.Core.Secrets;
using Kairo.Core.Sessions;
using Kairo.Core.Tests.Fakes;
using Kairo.Core.Tools;
using Kairo.Host;
using Kairo.Protocol;
using Kairo.Tools;
using static Kairo.Core.Tests.Fakes.ScriptedProvider;

namespace Kairo.Core.Tests;

public class SkillTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private string User => Path.Combine(_dir.Path, "user");
    private string Project => Path.Combine(_dir.Path, "project");

    private void AddSkill(string root, string folder, string content) =>
        _dir.File(Path.Combine(root.Substring(_dir.Path.Length + 1), "skills", folder, "SKILL.md"), content);

    [Fact]
    public void Skills_are_found_in_the_user_and_project_folders_and_the_project_wins_a_clash()
    {
        AddSkill(User, "review", "---\nname: review\ndescription: Review a diff\n---\nUser version.");
        AddSkill(User, "deploy", "---\ndescription: \"Ship it\"\n---\nSteps.");
        _dir.File(@"project\.kairo\skills\review\SKILL.md", "---\nname: review\ndescription: Project review\n---\nProject version.");

        var skills = SkillCatalog.Discover(Project, User);

        Assert.Equal(["deploy", "review"], skills.Select(s => s.Name));
        Assert.Equal("Ship it", skills[0].Description);
        Assert.Equal("Project review", skills[1].Description);
        Assert.Equal("Project version.", skills[1].Body);
        Assert.True(skills[1].FromProject);
    }

    [Fact]
    public void A_skill_without_frontmatter_uses_its_folder_and_first_line()
    {
        _dir.File(@"user\skills\notes\SKILL.md", "# Take notes\n\nWrite them down.");
        _dir.File(@"user\skills\bad name\SKILL.md", "---\nname: ../evil\n---\nx");

        var skill = Assert.Single(SkillCatalog.Discover(Project, User));

        Assert.Equal("notes", skill.Name);
        Assert.Equal("Take notes", skill.Description);
    }

    [Fact]
    public void The_system_prompt_lists_skills_and_memory()
    {
        AddSkill(User, "review", "---\ndescription: Review a diff\n---\nBody.");
        Memory.Append(User, "I prefer tabs");

        var prompt = SystemPrompt.Build(Project, SystemPrompt.FindInstructionFiles(Project, User), DateTimeOffset.Parse("2026-10-13"), PermissionMode.Default, SkillCatalog.Discover(Project, User));

        Assert.Contains("- review: Review a diff", prompt);
        Assert.Contains("I prefer tabs", prompt);
    }

    [Fact]
    public async Task The_skill_tool_returns_the_body_and_needs_no_approval()
    {
        AddSkill(User, "review", "---\ndescription: Review a diff\n---\nCheck the tests first.");
        var tool = new SkillTool(User);
        var ctx = new ToolContext(Project);
        var input = Json.Of("""{"name":"review"}""");

        var assessment = tool.Assess(input, ctx);
        var result = await tool.InvokeAsync(input, ctx, default);

        Assert.Equal(RiskLevel.Read, assessment.Risk);
        Assert.Contains("Check the tests first.", result.Content);
        Assert.Equal(PermissionDecision.Allow, new PermissionEngine(PermissionRules.WithDefaults(), PermissionMode.Default).Evaluate("Skill", assessment, ctx).Decision);
        var ex = Assert.Throws<ToolInputException>(() => tool.Assess(Json.Of("""{"name":"nope"}"""), ctx));
        Assert.Contains("review", ex.Message);
    }
}

public class SlashCommandTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private string User => Path.Combine(_dir.Path, "user");
    private string Project => Path.Combine(_dir.Path, "project");

    [Theory]
    [InlineData("/compact", "compact", "")]
    [InlineData("/compact keep the plan", "compact", "keep the plan")]
    [InlineData("  /review src/a.cs\nand b.cs", "review", "src/a.cs\nand b.cs")]
    public void Commands_are_parsed(string text, string name, string args)
    {
        Assert.Equal((name, args), SlashCommands.Parse(text));
    }

    [Theory]
    [InlineData("fix the bug")]
    [InlineData("/etc/hosts has a wrong entry")]
    [InlineData("//comment")]
    [InlineData("/ ")]
    public void Other_text_is_not_a_command(string text)
    {
        Assert.Null(SlashCommands.Parse(text));
        Assert.IsType<NotACommand>(SlashCommands.Resolve(text, Project, User));
    }

    [Fact]
    public void A_custom_command_expands_its_arguments_into_a_note()
    {
        _dir.File(@"project\.kairo\commands\fix.md", "---\ndescription: Fix an issue\n---\nFix issue $ARGUMENTS and add a test.");
        _dir.File(@"user\commands\plain.md", "Say hello.");

        var withArgs = Assert.IsType<PromptCommand>(SlashCommands.Resolve("/fix 42", Project, User));
        var appended = Assert.IsType<PromptCommand>(SlashCommands.Resolve("/plain loudly", Project, User));

        Assert.Equal("/fix 42", withArgs.Typed);
        Assert.Contains("Fix issue 42 and add a test.", withArgs.Note);
        Assert.Contains("Say hello.\n\nArguments: loudly", appended.Note);
    }

    [Fact]
    public void A_skill_can_be_run_by_name_and_built_ins_cannot_be_shadowed()
    {
        _dir.File(@"user\skills\review\SKILL.md", "---\ndescription: Review\n---\nLook at the diff.");
        _dir.File(@"user\commands\help.md", "I override help");

        var skill = Assert.IsType<PromptCommand>(SlashCommands.Resolve("/review the last commit", Project, User));
        var help = SlashCommands.Resolve("/help", Project, User);

        Assert.Contains("Look at the diff.", skill.Note);
        Assert.Contains("the last commit", skill.Note);
        Assert.Equal(new BuiltInCommand("help", ""), help);
        Assert.Equal(new UnknownCommand("nothing"), SlashCommands.Resolve("/nothing", Project, User));
    }

    [Fact]
    public void Help_lists_everything()
    {
        _dir.File(@"project\.kairo\commands\fix.md", "---\ndescription: Fix an issue\n---\nx");
        _dir.File(@"user\skills\review\SKILL.md", "---\ndescription: Review\n---\nx");

        var help = SlashCommands.Help(Project, User);

        Assert.Contains("/compact", help);
        Assert.Contains("/fix — Fix an issue", help);
        Assert.Contains("/review — Review", help);
    }
}

public class MemoryTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Notes_are_kept_one_per_line_and_limited()
    {
        Assert.Null(Memory.Append(_dir.Path, "use tabs"));
        Assert.Null(Memory.Append(_dir.Path, "answer in Russian\nplease"));

        var text = Memory.Read(_dir.Path);
        Assert.Contains("- use tabs\n", text);
        Assert.Contains("- answer in Russian please\n", text);
        Assert.NotNull(Memory.Append(_dir.Path, "  "));
        Assert.NotNull(Memory.Append(_dir.Path, new string('x', Memory.MaxNoteChars + 1)));
    }
}

public class SlashHostTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly string _project;
    private readonly string _user;
    private readonly SessionStore _store;
    private readonly SettingsService _settings;
    private readonly ConcurrentQueue<AgentEventDto> _events = new();
    private ScriptedProvider _model = new();

    public SlashHostTests()
    {
        _project = Path.Combine(_dir.Path, "project");
        _user = Path.Combine(_dir.Path, "user");
        Directory.CreateDirectory(_project);
        _store = new SessionStore(Path.Combine(_dir.Path, "data.db"));
        _settings = new SettingsService(_user, new InMemorySecretStore());
    }

    public void Dispose()
    {
        _store.Dispose();
        _dir.Dispose();
    }

    private AgentHost CreateHost() =>
        new(_store, _settings, ev => { _events.Enqueue(ev); return Task.CompletedTask; }, () => Task.CompletedTask, _ => _model);

    private string NewSession(AgentHost host)
    {
        _store.TouchWorkspace(_project);
        return host.Create(_project).Id;
    }

    private async Task<AgentEventDto> WaitFor(string type)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (_events.FirstOrDefault(e => e.Type == type) is { } found)
                return found;
            await Task.Delay(10);
        }
        throw new TimeoutException($"No '{type}' event. Got: {string.Join(", ", _events.Select(e => e.Type))}");
    }

    [Fact]
    public async Task Help_answers_with_a_notice_and_starts_no_turn()
    {
        var host = CreateHost();
        var id = NewSession(host);

        var started = host.Send(id, "/help");

        Assert.False(started);
        Assert.Contains("/compact", (await WaitFor("notice")).Text);
        Assert.Empty(_model.Requests);
        Assert.Empty(_store.LoadMessages(id));
    }

    [Fact]
    public async Task A_custom_command_sends_its_text_as_a_hidden_note_with_the_typed_command_as_the_prompt()
    {
        _dir.File(@"project\.kairo\commands\fix.md", "Fix issue $ARGUMENTS and add a test.");
        _model = new ScriptedProvider(Say("done"));
        var host = CreateHost();
        var id = NewSession(host);

        Assert.True(host.Send(id, "/fix 42"));
        await WaitFor("turn_finished");

        var message = _model.Requests[0].Messages[0];
        Assert.Contains("Fix issue 42 and add a test.", message.Text);
        Assert.Equal("/fix 42", message.UserTypedText);
        Assert.Equal("/fix 42", host.Open(id).Items[0].Text);
    }

    [Fact]
    public async Task Remembered_notes_reach_the_next_system_prompt()
    {
        _model = new ScriptedProvider(Say("ok"));
        var host = CreateHost();
        var id = NewSession(host);

        host.Send(id, "/remember always answer in Russian");
        Assert.Contains("Запомнил", (await WaitFor("notice")).Text);
        host.Send(id, "hello");
        await WaitFor("turn_finished");

        Assert.Contains("always answer in Russian", _model.Requests[0].System);
    }

    [Fact]
    public void An_unknown_command_is_refused_before_anything_is_sent()
    {
        var host = CreateHost();
        var id = NewSession(host);

        var ex = Assert.Throws<RpcException>(() => host.Send(id, "/nothing"));

        Assert.Contains("/help", ex.Message);
        Assert.Empty(_model.Requests);
    }
}
