using System.Text.Json.Serialization;

namespace Kairo.Protocol;

/// <summary>
/// Every method the UI may call. Methods express user intent; there is deliberately no method that
/// runs a command, reads or writes a file on the UI's behalf (see docs/SECURITY-MODEL.md).
/// </summary>
public static class Methods
{
    public const string AppInfo = "app.info";

    public const string SettingsGet = "settings.get";
    public const string SettingsUpdate = "settings.update";
    /// <summary>Stores or deletes an API key. Keys are write-only from the UI's side.</summary>
    public const string SecretSet = "secret.set";

    /// <summary>Opens the native folder picker. Only folders the user picked (or used before) can host a session.</summary>
    public const string WorkspacePick = "workspace.pick";
    public const string WorkspaceList = "workspace.list";

    public const string SessionList = "session.list";
    public const string SessionCreate = "session.create";
    public const string SessionOpen = "session.open";
    public const string SessionSend = "session.send";
    public const string SessionCancel = "session.cancel";
    public const string SessionSetMode = "session.setMode";

    public const string PermissionRespond = "permission.respond";
}

public static class Notifications
{
    /// <summary><see cref="AgentEventDto"/> for a running turn.</summary>
    public const string AgentEvent = "agent.event";

    /// <summary>The session list changed (created, renamed, started or finished running).</summary>
    public const string SessionsChanged = "sessions.changed";
}

public sealed record AppInfo(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("runtime")] string Runtime,
    [property: JsonPropertyName("os")] string Os,
    [property: JsonPropertyName("host")] string Host);

public sealed record Empty;

public sealed record Ok(bool Success = true);

// ---- Settings ----

public sealed record ProviderStatus(string Id, bool HasKey);

public sealed record Settings(
    string Provider,
    string Model,
    string? BaseUrl,
    string? Effort,
    string Mode,
    IReadOnlyList<ProviderStatus> Providers,
    bool Ready);

/// <summary>Null fields stay unchanged. Effort "default" clears it.</summary>
public sealed record SettingsUpdate(
    string? Provider = null,
    string? Model = null,
    string? BaseUrl = null,
    string? Effort = null,
    string? Mode = null);

/// <summary>An empty <see cref="Key"/> deletes the stored key.</summary>
public sealed record SecretSetParams(string Provider, string Key);

// ---- Workspaces and sessions ----

public sealed record Workspace(string Path, string Name);

public sealed record WorkspacePickResult(Workspace? Workspace);

public sealed record WorkspaceListResult(IReadOnlyList<Workspace> Workspaces);

public sealed record SessionSummary(
    string Id,
    string Title,
    string WorkingDirectory,
    string UpdatedAt,
    bool Running);

public sealed record SessionListResult(IReadOnlyList<SessionSummary> Sessions);

public sealed record SessionCreateParams(string WorkingDirectory);

public sealed record SessionRef(string SessionId);

/// <summary>A row of the rendered conversation. Kind: user | assistant | tool | error.</summary>
public sealed record TranscriptItem(
    string Kind,
    string? Text = null,
    string? ToolCallId = null,
    string? Tool = null,
    string? Summary = null,
    string? Output = null,
    string? Status = null,
    string? Diff = null);

public sealed record SessionDetail(
    SessionSummary Session,
    IReadOnlyList<TranscriptItem> Items,
    string Mode,
    string Model,
    IReadOnlyList<PermissionPrompt> PendingPermissions);

public sealed record SessionSendParams(string SessionId, string Text);

public sealed record SessionModeParams(string SessionId, string Mode);

// ---- Permissions ----

/// <summary>A tool call waiting for the user. Risk: read | write | exec | network | system.</summary>
/// <summary>Diff: unified diff of what a write or edit would change, when there is one.</summary>
public sealed record PermissionPrompt(string RequestId, string Tool, string Summary, string Subject, string Risk, string Reason, string? Diff = null);

/// <summary>Decision: allow | always | deny.</summary>
public sealed record PermissionRespondParams(string RequestId, string Decision);

// ---- Agent events ----

/// <summary>
/// One streamed event of a turn. Type: turn_started | text | tool_started | tool_finished | permission |
/// permission_resolved | error | turn_finished. Only the fields relevant to the type are set.
/// </summary>
public sealed record AgentEventDto(
    string SessionId,
    string Type,
    string? Text = null,
    string? ToolCallId = null,
    string? Tool = null,
    string? Summary = null,
    string? Output = null,
    string? Status = null,
    PermissionPrompt? Permission = null,
    string? RequestId = null,
    string? Reason = null,
    long? InputTokens = null,
    long? OutputTokens = null,
    string? Diff = null);
