using System.Text;
using System.Text.RegularExpressions;
using Kairo.Core.Tools;

namespace Kairo.Core.Permissions;

public enum PermissionDecision { Allow, Ask, Deny }

/// <summary>Session modes from docs/SECURITY-MODEL.md. An autonomous mode exists only inside a sandbox (M3).</summary>
public enum PermissionMode
{
    /// <summary>Reads inside the workspace run; everything else asks.</summary>
    Default,

    /// <summary>Like Default, but file edits inside the workspace run without asking.</summary>
    AcceptEdits,

    /// <summary>Read-only: anything above <see cref="RiskLevel.Read"/> is denied.</summary>
    Plan,
}

/// <summary>
/// A rule like <c>Read</c>, <c>Shell(git status)</c>, <c>Shell(npm test:*)</c> or <c>Read(**/.env)</c>.
/// Shell patterns match the command (a trailing <c>:*</c> means "this prefix"); file patterns are globs
/// matched against the workspace-relative path and the full path. A tool name ending in <c>*</c> covers a family,
/// such as <c>mcp__github__*</c> for every tool of one MCP server.
/// </summary>
public sealed partial record PermissionRule(string Tool, string? Pattern)
{
    public static PermissionRule Parse(string text)
    {
        var m = RuleSyntax().Match(text.Trim());
        if (!m.Success)
            throw new FormatException($"Invalid permission rule '{text}'. Expected 'Tool' or 'Tool(pattern)'.");
        return new PermissionRule(m.Groups["tool"].Value, m.Groups["pattern"].Success ? m.Groups["pattern"].Value : null);
    }

    public bool Matches(string toolName, ToolAssessment call, ToolContext ctx)
    {
        var sameTool = Tool.EndsWith('*')
            ? toolName.StartsWith(Tool[..^1], StringComparison.OrdinalIgnoreCase)
            : string.Equals(Tool, toolName, StringComparison.OrdinalIgnoreCase);
        if (!sameTool)
            return false;
        if (Pattern is null || Pattern == "*")
            return true;

        if (call.Risk == RiskLevel.Exec)
        {
            var command = call.Subject.Trim();
            return Pattern.EndsWith(":*", StringComparison.Ordinal)
                ? command.StartsWith(Pattern[..^2], StringComparison.Ordinal)
                : string.Equals(command, Pattern, StringComparison.Ordinal);
        }

        var regex = GlobToRegex(Pattern);
        var full = call.Subject.Replace('\\', '/');
        return regex.IsMatch(full) || regex.IsMatch(ctx.DisplayPath(call.Subject));
    }

    public override string ToString() => Pattern is null ? Tool : $"{Tool}({Pattern})";

    internal static Regex GlobToRegex(string glob)
    {
        var sb = new StringBuilder("^");
        var g = glob.Replace('\\', '/');
        for (var i = 0; i < g.Length; i++)
        {
            var c = g[i];
            if (c == '*' && i + 1 < g.Length && g[i + 1] == '*')
            {
                // "**/" matches zero or more directories; a bare "**" matches anything.
                if (i + 2 < g.Length && g[i + 2] == '/') { sb.Append("(?:.*/)?"); i += 2; }
                else { sb.Append(".*"); i++; }
            }
            else if (c == '*') sb.Append("[^/]*");
            else if (c == '?') sb.Append("[^/]");
            else sb.Append(Regex.Escape(c.ToString()));
        }
        sb.Append('$');
        return new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    [GeneratedRegex(@"^(?<tool>[A-Za-z_][\w.]*\*?)(?:\((?<pattern>.+)\))?$")]
    private static partial Regex RuleSyntax();
}

public sealed class PermissionRules
{
    public List<PermissionRule> Allow { get; } = [];
    public List<PermissionRule> Ask { get; } = [];
    public List<PermissionRule> Deny { get; } = [];

    /// <summary>Secrets are never read silently, whatever the allow rules say.</summary>
    public static PermissionRules WithDefaults()
    {
        var rules = new PermissionRules();
        rules.Ask.Add(PermissionRule.Parse("Read(**/.env)"));
        rules.Ask.Add(PermissionRule.Parse("Read(**/.env.*)"));
        return rules;
    }
}

public sealed record PermissionVerdict(PermissionDecision Decision, string Reason);

public sealed class PermissionEngine(PermissionRules rules, PermissionMode mode)
{
    public PermissionMode Mode { get; set; } = mode;
    public PermissionRules Rules { get; } = rules;

    public PermissionVerdict Evaluate(string toolName, ToolAssessment call, ToolContext ctx)
    {
        if (Rules.Deny.FirstOrDefault(r => r.Matches(toolName, call, ctx)) is { } deny)
            return new(PermissionDecision.Deny, $"denied by rule {deny}");

        if (Mode == PermissionMode.Plan && call.Risk != RiskLevel.Read)
            return new(PermissionDecision.Deny, "plan mode is read-only");

        if (Rules.Ask.FirstOrDefault(r => r.Matches(toolName, call, ctx)) is { } ask)
            return new(PermissionDecision.Ask, $"rule {ask}");

        if (Rules.Allow.FirstOrDefault(r => r.Matches(toolName, call, ctx)) is { } allow)
            return new(PermissionDecision.Allow, $"allowed by rule {allow}");

        return call.Risk switch
        {
            RiskLevel.Read when ctx.IsInsideWorkspace(call.Subject) => new(PermissionDecision.Allow, "read inside workspace"),
            RiskLevel.Read => new(PermissionDecision.Ask, "read outside workspace"),
            RiskLevel.Write when Mode == PermissionMode.AcceptEdits && ctx.IsInsideWorkspace(call.Subject) => new(PermissionDecision.Allow, "accept-edits mode"),
            _ => new(PermissionDecision.Ask, $"{call.Risk.ToString().ToLowerInvariant()} needs approval"),
        };
    }

    /// <summary>Remembers an "always allow" answer for the rest of the session.</summary>
    public void AllowForSession(string toolName, ToolAssessment call) =>
        Rules.Allow.Add(toolName.StartsWith("mcp__", StringComparison.Ordinal)
            ? new PermissionRule(toolName, null) // the arguments of an MCP call are rarely the same twice
            : new PermissionRule(toolName, call.Risk == RiskLevel.Exec ? call.Subject.Trim() : call.Subject.Replace('\\', '/')));
}
