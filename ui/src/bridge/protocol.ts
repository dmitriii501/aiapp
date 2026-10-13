// Mirror of src/Kairo.Protocol. Hand-written until TS generation from C# lands (docs/OPEN-QUESTIONS.md).

export const Methods = {
  AppInfo: "app.info",
  SettingsGet: "settings.get",
  SettingsUpdate: "settings.update",
  SecretSet: "secret.set",
  WorkspacePick: "workspace.pick",
  WorkspaceList: "workspace.list",
  SessionList: "session.list",
  SessionCreate: "session.create",
  SessionOpen: "session.open",
  SessionSend: "session.send",
  SessionCancel: "session.cancel",
  SessionSetMode: "session.setMode",
  SessionCompact: "session.compact",
  PermissionRespond: "permission.respond",
  McpList: "mcp.list",
  McpAdd: "mcp.add",
  McpRemove: "mcp.remove",
  McpSetEnabled: "mcp.setEnabled",
  McpReload: "mcp.reload",
  McpImportDesktop: "mcp.importDesktop",
  CheckpointPreview: "checkpoint.preview",
  CheckpointRollback: "checkpoint.rollback",
  UpdateGet: "update.get",
  UpdateCheck: "update.check",
  UpdateInstall: "update.install",
  TerminalOpen: "terminal.open",
  TerminalInput: "terminal.input",
  TerminalResize: "terminal.resize",
  TerminalClose: "terminal.close",
} as const;

export const Notifications = {
  AgentEvent: "agent.event",
  SessionsChanged: "sessions.changed",
  McpChanged: "mcp.changed",
  UpdateChanged: "update.changed",
  TerminalOutput: "terminal.output",
  TerminalExited: "terminal.exited",
} as const;

export type Mode = "default" | "acceptEdits" | "plan" | "bypass";
export type ProviderId = "anthropic" | "openai-compatible";
export type Decision = "allow" | "always" | "deny";
export type ToolStatus = "running" | "done" | "error" | "denied" | "interrupted";
export type Risk = "read" | "write" | "exec" | "network" | "system" | "plan";

export interface AppInfo {
  name: string;
  version: string;
  runtime: string;
  os: string;
  host: string;
}

/** started is false when the text was a command Kairo ran itself (/help, /remember): no turn began. */
export interface SendResult {
  started: boolean;
}

export interface Ok {
  success: boolean;
}

export interface ProviderStatus {
  id: ProviderId;
  hasKey: boolean;
}

export interface Settings {
  provider: ProviderId;
  model: string;
  baseUrl?: string;
  effort?: string;
  mode: Mode;
  providers: ProviderStatus[];
  ready: boolean;
  /** A Brave Search key is stored (or set in the environment): WebSearch works. */
  braveKey?: boolean;
}

export interface SettingsUpdate {
  provider?: ProviderId;
  model?: string;
  baseUrl?: string;
  effort?: string;
  mode?: Mode;
}

export interface Workspace {
  path: string;
  name: string;
}

export interface SessionSummary {
  id: string;
  title: string;
  workingDirectory: string;
  updatedAt: string;
  running: boolean;
}

export interface TranscriptItem {
  kind: "user" | "assistant" | "tool" | "compaction" | "error";
  text?: string;
  toolCallId?: string;
  tool?: string;
  summary?: string;
  output?: string;
  status?: ToolStatus;
  /** Unified diff (hunks only) of what a write or edit changed. */
  diff?: string;
  /** On a user prompt: the checkpoint holding the files the agent changed while answering it. */
  checkpointId?: string;
  files?: number;
  /** Extra text for a tool call, e.g. the task list of TodoWrite (JSON). */
  detail?: string;
}

export interface PermissionPrompt {
  requestId: string;
  tool: string;
  summary: string;
  subject: string;
  risk: Risk;
  reason: string;
  /** Unified diff of what the call would change, when it is a write or edit. */
  diff?: string;
  /** The plan (markdown), when the agent asks to leave plan mode. */
  detail?: string;
}

export interface SessionDetail {
  session: SessionSummary;
  items: TranscriptItem[];
  mode: Mode;
  model: string;
  pendingPermissions: PermissionPrompt[];
}

export type AgentEventType =
  | "turn_started"
  | "text"
  | "tool_started"
  | "tool_finished"
  | "permission"
  | "permission_resolved"
  | "checkpoint"
  | "mode"
  | "compacting"
  | "compacted"
  | "notice"
  | "error"
  | "turn_finished";

export interface AgentEvent {
  sessionId: string;
  type: AgentEventType;
  text?: string;
  toolCallId?: string;
  tool?: string;
  summary?: string;
  output?: string;
  status?: ToolStatus;
  permission?: PermissionPrompt;
  requestId?: string;
  reason?: string;
  inputTokens?: number;
  outputTokens?: number;
  diff?: string;
  checkpointId?: string;
  files?: number;
  detail?: string;
  /** On "compacted": estimated size of the conversation before and after, in tokens. */
  tokensBefore?: number;
  tokensAfter?: number;
}

