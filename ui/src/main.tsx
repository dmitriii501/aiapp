import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { App } from "./App";
import { bridge } from "./bridge/bridge";
import { useStore } from "./store";
import "./styles.css";

// Mock-only demos for screenshots and UI work in a browser:
// index.html#demo (waiting for approval), #demo-done (finished turn), #demo-compact, #demo-pc (a PC tool is running), #settings, #mcp.
if (bridge.kind === "mock" && location.hash.startsWith("#swarm")) {
  // #swarm: a swarm running (an approval waits); #swarm-done: the finished swarm; #swarm-new: the start form.
  setTimeout(async () => {
    const store = useStore.getState();
    store.setView("swarms");
    if (location.hash === "#swarm-new") return;
    await store.startSwarm({ workingDirectory: store.workspaces[0].path, goal: "Добавить тесты на парсер конфигурации и описать формат в README", workers: 2, maxTokens: 2_000_000 });
    if (location.hash === "#swarm-done") {
      useStore.subscribe((state) => {
        for (const prompt of state.swarm?.pendingPermissions ?? []) void state.respond(prompt.requestId, "allow");
      });
    }
  }, 300);
}
if (bridge.kind === "mock" && location.hash === "#mcp") {
  setTimeout(() => useStore.getState().setMcpOpen(true), 300);
}
if (bridge.kind === "mock" && location.hash === "#update") {
  setTimeout(() => useStore.getState().setUpdateOpen(true), 300);
}
if (bridge.kind === "mock" && location.hash === "#terminal") {
  setTimeout(async () => {
    const store = useStore.getState();
    await store.startSession(store.workspaces[0].path);
    useStore.getState().toggleTerminal();
  }, 300);
}
if (bridge.kind === "mock" && location.hash === "#settings") {
  setTimeout(() => useStore.getState().setSettingsOpen(true), 300);
}
if (bridge.kind === "mock" && location.hash.startsWith("#demo")) {
  setTimeout(async () => {
    const store = useStore.getState();
    if (location.hash === "#demo-rollback") {
      // Finish the turn, then open the rollback dialog for the prompt that changed files.
      useStore.subscribe((state) => {
        const chat = state.currentId ? state.chats[state.currentId] : undefined;
        const ask = chat?.items.find((i) => i.kind === "permission");
        if (ask?.kind === "permission") void state.respond(ask.prompt.requestId, "allow");
        const prompt = chat?.items.find((i) => i.kind === "user");
        if (chat && !chat.running && prompt?.kind === "user" && prompt.checkpointId && !state.rollback) void state.openRollback(prompt.checkpointId);
      });
    }
    if (location.hash === "#demo-compact") {
      // Finish the turn, then compact: the divider with the token counts and a way to see the summary.
      let compacted = false;
      useStore.subscribe((state) => {
        const chat = state.currentId ? state.chats[state.currentId] : undefined;
        const ask = chat?.items.find((i) => i.kind === "permission");
        if (ask?.kind === "permission") void state.respond(ask.prompt.requestId, "allow");
        if (chat && !chat.running && !compacted && chat.items.some((i) => i.kind === "assistant")) {
          compacted = true;
          void state.compact();
        }
      });
    }
    if (location.hash === "#demo-done") {
      useStore.subscribe((state) => {
        const chat = state.currentId ? state.chats[state.currentId] : undefined;
        const ask = chat?.items.find((i) => i.kind === "permission");
        if (ask?.kind === "permission") void state.respond(ask.prompt.requestId, "allow");
      });
    }
    if (location.hash === "#demo-pc") {
      // A PC tool is running: the status bar says so (the real thing comes from the agent events).
      await store.startSession(store.workspaces[0].path);
      const id = useStore.getState().currentId!;
      useStore.setState((st) => ({
        chats: {
          ...st.chats,
          [id]: {
            ...st.chats[id],
            running: true,
            items: [
              { id: "d1", kind: "user", text: "Открой блокнот и напиши приветствие" },
              { id: "d2", kind: "tool", toolCallId: "t1", tool: "PcPursue", summary: "PcPursue(notepad.exe: Write a greeting)", status: "running" },
            ],
          },
        },
      }));
      return;
    }
    await store.startSession(store.workspaces[0].path);
    await useStore.getState().send(location.hash === "#demo-plan" ? "План: переименовать функцию Calc" : "Поменяй приветствие в Program.cs на Hello, Kairo");
  }, 300);
}

createRoot(document.getElementById("root")!).render(
  <StrictMode>
    <App />
  </StrictMode>,
);
