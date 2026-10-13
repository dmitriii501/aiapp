import { create } from "zustand";
import { bridge, errorMessage } from "./bridge/bridge";
import type {
  AgentEvent,
  Decision,
  Mode,
  PermissionPrompt,
  ProviderId,
  SessionSummary,
  CheckpointPreview,
  McpListResult,
  UpdateStatus,
  Settings,
  SettingsUpdate,
  ToolStatus,
  Workspace,
} from "./bridge/protocol";
import { Methods, Notifications } from "./bridge/protocol";
import * as terminal from "./terminal/registry";

export type Item =
  | { id: string; kind: "user"; text: string; checkpointId?: string; files?: number }
  | { id: string; kind: "assistant"; text: string; streaming: boolean }
  | { id: string; kind: "tool"; toolCallId: string; tool: string; summary: string; output?: string; diff?: string; detail?: string; status: ToolStatus | "waiting" }
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

let spawning = false;

function loadTerminalHeight(): number {
  try {
    const saved = Number(localStorage.getItem("kairo.terminalHeight"));
    return saved >= 120 ? saved : 260;
  } catch {
    return 260;
  }
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
  mcp?: McpListResult;
  mcpOpen: boolean;
  update?: UpdateStatus;
  updateOpen: boolean;
  /** The rollback dialog: what rolling back one prompt's file changes would do. */
  rollback?: CheckpointPreview;
  terminalOpen: boolean;
  terminals: { id: string; title: string; exited: boolean }[];
  activeTerminalId?: string;
  terminalHeight: number;
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
  setMcpOpen(open: boolean): void;
  openRollback(checkpointId: string): Promise<void>;
  closeRollback(): void;
  applyRollback(paths: string[], overwriteConflicts: boolean): Promise<void>;
  setUpdateOpen(open: boolean): void;
  checkUpdate(): Promise<void>;
  installUpdate(): Promise<void>;
  toggleTerminal(): void;
  newTerminal(): Promise<void>;
  selectTerminal(id: string): void;
  closeTerminal(id: string): Promise<void>;
  setTerminalHeight(height: number): void;
  mcpAdd(name: string, target: { command?: string; url?: string }): Promise<boolean>;
  mcpRemove(name: string): Promise<void>;
  mcpSetEnabled(name: string, enabled: boolean): Promise<void>;
  mcpReload(): Promise<void>;
  mcpImportDesktop(): Promise<void>;
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
          items.push({ id: nextId(), kind: "tool", toolCallId: e.toolCallId!, tool: e.tool!, summary: e.summary ?? e.tool!, detail: e.detail, status: "running" });
          return { ...chat, items };
        case "tool_finished":
          return {
            ...chat,
            items: items.map((i) => (i.kind === "tool" && i.toolCallId === e.toolCallId ? { ...i, status: e.status ?? "done", output: e.output, diff: e.diff } : i)),
          };
        case "mode":
          // The agent left plan mode after the user approved its plan.
          return { ...chat, mode: (e.text as Mode) ?? chat.mode };
        case "checkpoint": {
          // The files this prompt changed: shown under the prompt, updated as more files are changed.
          const at = items.findLastIndex((i) => i.kind === "user");
          if (at >= 0) items[at] = { ...(items[at] as Extract<Item, { kind: "user" }>), checkpointId: e.checkpointId, files: e.files };
          return { ...chat, items };
        }
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
    mcpOpen: false,
    updateOpen: false,
    terminalOpen: false,
    terminals: [],
    terminalHeight: loadTerminalHeight(),

    async boot() {
      bridge.on(Notifications.AgentEvent, onEvent);
      bridge.on(Notifications.SessionsChanged, () => void refreshSessions().catch(fail));
      bridge.on(Notifications.McpChanged, (mcp) => set({ mcp }));
      bridge.on(Notifications.UpdateChanged, (update) => set({ update }));
      bridge.on(Notifications.TerminalOutput, (o) => terminal.write(o.terminalId, o.data));
      bridge.on(Notifications.TerminalExited, (e) => {
        terminal.exited(e.terminalId, e.exitCode);
        set((s) => ({ terminals: s.terminals.map((t) => (t.id === e.terminalId ? { ...t, exited: true } : t)) }));
      });
      try {
        const settings = await bridge.call(Methods.SettingsGet, {});
        set({ settings });
        void bridge.call(Methods.McpList, {}).then((mcp) => set({ mcp })).catch(fail);
        void bridge.call(Methods.UpdateGet, {}).then((update) => set({ update })).catch(fail);
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
              return { id: nextId(), kind: "user", text: t.text ?? "", checkpointId: t.checkpointId, files: t.files };
            case "assistant":
              return { id: nextId(), kind: "assistant", text: t.text ?? "", streaming: false };
            case "tool":
              return { id: nextId(), kind: "tool", toolCallId: t.toolCallId!, tool: t.tool!, summary: t.summary ?? t.tool!, output: t.output, diff: t.diff, detail: t.detail, status: t.status ?? "done" };
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
    setMcpOpen: (open) => set({ mcpOpen: open }),

    async openRollback(checkpointId) {
      try {
        set({ rollback: await bridge.call(Methods.CheckpointPreview, { checkpointId }) });
      } catch (e) {
        fail(e);
      }
    },

    closeRollback: () => set({ rollback: undefined }),

    async applyRollback(paths, overwriteConflicts) {
      const preview = get().rollback;
      const id = get().currentId;
      if (!preview) return;
      try {
        const result = await bridge.call(Methods.CheckpointRollback, { checkpointId: preview.checkpointId, paths, overwriteConflicts });
        set({ rollback: undefined });
        const parts = [`восстановлено файлов: ${result.restored}`, `удалено: ${result.deleted}`];
        if (result.skipped.length > 0) parts.push(`пропущено: ${result.skipped.length}`);
        if (id) patchChat(id, (chat) => ({ ...chat, items: [...chat.items, { id: nextId(), kind: "notice", text: `Откат: ${parts.join(", ")}` }] }));
      } catch (e) {
        fail(e);
      }
    },

    setUpdateOpen: (open) => set({ updateOpen: open }),

    async checkUpdate() {
      await bridge.call(Methods.UpdateCheck, {}).then((update) => set({ update })).catch(fail);
    },

    async installUpdate() {
      await bridge.call(Methods.UpdateInstall, {}).catch(fail);
    },

    toggleTerminal() {
      const { terminalOpen, currentId, sessions } = get();
      if (!terminalOpen && !sessions.some((x) => x.id === currentId) && get().terminals.length === 0) {
        set({ toast: "Откройте сессию: терминал запускается в её папке." });
        return;
      }
      set({ terminalOpen: !terminalOpen });
    },

    async newTerminal() {
      if (spawning) return;
      const session = get().sessions.find((x) => x.id === get().currentId);
      const cwd = session?.workingDirectory ?? get().workspaces[0]?.path;
      if (!cwd) {
        set({ toast: "Откройте сессию: терминал запускается в её папке." });
        return;
      }
      spawning = true;
      try {
        const { id, title } = await terminal.spawn(cwd);
        set((s) => ({ terminals: [...s.terminals, { id, title, exited: false }], activeTerminalId: id }));
      } catch (e) {
        fail(e);
        if (get().terminals.length === 0) set({ terminalOpen: false });
      } finally {
        spawning = false;
      }
    },

    selectTerminal(id) {
      terminal.show(id);
      set({ activeTerminalId: id });
    },

    async closeTerminal(id) {
      await bridge.call(Methods.TerminalClose, { terminalId: id }).catch(() => {});
      terminal.dispose(id);
      const rest = get().terminals.filter((t) => t.id !== id);
      const next = get().activeTerminalId === id ? rest[rest.length - 1]?.id : get().activeTerminalId;
      set({ terminals: rest, activeTerminalId: next, terminalOpen: rest.length > 0 && get().terminalOpen });
      terminal.show(next);
    },

    setTerminalHeight(height) {
      const clamped = Math.max(120, Math.min(height, Math.round(window.innerHeight * 0.7)));
      set({ terminalHeight: clamped });
      try {
        localStorage.setItem("kairo.terminalHeight", String(clamped));
      } catch {
        // Storage can be unavailable; the height just is not remembered.
      }
    },

    async mcpAdd(name, target) {
      try {
        set({ mcp: await bridge.call(Methods.McpAdd, { name, ...target }) });
        return true;
      } catch (e) {
        fail(e);
        return false;
      }
    },

    async mcpRemove(name) {
      await bridge.call(Methods.McpRemove, { name }).then((mcp) => set({ mcp })).catch(fail);
    },

    async mcpSetEnabled(name, enabled) {
      await bridge.call(Methods.McpSetEnabled, { name, enabled }).then((mcp) => set({ mcp })).catch(fail);
    },

    async mcpReload() {
      await bridge.call(Methods.McpReload, {}).then((mcp) => set({ mcp })).catch(fail);
    },

    async mcpImportDesktop() {
      try {
        const mcp = await bridge.call(Methods.McpImportDesktop, {});
        set({ mcp, toast: mcp.imported?.length ? `Добавлено серверов: ${mcp.imported.length}` : "Новых серверов в Claude Desktop нет" });
      } catch (e) {
        fail(e);
      }
    },
    showToast: (text) => set({ toast: text }),
  };
});
