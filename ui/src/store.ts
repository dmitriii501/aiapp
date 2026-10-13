import { create } from "zustand";
import { bridge, errorMessage } from "./bridge/bridge";
import type {
  AgentEvent,
  Decision,
  Mode,
  PermissionPrompt,
  ProviderId,
  SessionSummary,
  Settings,
  SettingsUpdate,
  ToolStatus,
  Workspace,
} from "./bridge/protocol";
import { Methods, Notifications } from "./bridge/protocol";

export type Item =
  | { id: string; kind: "user"; text: string }
  | { id: string; kind: "assistant"; text: string; streaming: boolean }
  | { id: string; kind: "tool"; toolCallId: string; tool: string; summary: string; output?: string; diff?: string; status: ToolStatus | "waiting" }
  | { id: string; kind: "permission"; prompt: PermissionPrompt }
  | { id: string; kind: "error"; text: string }
  | { id: string; kind: "notice"; text: string };

export interface Chat {
  items: Item[];
  running: boolean;
  mode: Mode;
  model: string;
  /** When the current turn started (ms), for the elapsed-time indicator. */
  startedAt?: number;
  /** Tokens used by the last finished turn. */
  usage?: { input: number; output: number };
}

export const MODES: Mode[] = ["default", "acceptEdits", "plan"];

interface State {
  booted: boolean;
  settings?: Settings;
  sessions: SessionSummary[];
  workspaces: Workspace[];
  currentId?: string;
  chats: Record<string, Chat>;
  settingsOpen: boolean;
  toast?: string;

  boot(): Promise<void>;
  openSession(id: string): Promise<void>;
  closeSession(): void;
  startSession(path: string): Promise<void>;
  pickFolder(): Promise<void>;
  send(text: string): Promise<void>;
  cancel(): Promise<void>;
  setMode(mode: Mode): Promise<void>;
  cycleMode(): Promise<void>;
  respond(requestId: string, decision: Decision): Promise<void>;
  saveSettings(update: SettingsUpdate, key?: { provider: ProviderId; key: string }): Promise<boolean>;
  setSettingsOpen(open: boolean): void;
  showToast(text?: string): void;
}

let seq = 0;
const nextId = () => `i${++seq}`;

