using System.Text.Json;
using Kairo.Core.Context;
using Kairo.Core.Models;
using Kairo.Core.Tools;

namespace Kairo.Tools;

/// <summary>
/// Loads a skill's instructions. Skills come from the user's skills folder and the project's <c>.kairo\skills</c>; the system
/// prompt lists their names. Loading one only reads text the user or the project put there, so it needs no approval.
/// </summary>
public sealed class SkillTool(string? userConfigDirectory) : ITool
{
    public ToolSpec Spec { get; } = new(
        "Skill",
        "Loads the instructions of a skill listed in the system prompt, to follow while you do the task. Call it before starting " +
        "work the skill covers. The result also names the skill's folder, where it may keep scripts and reference files.",
        ToolInput.Schema(("name", "string", "The skill's name, as listed.", true)));

    public ToolAssessment Assess(JsonElement input, ToolContext ctx)
    {
        var skill = Find(input, ctx);
        // The Subject is the working directory so the "read inside the workspace" rule applies.
        return new(RiskLevel.Read, ctx.WorkingDirectory, $"Skill({skill.Name})");
    }

    public Task<ToolResult> InvokeAsync(JsonElement input, ToolContext ctx, CancellationToken ct)
    {
        var skill = Find(input, ctx);
        return Task.FromResult(new ToolResult(
            $"Skill '{skill.Name}' (folder: {skill.Directory}). Follow these instructions for the task:\n\n{skill.Body}"));
    }

    private Skill Find(JsonElement input, ToolContext ctx)
    {
        var name = ToolInput.RequiredString(input, "name").Trim().TrimStart('/');
        var skills = SkillCatalog.Discover(ctx.WorkingDirectory, userConfigDirectory);
        return skills.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? throw new ToolInputException(skills.Count == 0
                ? $"There is no skill '{name}': no skills are installed."
                : $"There is no skill '{name}'. Available: {string.Join(", ", skills.Select(s => s.Name))}.");
    }
}
