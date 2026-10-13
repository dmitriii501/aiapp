import { useEffect } from "react";
import { MessagesSquare, Settings } from "lucide-react";
import { useStore } from "./store";
import { Sidebar } from "./components/Sidebar";
import { ChatView } from "./components/ChatView";
import { Welcome } from "./components/Welcome";
import { SettingsDialog } from "./components/SettingsDialog";
import { McpDialog } from "./components/McpDialog";
import { TerminalPanel } from "./components/TerminalPanel";
import { StatusBar } from "./components/StatusBar";
import { Toast } from "./components/Toast";

export function App() {
  const booted = useStore((s) => s.booted);
  const currentId = useStore((s) => s.currentId);
  const settingsOpen = useStore((s) => s.settingsOpen);
  const mcpOpen = useStore((s) => s.mcpOpen);
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
        <button className="activitybar__item activitybar__item--active" title="Сессии" onClick={closeSession}>
          <MessagesSquare size={22} strokeWidth={1.5} />
        </button>
        <div className="activitybar__spacer" />
        <button className="activitybar__item" title="Настройки" onClick={() => setSettingsOpen(true)}>
          <Settings size={22} strokeWidth={1.5} />
        </button>
      </nav>
      <Sidebar />
      <main className="editor">
        <div className="editor__main">{booted && (currentId ? <ChatView key={currentId} sessionId={currentId} /> : <Welcome />)}</div>
        {terminalOpen && <TerminalPanel />}
      </main>
      <StatusBar />
      {settingsOpen && <SettingsDialog />}
      {mcpOpen && <McpDialog />}
      <Toast />
    </div>
  );
}
