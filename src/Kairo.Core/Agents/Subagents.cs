using Kairo.Core.Hooks;
using Kairo.Core.Models;
using Kairo.Core.Permissions;
using Kairo.Core.Tools;

namespace Kairo.Core.Agents;

/// <summary>A kind of subagent: which tools it gets and how it is briefed.</summary>
public sealed record SubagentType(string Name, string Description, Func<ITool, bool> Allows, string Brief);

public static class SubagentTypes
{
    public const string Explore = "explore";
    public const string General = "general";

    /// <summary>Tools a subagent never gets: it cannot start more subagents, own the user's task list or end plan mode.</summary>
    private static readonly HashSet<string> Reserved = new(StringComparer.Ordinal) { "Task", "TodoWrite", "ExitPlanMode" };

    private static readonly HashSet<string> ReadOnly = new(StringComparer.Ordinal) { "Read", "Glob", "Grep", "WebFetch", "WebSearch" };

    public static readonly IReadOnlyList<SubagentType> All =
    [
        new(Explore, "Read-only research: finds and reads code, files and web pages and reports what it found.",
            t => ReadOnly.Contains(t.Spec.Name),
            "You are a research subagent. You can only read and search. Do not try to change anything."),
        new(General, "Does a self-contained job with the same tools as the main agent, including editing files and running commands.",
            t => !Reserved.Contains(t.Spec.Name),
            "You are a subagent doing one self-contained job for the main agent."),
    ];

    public static SubagentType? Find(string name) => All.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Runs a subagent: a separate agent loop with its own history and a smaller tool set that answers with one final message.
/// It shares the parent's permission engine and prompt, so the user approves its actions exactly as they would the parent's,
/// and the parent's mode (plan mode, bypass) applies to it.
/// </summary>
public sealed class SubagentRunner(
    IModelProvider provider,
    ToolRegistry tools,
    PermissionEngine permissions,
    IPermissionPrompt permissionPrompt,
    ToolContext toolContext,
    string systemPrompt,
    AgentOptions options,
    IHooks? hooks = null) : ISubagents
{
    public const int MaxModelCalls = 40;
    public const int MaxResultChars = 30_000;

    public async Task<ToolResult> RunAsync(string type, string task, CancellationToken ct)
    {
        var kind = SubagentTypes.Find(type) ?? throw new ToolInputException(
            $"Unknown subagent type '{type}'. Use one of: {string.Join(", ", SubagentTypes.All.Select(t => t.Name))}.");

        var registry = new ToolRegistry(tools.All.Where(kind.Allows));
        var system = systemPrompt + "\n\n" + kind.Brief
            + " You start with no memory of the conversation: everything you need is in the task you were given."
            + " Work on your own, then finish with a single message that holds your findings, since that message is all the"
            + " main agent will see. Be specific: file paths, line numbers, names, and what you did or did not verify.";

        var agent = new Agent(provider, registry, permissions, permissionPrompt, toolContext, system,
            options with { MaxModelCalls = MaxModelCalls, Subagent = true }) { Hooks = hooks };

        var toolCalls = 0;
        string? stopped = null;
        await foreach (var ev in agent.RunAsync(task, ct: ct).ConfigureAwait(false))
        {
            switch (ev)
            {
                case ToolCallStarted:
                    toolCalls++;
                    break;
                case AgentFinished f when f.Reason is "max_model_calls" or "refusal":
                    stopped = f.Reason;
                    break;
            }
        }

        var answer = agent.History.LastOrDefault(m => m.Role == ChatRole.Assistant)?.Text.Trim() ?? "";
        if (answer.Length == 0 && stopped == "max_model_calls")
            return ToolResult.Error($"The subagent reached its step limit ({MaxModelCalls} model calls) before it wrote a report. Try a narrower task.");
        if (answer.Length == 0)
            return ToolResult.Error($"The subagent ended without a report{(stopped is null ? "" : $" ({stopped})")}.");
        if (answer.Length > MaxResultChars)
            answer = answer[..MaxResultChars] + "\n…(report cut)";

        var footer = $"\n\n[subagent '{kind.Name}': {toolCalls} tool call(s)"
            + (stopped == "max_model_calls" ? ", stopped at the step limit, the report may be incomplete" : "")
            + "]";
        return new ToolResult(answer + footer);
    }
}
