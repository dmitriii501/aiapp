using System.Text.Json;
using Kairo.Core.Agents;
using Kairo.Core.Models;
using Kairo.Core.Tools;

namespace Kairo.Tools;

/// <summary>Ends the active <c>/goal</c>: the model says the goal is reached and what it checked. Does nothing outside a goal.</summary>
public sealed class GoalCompleteTool : ITool
{
    public ToolSpec Spec { get; } = new(
        "GoalComplete",
        "Call this only while a /goal is active, when the goal is truly achieved and you checked the result yourself. evidence: what you " +
        "checked and what you saw (a test run that passes, the file you read, the screen you looked at). Kairo stops pushing you on after it. " +
        "Do not call it to end a turn that is not finished.",
        ToolInput.Schema(("evidence", "string", "What you checked and saw that shows the goal is reached.", true)));

    public ToolAssessment Assess(JsonElement input, ToolContext ctx) => new(RiskLevel.Read, ctx.WorkingDirectory, "GoalComplete");

    public Task<ToolResult> InvokeAsync(JsonElement input, ToolContext ctx, CancellationToken ct)
    {
        var evidence = ToolInput.RequiredString(input, "evidence").Trim();
        if (ctx.Goal is not { Status: GoalStatus.Active } goal)
            return Task.FromResult(ToolResult.Error("There is no active goal. This tool is only for /goal."));
        if (evidence.Length < 12)
            return Task.FromResult(ToolResult.Error("evidence is too short: say what you checked and what you saw."));
        goal.Status = GoalStatus.Complete;
        goal.Detail = evidence;
        return Task.FromResult(new ToolResult("The goal is marked complete. Tell the user in a sentence or two what was done."));
    }
}

/// <summary>Stops the active <c>/goal</c> because only the user can go on.</summary>
public sealed class GoalBlockedTool : ITool
{
    public ToolSpec Spec { get; } = new(
        "GoalBlocked",
        "Call this only while a /goal is active, when you cannot go on without the user: a decision that is theirs, a missing password, key or file, " +
        "a permission they refused, something that needs their hands. Say exactly what you need. Do not use it for a problem you can still work around.",
        ToolInput.Schema(("reason", "string", "What you need from the user.", true)));

    public ToolAssessment Assess(JsonElement input, ToolContext ctx) => new(RiskLevel.Read, ctx.WorkingDirectory, "GoalBlocked");

    public Task<ToolResult> InvokeAsync(JsonElement input, ToolContext ctx, CancellationToken ct)
    {
        var reason = ToolInput.RequiredString(input, "reason").Trim();
        if (ctx.Goal is not { Status: GoalStatus.Active } goal)
            return Task.FromResult(ToolResult.Error("There is no active goal. This tool is only for /goal."));
        goal.Status = GoalStatus.Blocked;
        goal.Detail = reason;
        return Task.FromResult(new ToolResult("The user was told you need their help. Stop here and wait."));
    }
}
