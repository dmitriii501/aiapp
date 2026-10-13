using System.Text.Json;
using Kairo.Core.Models;

namespace Kairo.Core.Tools;

/// <summary>Risk levels from docs/SECURITY-MODEL.md, lowest to highest.</summary>
public enum RiskLevel { Read, Write, Exec, Network, System }

/// <summary>
/// What a specific call would do. <see cref="Subject"/> is what permission rules match against
/// (a full file path, a shell command); <see cref="Summary"/> is shown to the user.
/// </summary>
public sealed record ToolAssessment(RiskLevel Risk, string Subject, string Summary);

public sealed record ToolResult(string Content, bool IsError = false)
{
    public static ToolResult Error(string message) => new(message, IsError: true);
}

/// <summary>Thrown by tools for bad input; the agent turns it into an error result for the model.</summary>
public sealed class ToolInputException(string message) : Exception(message);

public interface ITool
{
    ToolSpec Spec { get; }

    /// <summary>Classifies a call before it runs. Throws <see cref="ToolInputException"/> on bad input.</summary>
    ToolAssessment Assess(JsonElement input, ToolContext ctx);

    Task<ToolResult> InvokeAsync(JsonElement input, ToolContext ctx, CancellationToken ct);
}

/// <summary>Per-session state shared by tools.</summary>
public sealed class ToolContext(string workingDirectory)
{
    public string WorkingDirectory { get; } = Path.GetFullPath(workingDirectory);

    /// <summary>Files read in this session. Write/Edit refuse to overwrite a file the agent has not seen.</summary>
    public HashSet<string> ReadFiles { get; } = new(StringComparer.OrdinalIgnoreCase);

    public string ResolvePath(string path) =>
        Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(WorkingDirectory, path));

    public bool IsInsideWorkspace(string fullPath)
    {
        var root = WorkingDirectory.EndsWith(Path.DirectorySeparatorChar) ? WorkingDirectory : WorkingDirectory + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase) || string.Equals(fullPath, WorkingDirectory, StringComparison.OrdinalIgnoreCase);
    }

    public string DisplayPath(string fullPath) =>
        IsInsideWorkspace(fullPath) ? Path.GetRelativePath(WorkingDirectory, fullPath).Replace('\\', '/') : fullPath;
}

/// <summary>Helpers for reading tool input without reflection.</summary>
public static class ToolInput
{
    public static string RequiredString(JsonElement input, string name)
    {
        if (input.ValueKind != JsonValueKind.Object || !input.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.String)
            throw new ToolInputException($"Missing required string parameter '{name}'.");
        return v.GetString()!;
    }

    public static string? OptionalString(JsonElement input, string name) =>
        input.ValueKind == JsonValueKind.Object && input.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static int? OptionalInt(JsonElement input, string name) =>
        input.ValueKind == JsonValueKind.Object && input.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;

    public static bool OptionalBool(JsonElement input, string name) =>
        input.ValueKind == JsonValueKind.Object && input.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    /// <summary>Builds a JSON Schema object for a tool from (name, type, description, required) tuples.</summary>
    public static JsonElement Schema(params (string Name, string Type, string Description, bool Required)[] properties)
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("type", "object");
            w.WriteStartObject("properties");
            foreach (var p in properties)
            {
                w.WriteStartObject(p.Name);
                w.WriteString("type", p.Type);
                w.WriteString("description", p.Description);
                w.WriteEndObject();
            }
            w.WriteEndObject();
            w.WriteStartArray("required");
            foreach (var p in properties.Where(p => p.Required))
                w.WriteStringValue(p.Name);
            w.WriteEndArray();
            w.WriteBoolean("additionalProperties", false);
            w.WriteEndObject();
        }
        return JsonDocument.Parse(buffer.ToArray()).RootElement.Clone();
    }
}
