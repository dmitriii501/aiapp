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

    /// <summary>
    /// Terminals are the one place where the UI's input reaches a shell. It is the user typing, not the agent: the host picks
    /// the shell and the folder (a known workspace), the UI only sends keystrokes. Agent tool calls never go through here.
    /// </summary>
    public const string TerminalOpen = "terminal.open";
    public const string TerminalInput = "terminal.input";
    public const string TerminalResize = "terminal.resize";
    public const string TerminalClose = "terminal.close";

    /// <summary>Current update state. The UI can ask to check and to install, never to choose where an update comes from.</summary>
    public const string UpdateGet = "update.get";
    public const string UpdateCheck = "update.check";
    /// <summary>Downloads the offered update and restarts into it. Refused while an agent is working.</summary>
    public const string UpdateInstall = "update.install";

    public const string McpList = "mcp.list";
    /// <summary>Adds a server to mcp.json. The UI sends what the user typed; the host validates it.</summary>
    public const string McpAdd = "mcp.add";
    public const string McpRemove = "mcp.remove";
    public const string McpSetEnabled = "mcp.setEnabled";
    /// <summary>Reconnects: restarts failed servers and applies edits made to mcp.json by hand.</summary>
    public const string McpReload = "mcp.reload";
    /// <summary>Copies servers from Claude Desktop's config. Existing names are kept.</summary>
    public const string McpImportDesktop = "mcp.importDesktop";
}

public static class Notifications
{
    /// <summary><see cref="AgentEventDto"/> for a running turn.</summary>
    public const string AgentEvent = "agent.event";

    /// <summary>The session list changed (created, renamed, started or finished running).</summary>
    public const string SessionsChanged = "sessions.changed";

    /// <summary><see cref="UpdateStatus"/> whenever the update state changes.</summary>
    public const string UpdateChanged = "update.changed";

    /// <summary><see cref="TerminalOutput"/>: text the shell printed (VT sequences included).</summary>
    public const string TerminalOutputEvent = "terminal.output";

    /// <summary><see cref="TerminalExited"/>: the shell process ended.</summary>
    public const string TerminalExitedEvent = "terminal.exited";

    /// <summary><see cref="McpListResult"/> after a server connected, failed or was edited.</summary>
    public const string McpChanged = "mcp.changed";
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

// ---- MCP ----

/// <summary>Status: connecting | ready | failed | disabled. Transport: stdio | http | file.</summary>
public sealed record McpServerInfo(string Name, string Transport, string Status, string? Error, IReadOnlyList<string> Tools);

/// <summary>Imported: names added by <see cref="Methods.McpImportDesktop"/>, otherwise null.</summary>
public sealed record McpListResult(IReadOnlyList<McpServerInfo> Servers, string ConfigPath, IReadOnlyList<string>? Imported = null);

/// <summary>Command: the whole command line, e.g. <c>npx -y @scope/server C:\proj</c>. Url: for remote servers. Exactly one is set.</summary>
public sealed record McpAddParams(string Name, string? Command = null, string? Url = null);

public sealed record McpNameParams(string Name);

public sealed record McpEnabledParams(string Name, bool Enabled);

// ---- Terminal ----

public sealed record TerminalOpenParams(string WorkingDirectory, int Columns, int Rows);

public sealed record TerminalInfo(string TerminalId, string Title);

public sealed record TerminalInputParams(string TerminalId, string Data);

public sealed record TerminalResizeParams(string TerminalId, int Columns, int Rows);

public sealed record TerminalRef(string TerminalId);

public sealed record TerminalOutput(string TerminalId, string Data);

public sealed record TerminalExited(string TerminalId, int ExitCode);

// ---- Updates ----

/// <summary>
/// State: disabled (not an installed build, or no update feed) | idle | checking | available | downloading | error.
/// Version and Notes describe the offered update; Progress is 0-100 while downloading.
/// </summary>
public sealed record UpdateStatus(string State, string CurrentVersion, string? Version = null, string? Notes = null, int? Progress = null, string? Error = null);
