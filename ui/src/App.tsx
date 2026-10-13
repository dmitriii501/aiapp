import { useEffect } from "react";
import { MessagesSquare, Network, Settings } from "lucide-react";
import { useStore } from "./store";
import { Sidebar } from "./components/Sidebar";
import { SwarmSidebar } from "./components/SwarmSidebar";
import { SwarmView, SwarmWelcome } from "./components/SwarmView";
import { ChatView } from "./components/ChatView";
import { Welcome } from "./components/Welcome";
import { SettingsDialog } from "./components/SettingsDialog";
import { McpDialog } from "./components/McpDialog";
import { TerminalPanel } from "./components/TerminalPanel";
import { UpdateDialog } from "./components/UpdateDialog";
import { RollbackDialog } from "./components/RollbackDialog";
import { StatusBar } from "./components/StatusBar";
import { Toast } from "./components/Toast";

export function App() {
  const booted = useStore((s) => s.booted);
  const currentId = useStore((s) => s.currentId);
  const view = useStore((s) => s.view);
  const setView = useStore((s) => s.setView);
  const currentSwarmId = useStore((s) => s.currentSwarmId);
  const settingsOpen = useStore((s) => s.settingsOpen);
  const mcpOpen = useStore((s) => s.mcpOpen);
  const updateOpen = useStore((s) => s.updateOpen);
  const rollbackOpen = useStore((s) => s.rollback !== undefined);
  const terminalOpen = useStore((s) => s.terminalOpen);
  const toggleTerminal = useStore((s) => s.toggleTerminal);
  const boot = useStore((s) => s.boot);
  const closeSession = useStore((s) => s.closeSession);
  const setSettingsOpen = useStore((s) => s.setSettingsOpen);

  useEffect(() => {
    void boot();
  }, [boot]);

  // Ctrl+` shows and hides the terminal panel (inside the terminal, xterm hands the key to us).
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.ctrlKey && e.key === "`") {
        e.preventDefault();
        toggleTerminal();
      }
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [toggleTerminal]);

  return (
    <div className="workbench">
      <nav className="activitybar" aria-label="Разделы">
        <button
          className={`activitybar__item${view === "sessions" ? " activitybar__item--active" : ""}`}
          title="Сессии"
          onClick={() => {
            if (view === "sessions") closeSession();
            setView("sessions");
          }}
        >
          <MessagesSquare size={22} strokeWidth={1.5} />
        </button>
        <button className={`activitybar__item${view === "swarms" ? " activitybar__item--active" : ""}`} title="Рой агентов" onClick={() => setView("swarms")}>
          <Network size={22} strokeWidth={1.5} />
        </button>
        <div className="activitybar__spacer" />
        <button className="activitybar__item" title="Настройки" onClick={() => setSettingsOpen(true)}>
          <Settings size={22} strokeWidth={1.5} />
        </button>
      </nav>
      {view === "swarms" ? <SwarmSidebar /> : <Sidebar />}
      <main className="editor">
        <div className="editor__main">
          {booted &&
            (view === "swarms" ? (
              currentSwarmId ? (
                <SwarmView key={currentSwarmId} />
              ) : (
                <SwarmWelcome />
              )
            ) : currentId ? (
              <ChatView key={currentId} sessionId={currentId} />
            ) : (
              <Welcome />
            ))}
        </div>
        {terminalOpen && <TerminalPanel />}
      </main>
      <StatusBar />
      {settingsOpen && <SettingsDialog />}
      {mcpOpen && <McpDialog />}
      {updateOpen && <UpdateDialog />}
      {rollbackOpen && <RollbackDialog />}
      <Toast />
    </div>
  );
}
