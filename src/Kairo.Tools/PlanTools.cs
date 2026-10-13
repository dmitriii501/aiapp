using System.Text;
using System.Text.Json;
using Kairo.Core.Models;
using Kairo.Core.Tools;

namespace Kairo.Tools;

/// <summary>
/// The agent's task list for multi-step work. It only reports progress to the user; it touches no files,
/// so it counts as a read and needs no approval.
/// </summary>
public sealed class TodoWriteTool : ITool
{
    public const int MaxTodos = 50;
    public const int MaxContentLength = 200;

    private static readonly string[] Statuses = ["pending", "in_progress", "completed"];

    public ToolSpec Spec { get; } = new(
        "TodoWrite",
        "Keeps a short task list for work with several steps, so the user can follow your progress. Send the whole " +
        "list each time. Mark one task in_progress while you work on it and completed as soon as it is done. " +
        "Do not use it for a single simple task.",
        JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "todos": {
                  "type": "array",
                  "description": "The complete, current task list.",
                  "items": {
                    "type": "object",
                    "properties": {
                      "content": { "type": "string", "description": "What to do, in the imperative." },
                      "status": { "type": "string", "enum": ["pending", "in_progress", "completed"] }
                    },
                    "required": ["content", "status"]
                  }
                }
              },
              "required": ["todos"],
              "additionalProperties": false
            }
            """).RootElement.Clone());

    public ToolAssessment Assess(JsonElement input, ToolContext ctx)
    {
        var todos = Parse(input);
        var done = todos.Count(t => t.Status == "completed");
        // Subject is the working directory so the "read inside the workspace" rule applies.
        return new(RiskLevel.Read, ctx.WorkingDirectory, $"TodoWrite({done}/{todos.Count})", Detail: Serialize(todos));
    }

    public Task<ToolResult> InvokeAsync(JsonElement input, ToolContext ctx, CancellationToken ct)
    {
        var todos = Parse(input);
        return Task.FromResult(new ToolResult(
            $"Todo list updated ({todos.Count(t => t.Status == "completed")} of {todos.Count} done). Continue with the current task."));
    }

    internal sealed record Todo(string Content, string Status);

    private static List<Todo> Parse(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object || !input.TryGetProperty("todos", out var array) || array.ValueKind != JsonValueKind.Array)
            throw new ToolInputException("Missing required array parameter 'todos'.");
        if (array.GetArrayLength() > MaxTodos)
            throw new ToolInputException($"Too many tasks (maximum {MaxTodos}). Merge or drop some.");

        var todos = new List<Todo>();
        foreach (var item in array.EnumerateArray())
        {
            var content = item.ValueKind == JsonValueKind.Object && item.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString()!.Trim() : "";
            var status = item.ValueKind == JsonValueKind.Object && item.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString()! : "";
            if (content.Length == 0)
                throw new ToolInputException("Every task needs a non-empty 'content'.");
            if (!Statuses.Contains(status))
                throw new ToolInputException($"Task status must be one of: {string.Join(", ", Statuses)}.");
            todos.Add(new Todo(content.Length <= MaxContentLength ? content : content[..MaxContentLength] + "…", status));
        }
        return todos;
    }

    /// <summary>The cleaned list as JSON, for the UI to render.</summary>
    private static string Serialize(List<Todo> todos)
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartArray();
            foreach (var t in todos)
            {
                w.WriteStartObject();
                w.WriteString("content", t.Content);
                w.WriteString("status", t.Status);
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}

/// <summary>
/// Ends plan mode: shows the user a plan and asks to go ahead. The permission engine always asks for this tool in plan
/// mode; the agent loop switches the mode according to the answer.
/// </summary>
public sealed class ExitPlanModeTool : ITool
{
    public const int MaxPlanChars = 20_000;

    public ToolSpec Spec { get; } = new(
        "ExitPlanMode",
        "Use this only in plan mode, when you have finished investigating and your plan is ready. Pass the plan as " +
        "markdown: what you will change and why, file by file, and how you will verify it. The user approves it, " +
        "or sends you back to plan further. Do not start changing anything before it is approved.",
        ToolInput.Schema(("plan", "string", "The plan, in markdown.", true)));

    public ToolAssessment Assess(JsonElement input, ToolContext ctx)
    {
        var plan = ToolInput.RequiredString(input, "plan").Trim();
        if (plan.Length == 0)
            throw new ToolInputException("The plan is empty.");
        var shown = plan.Length <= MaxPlanChars ? plan : plan[..MaxPlanChars] + "\n…";
        return new(RiskLevel.Read, ctx.WorkingDirectory, "ExitPlanMode", Detail: shown);
    }

    public Task<ToolResult> InvokeAsync(JsonElement input, ToolContext ctx, CancellationToken ct) =>
        Task.FromResult(new ToolResult("The user approved the plan. Start implementing it now."));
}
