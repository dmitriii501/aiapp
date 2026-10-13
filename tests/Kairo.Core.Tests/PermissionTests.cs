using Kairo.Core.Permissions;
using Kairo.Core.Tools;

namespace Kairo.Core.Tests;

public class PermissionTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "kairo-perm");
    private readonly ToolContext _ctx = new(Root);

    private static ToolAssessment File(RiskLevel risk, string relative) => new(risk, Path.Combine(Root, relative), relative);
    private static ToolAssessment Command(string cmd) => new(RiskLevel.Exec, cmd, cmd);

    private PermissionDecision Decide(PermissionEngine engine, string tool, ToolAssessment call) => engine.Evaluate(tool, call, _ctx).Decision;

    [Fact]
    public void Defaults_allow_workspace_reads_and_ask_for_everything_else()
    {
        var engine = new PermissionEngine(PermissionRules.WithDefaults(), PermissionMode.Default);

        Assert.Equal(PermissionDecision.Allow, Decide(engine, "Read", File(RiskLevel.Read, "src/a.cs")));
        Assert.Equal(PermissionDecision.Ask, Decide(engine, "Read", new ToolAssessment(RiskLevel.Read, @"C:\Windows\win.ini", "")));
        Assert.Equal(PermissionDecision.Ask, Decide(engine, "Edit", File(RiskLevel.Write, "src/a.cs")));
        Assert.Equal(PermissionDecision.Ask, Decide(engine, "Shell", Command("dir")));
    }

    [Fact]
    public void Env_files_always_ask()
    {
        var engine = new PermissionEngine(PermissionRules.WithDefaults(), PermissionMode.Default);

        Assert.Equal(PermissionDecision.Ask, Decide(engine, "Read", File(RiskLevel.Read, ".env")));
        Assert.Equal(PermissionDecision.Ask, Decide(engine, "Read", File(RiskLevel.Read, "app/.env.local")));
    }

    [Fact]
    public void Accept_edits_mode_allows_workspace_writes_only()
    {
        var engine = new PermissionEngine(PermissionRules.WithDefaults(), PermissionMode.AcceptEdits);

        Assert.Equal(PermissionDecision.Allow, Decide(engine, "Edit", File(RiskLevel.Write, "a.cs")));
        Assert.Equal(PermissionDecision.Ask, Decide(engine, "Write", new ToolAssessment(RiskLevel.Write, @"C:\other\a.cs", "")));
        Assert.Equal(PermissionDecision.Ask, Decide(engine, "Shell", Command("dir")));
    }

    [Fact]
    public void Shell_rules_match_exact_commands_and_prefixes()
    {
        var rules = PermissionRules.WithDefaults();
        rules.Allow.Add(PermissionRule.Parse("Shell(git status)"));
        rules.Allow.Add(PermissionRule.Parse("Shell(dotnet test:*)"));
        var engine = new PermissionEngine(rules, PermissionMode.Default);

        Assert.Equal(PermissionDecision.Allow, Decide(engine, "Shell", Command("git status")));
        Assert.Equal(PermissionDecision.Ask, Decide(engine, "Shell", Command("git status; rm -r *")));
        Assert.Equal(PermissionDecision.Allow, Decide(engine, "Shell", Command("dotnet test --no-build")));
    }

    [Fact]
    public void Deny_beats_allow_and_plan_mode_blocks_non_reads()
    {
        var rules = PermissionRules.WithDefaults();
        rules.Allow.Add(PermissionRule.Parse("Shell"));
        rules.Deny.Add(PermissionRule.Parse("Shell(git push:*)"));
        var engine = new PermissionEngine(rules, PermissionMode.Default);
        var plan = new PermissionEngine(PermissionRules.WithDefaults(), PermissionMode.Plan);

        Assert.Equal(PermissionDecision.Allow, Decide(engine, "Shell", Command("git log")));
        Assert.Equal(PermissionDecision.Deny, Decide(engine, "Shell", Command("git push --force")));
        Assert.Equal(PermissionDecision.Deny, Decide(plan, "Edit", File(RiskLevel.Write, "a.cs")));
        Assert.Equal(PermissionDecision.Allow, Decide(plan, "Read", File(RiskLevel.Read, "a.cs")));
    }

    [Fact]
    public void File_rules_use_globs()
    {
        var rules = PermissionRules.WithDefaults();
        rules.Deny.Add(PermissionRule.Parse("Read(**/secrets/**)"));
        rules.Allow.Add(PermissionRule.Parse("Edit(src/**/*.cs)"));
        var engine = new PermissionEngine(rules, PermissionMode.Default);

        Assert.Equal(PermissionDecision.Deny, Decide(engine, "Read", File(RiskLevel.Read, @"config\secrets\key.txt")));
        Assert.Equal(PermissionDecision.Allow, Decide(engine, "Edit", File(RiskLevel.Write, @"src\deep\A.cs")));
        Assert.Equal(PermissionDecision.Ask, Decide(engine, "Edit", File(RiskLevel.Write, @"tests\A.cs")));
    }

    [Theory]
    [InlineData("Read", "Read", null)]
    [InlineData("Shell(npm test:*)", "Shell", "npm test:*")]
    [InlineData("Read(**/.env)", "Read", "**/.env")]
    public void Rules_parse(string text, string tool, string? pattern) =>
        Assert.Equal(new PermissionRule(tool, pattern), PermissionRule.Parse(text));

    [Fact]
    public void Malformed_rules_are_rejected() =>
        Assert.Throws<FormatException>(() => PermissionRule.Parse("Shell(oops"));
}
