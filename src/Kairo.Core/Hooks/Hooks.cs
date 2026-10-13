using System.Text.Json;

namespace Kairo.Core.Hooks;

public enum HookEvent
{
    /// <summary>Before a tool call is checked and run. Exit code 2 blocks it.</summary>
    PreToolUse,

    /// <summary>After a tool call. Exit code 2 sends the hook's message to the model.</summary>
    PostToolUse,

    /// <summary>When the user sends a prompt. Exit code 2 stops the prompt; text printed on exit code 0 is given to the model.</summary>
    UserPromptSubmit,

    /// <summary>When the model has finished answering. Exit code 2 sends its message to the model and the run goes on.</summary>
    Stop,
}

/// <summary>
/// A hook from the user's config.toml: a command that runs on an event. Hooks are code that runs on the user's machine,
/// so they are read from the user's file only, never from a project.
/// </summary>
/// <param name="Matcher">Tool names the hook applies to, for tool events: <c>Shell</c>, <c>Edit|Write</c>, <c>mcp__*</c>. Empty means all.</param>
public sealed record HookConfig(HookEvent Event, string? Matcher, string Command, int TimeoutSeconds = 30)
{
    public bool Matches(string? toolName)
    {
        if (string.IsNullOrWhiteSpace(Matcher) || Matcher == "*")
            return true;
        if (toolName is null)
            return false;
        return Matcher.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Any(m => m.EndsWith('*') ? toolName.StartsWith(m[..^1], StringComparison.Ordinal) : string.Equals(m, toolName, StringComparison.Ordinal));
    }
}

/// <summary>What a hook is told, as JSON on its standard input.</summary>
public sealed record HookInput(
    HookEvent Event,
    string WorkingDirectory,
    string? ToolName = null,
    JsonElement? ToolInput = null,
    string? ToolResponse = null,
    string? Prompt = null);

/// <param name="Blocked">A hook exited with code 2.</param>
/// <param name="Message">What the hook wrote to its error output when it blocked.</param>
/// <param name="Context">What hooks printed on success (used for UserPromptSubmit).</param>
public sealed record HookOutcome(bool Blocked, string? Message = null, string? Context = null)
{
    public static readonly HookOutcome Nothing = new(false);
}

public interface IHooks
{
    bool Has(HookEvent hookEvent);

    /// <summary>Runs the hooks for the event one after another. The first to block ends it. A hook that fails or times out does not block.</summary>
    Task<HookOutcome> RunAsync(HookInput input, CancellationToken ct);
}