export const useStore = create<State>((set, get) => {
  const patchChat = (sessionId: string, update: (chat: Chat) => Chat) =>
    set((s) => (s.chats[sessionId] ? { chats: { ...s.chats, [sessionId]: update(s.chats[sessionId]) } } : {}));

  const fail = (e: unknown) => set({ toast: errorMessage(e) });

  const refreshSessions = async () => {
    const { sessions } = await bridge.call(Methods.SessionList, {});
    set({ sessions });
  };

  const refreshWorkspaces = async () => {
    const { workspaces } = await bridge.call(Methods.WorkspaceList, {});
    set({ workspaces });
  };

  const onEvent = (e: AgentEvent) =>
    patchChat(e.sessionId, (chat) => {
      const items = [...chat.items];
      const last = items[items.length - 1];
      const endStreaming = () => {
        if (last?.kind === "assistant" && last.streaming) items[items.length - 1] = { ...last, streaming: false };
      };

      switch (e.type) {
        case "turn_started":
          return { ...chat, running: true, startedAt: chat.startedAt ?? Date.now() };
        case "text":
          if (last?.kind === "assistant" && last.streaming) items[items.length - 1] = { ...last, text: last.text + (e.text ?? "") };
          else items.push({ id: nextId(), kind: "assistant", text: e.text ?? "", streaming: true });
          return { ...chat, items };
        case "tool_started":
          endStreaming();
          items.push({ id: nextId(), kind: "tool", toolCallId: e.toolCallId!, tool: e.tool!, summary: e.summary ?? e.tool!, status: "running" });
          return { ...chat, items };
        case "tool_finished":
          return {
            ...chat,
            items: items.map((i) => (i.kind === "tool" && i.toolCallId === e.toolCallId ? { ...i, status: e.status ?? "done", output: e.output, diff: e.diff } : i)),
          };
        case "permission":
          endStreaming();
          items.push({ id: nextId(), kind: "permission", prompt: e.permission! });
          return { ...chat, items: items.map((i) => (i.kind === "tool" && i.status === "running" ? { ...i, status: "waiting" as const } : i)) };
        case "permission_resolved":
          return {
            ...chat,
            items: items
              .filter((i) => !(i.kind === "permission" && i.prompt.requestId === e.requestId))
              .map((i) => (i.kind === "tool" && i.status === "waiting" ? { ...i, status: "running" as const } : i)),
          };
        case "error":
          endStreaming();
          items.push({ id: nextId(), kind: "error", text: e.text ?? "Неизвестная ошибка" });
          return { ...chat, items };
        case "turn_finished":
          endStreaming();
          if (e.reason === "cancelled") items.push({ id: nextId(), kind: "notice", text: "Остановлено" });
          if (e.reason === "max_model_calls") items.push({ id: nextId(), kind: "notice", text: "Достигнут лимит шагов. Напишите «продолжай», чтобы агент продолжил." });
          if (e.reason === "refusal") items.push({ id: nextId(), kind: "notice", text: "Модель отказалась выполнять запрос." });
          return {
            ...chat,
            running: false,
            startedAt: undefined,
            usage: { input: e.inputTokens ?? 0, output: e.outputTokens ?? 0 },
            items: items.map((i) => (i.kind === "tool" && (i.status === "running" || i.status === "waiting") ? { ...i, status: "interrupted" as const } : i)),
          };
      }
    });

  return {
    booted: false,
    sessions: [],
    workspaces: [],
    chats: {},
    settingsOpen: false,

    async boot() {
      bridge.on(Notifications.AgentEvent, onEvent);
      bridge.on(Notifications.SessionsChanged, () => void refreshSessions().catch(fail));
      try {
        const settings = await bridge.call(Methods.SettingsGet, {});
        set({ settings });
        await Promise.all([refreshSessions(), refreshWorkspaces()]);
      } catch (e) {
        fail(e);
      }
      set({ booted: true });
    },

    async openSession(id) {
      set({ currentId: id });
      try {
        const detail = await bridge.call(Methods.SessionOpen, { sessionId: id });
        const items: Item[] = detail.items.map((t) => {
          switch (t.kind) {
            case "user":
              return { id: nextId(), kind: "user", text: t.text ?? "" };
            case "assistant":
              return { id: nextId(), kind: "assistant", text: t.text ?? "", streaming: false };
            case "tool":
              return { id: nextId(), kind: "tool", toolCallId: t.toolCallId!, tool: t.tool!, summary: t.summary ?? t.tool!, output: t.output, diff: t.diff, status: t.status ?? "done" };
            default:
              return { id: nextId(), kind: "error", text: t.text ?? "" };
          }
        });
        if (detail.pendingPermissions.length > 0) {
          const last = items.findLast((i) => i.kind === "tool");
          if (last?.kind === "tool") last.status = "waiting";
        }
        for (const prompt of detail.pendingPermissions) items.push({ id: nextId(), kind: "permission", prompt });
        set((s) => ({
          chats: { ...s.chats, [id]: { items, running: detail.session.running, mode: detail.mode, model: detail.model } },
        }));
      } catch (e) {
        fail(e);
      }
    },

    closeSession: () => set({ currentId: undefined }),

    async startSession(path) {
      try {
        const session = await bridge.call(Methods.SessionCreate, { workingDirectory: path });
        await refreshSessions();
        await get().openSession(session.id);
      } catch (e) {
        fail(e);
      }
    },

    async pickFolder() {
      try {
        const { workspace } = await bridge.call(Methods.WorkspacePick, {});
        if (!workspace) return;
        await refreshWorkspaces();
        await get().startSession(workspace.path);
      } catch (e) {
        fail(e);
      }
    },

    async send(text) {
      const id = get().currentId;
      if (!id) return;
      patchChat(id, (chat) => ({ ...chat, running: true, startedAt: Date.now(), items: [...chat.items, { id: nextId(), kind: "user", text }] }));
      try {
        await bridge.call(Methods.SessionSend, { sessionId: id, text });
      } catch (e) {
        patchChat(id, (chat) => ({ ...chat, running: false, startedAt: undefined, items: [...chat.items, { id: nextId(), kind: "error", text: errorMessage(e) }] }));
      }
    },

    async cancel() {
      const id = get().currentId;
      if (id) await bridge.call(Methods.SessionCancel, { sessionId: id }).catch(fail);
    },

    async setMode(mode) {
      const id = get().currentId;
      if (!id) return;
      try {
        await bridge.call(Methods.SessionSetMode, { sessionId: id, mode });
        patchChat(id, (chat) => ({ ...chat, mode }));
      } catch (e) {
        fail(e);
      }
    },

    async cycleMode() {
      const id = get().currentId;
      const chat = id ? get().chats[id] : undefined;
      if (chat) await get().setMode(MODES[(MODES.indexOf(chat.mode) + 1) % MODES.length]);
    },

    async respond(requestId, decision) {
      await bridge.call(Methods.PermissionRespond, { requestId, decision }).catch(fail);
    },

    async saveSettings(update, key) {
      try {
        let settings = await bridge.call(Methods.SettingsUpdate, update);
        if (key) settings = await bridge.call(Methods.SecretSet, key);
        set({ settings });
        return true;
      } catch (e) {
        fail(e);
        return false;
      }
    },

    setSettingsOpen: (open) => set({ settingsOpen: open }),
    showToast: (text) => set({ toast: text }),
  };
});
