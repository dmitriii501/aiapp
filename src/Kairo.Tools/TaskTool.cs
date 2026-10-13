using System.Text.Json;
using Kairo.Core.Agents;
using Kairo.Core.Models;
using Kairo.Core.Tools;

namespace Kairo.Tools;

/// <summary>
/// Hands a self-contained job to a subagent: a separate agent loop with its own context that returns one report. It keeps
/// big searches and side investigations out of the main conversation. The Task call itself needs no approval; the subagent's
/// own tool calls are checked against the same permissions as the main agent's.
/// </summary>
public sealed class TaskTool : ITool
{
    public const int MaxDescriptionChars = 80;

    public ToolSpec Spec { get; } = new(
        "Task",
        "Starts a subagent to do a self-contained job and returns its report. Use it to search or investigate widely " +
        "without filling your own context (\"find where X is handled and how\"), or to carry out a well-defined piece " +
        "of work. The subagent knows nothing about this conversation: put everything it needs in the prompt, and say what " +
        "its report should contain. Types: " + string.Join("; ", SubagentTypes.All.Select(t => $"'{t.Name}' - {t.Description}")) +
        " Default is 'explore'. Do not use it for something one Read or Grep answers.",
        ToolInput.Schema(
            ("description", "string", "A few words naming the job, shown to the user.", true),
            ("prompt", "string", "The complete task for the subagent.", true),
            ("subagent_type", "string", "'explore' (read-only) or 'general'. Default 'explore'.", false)));

    public ToolAssessment Assess(JsonElement input, ToolContext ctx)
    {
        var (type, description, _) = Parse(input);
        // The subagent's own calls are assessed one by one, so the Task call counts as a read.
        return new(RiskLevel.Read, ctx.WorkingDirectory, $"Task({type}: {description})");
    }

    public async Task<ToolResult> InvokeAsync(JsonElement input, ToolContext ctx, CancellationToken ct)
    {
        var (type, _, prompt) = Parse(input);
        if (ctx.Subagents is not { } subagents)
            return ToolResult.Error("Subagents are not available here.");
        return await subagents.RunAsync(type, prompt, ct).ConfigureAwait(false);
    }

    private static (string Type, string Description, string Prompt) Parse(JsonElement input)
    {
        var description = ToolInput.RequiredString(input, "description").Trim().ReplaceLineEndings(" ");
        var prompt = ToolInput.RequiredString(input, "prompt").Trim();
        if (prompt.Length == 0)
            throw new ToolInputException("The prompt is empty.");
        var type = (ToolInput.OptionalString(input, "subagent_type") ?? SubagentTypes.Explore).Trim().ToLowerInvariant();
        if (SubagentTypes.Find(type) is null)
            throw new ToolInputException($"Unknown subagent type '{type}'. Use one of: {string.Join(", ", SubagentTypes.All.Select(t => t.Name))}.");
        return (type, description.Length <= MaxDescriptionChars ? description : description[..MaxDescriptionChars] + "…", prompt);
    }
}
