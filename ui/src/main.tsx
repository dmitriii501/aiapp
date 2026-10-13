import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { App } from "./App";
import { bridge } from "./bridge/bridge";
import { useStore } from "./store";
import "./styles.css";

// Mock-only demos for screenshots and UI work in a browser:
// index.html#demo (waiting for approval), #demo-done (finished turn), #settings, #mcp.
if (bridge.kind === "mock" && location.hash === "#mcp") {
  setTimeout(() => useStore.getState().setMcpOpen(true), 300);
}
if (bridge.kind === "mock" && location.hash === "#settings") {
  setTimeout(() => useStore.getState().setSettingsOpen(true), 300);
}
if (bridge.kind === "mock" && location.hash.startsWith("#demo")) {
  setTimeout(async () => {
    const store = useStore.getState();
    if (location.hash === "#demo-done") {
      useStore.subscribe((state) => {
        const chat = state.currentId ? state.chats[state.currentId] : undefined;
        const ask = chat?.items.find((i) => i.kind === "permission");
        if (ask?.kind === "permission") void state.respond(ask.prompt.requestId, "allow");
      });
    }
    await store.startSession(store.workspaces[0].path);
    await useStore.getState().send("Поменяй приветствие в Program.cs на Hello, Kairo");
  }, 300);
}

createRoot(document.getElementById("root")!).render(
  <StrictMode>
    <App />
  </StrictMode>,
);
