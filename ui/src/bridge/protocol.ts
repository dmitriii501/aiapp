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
  PermissionRespond: "permission.respond",
} as const;

export const Notifications = {
  AgentEvent: "agent.event",
  SessionsChanged: "sessions.changed",
} as const;

export type Mode = "default" | "acceptEdits" | "plan";
export type ProviderId = "anthropic" | "openai-compatible";
export type Decision = "allow" | "always" | "deny";
export type ToolStatus = "running" | "done" | "error" | "denied" | "interrupted";
export type Risk = "read" | "write" | "exec" | "network" | "system";

export interface AppInfo {
  name: string;
  version: string;
  runtime: string;
  os: string;
  host: string;
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
  kind: "user" | "assistant" | "tool" | "error";
  text?: string;
  toolCallId?: string;
  tool?: string;
  summary?: string;
  output?: string;
  status?: ToolStatus;
  /** Unified diff (hunks only) of what a write or edit changed. */
  diff?: string;
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
}

type NoParams = Record<string, never>;

export interface MethodMap {
  [Methods.AppInfo]: { params: NoParams; result: AppInfo };
  [Methods.SettingsGet]: { params: NoParams; result: Settings };
  [Methods.SettingsUpdate]: { params: SettingsUpdate; result: Settings };
  [Methods.SecretSet]: { params: { provider: ProviderId; key: string }; result: Settings };
  [Methods.WorkspacePick]: { params: NoParams; result: { workspace?: Workspace } };
  [Methods.WorkspaceList]: { params: NoParams; result: { workspaces: Workspace[] } };
  [Methods.SessionList]: { params: NoParams; result: { sessions: SessionSummary[] } };
  [Methods.SessionCreate]: { params: { workingDirectory: string }; result: SessionSummary };
  [Methods.SessionOpen]: { params: { sessionId: string }; result: SessionDetail };
  [Methods.SessionSend]: { params: { sessionId: string; text: string }; result: Ok };
  [Methods.SessionCancel]: { params: { sessionId: string }; result: Ok };
  [Methods.SessionSetMode]: { params: { sessionId: string; mode: Mode }; result: Ok };
  [Methods.PermissionRespond]: { params: { requestId: string; decision: Decision }; result: Ok };
}

export interface NotificationMap {
  [Notifications.AgentEvent]: AgentEvent;
  [Notifications.SessionsChanged]: NoParams;
}
