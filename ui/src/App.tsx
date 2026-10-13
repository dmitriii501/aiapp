import { useEffect } from "react";
import { MessagesSquare, Settings } from "lucide-react";
import { useStore } from "./store";
import { Sidebar } from "./components/Sidebar";
import { ChatView } from "./components/ChatView";
import { Welcome } from "./components/Welcome";
import { SettingsDialog } from "./components/SettingsDialog";
import { StatusBar } from "./components/StatusBar";
import { Toast } from "./components/Toast";

export function App() {
  const booted = useStore((s) => s.booted);
  const currentId = useStore((s) => s.currentId);
  const settingsOpen = useStore((s) => s.settingsOpen);
  const boot = useStore((s) => s.boot);
  const closeSession = useStore((s) => s.closeSession);
  const setSettingsOpen = useStore((s) => s.setSettingsOpen);

  useEffect(() => {
    void boot();
  }, [boot]);

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
      <main className="editor">{booted && (currentId ? <ChatView key={currentId} sessionId={currentId} /> : <Welcome />)}</main>
      <StatusBar />
      {settingsOpen && <SettingsDialog />}
      <Toast />
    </div>
  );
}
