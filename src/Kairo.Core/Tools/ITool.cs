using System.Text.Json;
using Kairo.Core.Models;

namespace Kairo.Core.Tools;

/// <summary>Risk levels from docs/SECURITY-MODEL.md, lowest to highest.</summary>
public enum RiskLevel { Read, Write, Exec, Network, System }

/// <summary>
/// What a specific call would do. <see cref="Subject"/> is what permission rules match against
/// (a full file path, a shell command); <see cref="Summary"/> is shown to the user.
/// </summary>
/// <param name="Detail">
/// Optional text for the UI to show with the call, derived from the input alone (a task list, a plan). The model never sees it.
/// </param>
public sealed record ToolAssessment(RiskLevel Risk, string Subject, string Summary, string? Detail = null);

/// <summary>Diff is shown to the user only (unified format, see <see cref="UnifiedDiff"/>); the model never sees it.</summary>
public sealed record ToolResult(string Content, bool IsError = false, string? Diff = null, IReadOnlyList<ToolImage>? Images = null)
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

    /// <summary>
    /// What the call would change, as a unified diff, for the permission prompt. Best effort: null when there is
    /// nothing to show or the target cannot be read. Must not throw for bad input.
    /// </summary>
    string? Preview(JsonElement input, ToolContext ctx) => null;
}

/// <summary>Lets file-changing tools save a file's previous state so the user can roll the change back.</summary>
public interface IFileHistory
{
    /// <summary>Call before changing <paramref name="path"/>; dispose once the change is written.</summary>
    IDisposable Track(string path);
}

/// <summary>Lets the Task tool start a subagent. Set by the agent loop for each run; null where subagents are not available.</summary>
public interface ISubagents
{
    /// <summary>Runs <paramref name="task"/> in a fresh agent of the given type and returns its final report.</summary>
    Task<ToolResult> RunAsync(string type, string task, CancellationToken ct);
}

/// <summary>A process to run: the program, its arguments and how long it may take.</summary>
public sealed record ProcessLaunch(string FileName, IReadOnlyList<string> Arguments, string WorkingDirectory, int TimeoutMs);

/// <summary>What a finished process left behind: stdout and stderr together, in the order they were written.</summary>
public sealed record ProcessOutcome(int ExitCode, string Output, bool TimedOut);

/// <summary>
/// Runs commands in a restricted environment (the Windows AppContainer, see docs/SECURITY-MODEL.md → Песочница). When a session has one,
/// <c>Shell</c> uses it instead of starting a process with the user's full rights.
/// </summary>
public interface IProcessSandbox
{
    /// <summary>Folders the sandboxed commands may change. File tools use the same list.</summary>
    IReadOnlyList<string> WritablePaths { get; }

    /// <summary>Runs the process to the end, the timeout or cancellation, and kills everything it started.</summary>
    Task<ProcessOutcome> RunAsync(ProcessLaunch launch, CancellationToken ct);
}

/// <summary>Per-session state shared by tools.</summary>
public sealed class ToolContext(string workingDirectory)
{
    public string WorkingDirectory { get; } = Path.GetFullPath(workingDirectory);

    /// <summary>Set by the host for each turn; null when nobody keeps checkpoints (the CLI).</summary>
    public IFileHistory? History { get; set; }

    /// <summary>Set by the host when the session is autonomous; null runs commands with the user's own rights.</summary>
    public IProcessSandbox? Sandbox { get; set; }

    /// <summary>Set by the agent loop; the Task tool uses it to start subagents.</summary>
    public ISubagents? Subagents { get; set; }

    /// <summary>Files read in this session. Write/Edit refuse to overwrite a file the agent has not seen.</summary>
    public HashSet<string> ReadFiles { get; } = new(StringComparer.OrdinalIgnoreCase);

    public string ResolvePath(string path) =>
        Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(WorkingDirectory, path));

    public bool IsInsideWorkspace(string fullPath)
    {
        var root = WorkingDirectory.EndsWith(Path.DirectorySeparatorChar) ? WorkingDirectory : WorkingDirectory + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase) || string.Equals(fullPath, WorkingDirectory, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// With a sandbox, files are changed only inside the folders it may write to, the same fence that holds for commands. Returns the
    /// reason to refuse, or null when the change is allowed.
    /// </summary>
    public string? SandboxDenial(string fullPath)
    {
        if (Sandbox is null)
            return null;
        foreach (var root in Sandbox.WritablePaths)
        {
            var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
            if (fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase))
                return null;
        }
        return $"{fullPath} is outside the folders this session may change ({string.Join(", ", Sandbox.WritablePaths.Take(3))}). Work inside them.";
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

    public static double? OptionalNumber(JsonElement input, string name) =>
        input.ValueKind == JsonValueKind.Object && input.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? d : null;

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