export type RollbackAction = "restore" | "delete" | "conflict" | "unchanged" | "unavailable";

export interface CheckpointFile {
  path: string;
  displayPath: string;
  action: RollbackAction;
  /** From how the file is now to how a rollback leaves it. */
  diff?: string;
}

export interface CheckpointPreview {
  checkpointId: string;
  files: CheckpointFile[];
}

export interface CheckpointRollbackResult {
  restored: number;
  deleted: number;
  skipped: string[];
}

export type UpdateState = "disabled" | "idle" | "checking" | "available" | "downloading" | "error";

export interface UpdateStatus {
  state: UpdateState;
  currentVersion: string;
  /** The offered version. */
  version?: string;
  /** Release notes (markdown). */
  notes?: string;
  /** 0-100 while downloading. */
  progress?: number;
  error?: string;
}

export interface TerminalInfo {
  terminalId: string;
  title: string;
}

export type McpStatus = "connecting" | "ready" | "failed" | "disabled";

export interface McpServerInfo {
  name: string;
  transport: "stdio" | "http" | "file";
  status: McpStatus;
  error?: string;
  tools: string[];
}

export interface McpListResult {
  servers: McpServerInfo[];
  configPath: string;
  /** Names added by mcp.importDesktop. */
  imported?: string[];
}

type NoParams = Record<string, never>;

export interface MethodMap {
  [Methods.AppInfo]: { params: NoParams; result: AppInfo };
  [Methods.SettingsGet]: { params: NoParams; result: Settings };
  [Methods.SettingsUpdate]: { params: SettingsUpdate; result: Settings };
  [Methods.SecretSet]: { params: { provider: ProviderId | "brave"; key: string }; result: Settings };
  [Methods.WorkspacePick]: { params: NoParams; result: { workspace?: Workspace } };
  [Methods.WorkspaceList]: { params: NoParams; result: { workspaces: Workspace[] } };
  [Methods.SessionList]: { params: NoParams; result: { sessions: SessionSummary[] } };
  [Methods.SessionCreate]: { params: { workingDirectory: string }; result: SessionSummary };
  [Methods.SessionOpen]: { params: { sessionId: string }; result: SessionDetail };
  [Methods.SessionSend]: { params: { sessionId: string; text: string }; result: SendResult };
  [Methods.SessionCancel]: { params: { sessionId: string }; result: Ok };
  [Methods.SessionSetMode]: { params: { sessionId: string; mode: Mode }; result: Ok };
  [Methods.SessionCompact]: { params: { sessionId: string; focus?: string }; result: Ok };
  [Methods.PermissionRespond]: { params: { requestId: string; decision: Decision }; result: Ok };
  [Methods.McpList]: { params: NoParams; result: McpListResult };
  [Methods.McpAdd]: { params: { name: string; command?: string; url?: string }; result: McpListResult };
  [Methods.McpRemove]: { params: { name: string }; result: McpListResult };
  [Methods.McpSetEnabled]: { params: { name: string; enabled: boolean }; result: McpListResult };
  [Methods.McpReload]: { params: NoParams; result: McpListResult };
  [Methods.McpImportDesktop]: { params: NoParams; result: McpListResult };
  [Methods.CheckpointPreview]: { params: { checkpointId: string }; result: CheckpointPreview };
  [Methods.CheckpointRollback]: { params: { checkpointId: string; paths: string[]; overwriteConflicts: boolean }; result: CheckpointRollbackResult };
  [Methods.UpdateGet]: { params: NoParams; result: UpdateStatus };
  [Methods.UpdateCheck]: { params: NoParams; result: UpdateStatus };
  [Methods.UpdateInstall]: { params: NoParams; result: Ok };
  [Methods.TerminalOpen]: { params: { workingDirectory: string; columns: number; rows: number }; result: TerminalInfo };
  [Methods.TerminalInput]: { params: { terminalId: string; data: string }; result: Ok };
  [Methods.TerminalResize]: { params: { terminalId: string; columns: number; rows: number }; result: Ok };
  [Methods.TerminalClose]: { params: { terminalId: string }; result: Ok };
}

export interface NotificationMap {
  [Notifications.AgentEvent]: AgentEvent;
  [Notifications.SessionsChanged]: NoParams;
  [Notifications.McpChanged]: McpListResult;
  [Notifications.UpdateChanged]: UpdateStatus;
  [Notifications.TerminalOutput]: { terminalId: string; data: string };
  [Notifications.TerminalExited]: { terminalId: string; exitCode: number };
}
