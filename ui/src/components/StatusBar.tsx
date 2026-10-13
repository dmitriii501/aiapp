import { Cpu, Folder } from "lucide-react";
import { useStore } from "../store";
import { folderName } from "../format";

const k = (n: number) => (n >= 1000 ? `${(n / 1000).toFixed(1)}k` : String(n));

export function StatusBar() {
  const settings = useStore((s) => s.settings);
  const session = useStore((s) => s.sessions.find((x) => x.id === s.currentId));
  const chat = useStore((s) => (s.currentId ? s.chats[s.currentId] : undefined));

  return (
    <footer className="statusbar">
      <div className="statusbar__group">
        {session && (
          <span className="statusbar__item" title={session.workingDirectory}>
            <Folder size={13} />
            {folderName(session.workingDirectory)}
          </span>
        )}
        <span className="statusbar__item">
          <Cpu size={13} />
          {chat?.model ?? settings?.model ?? "—"}
        </span>
      </div>
      <div className="statusbar__group">
        {chat?.usage && (
          <span className="statusbar__item" title="Токены последнего ответа: вход / выход">
            ↑{k(chat.usage.input)} ↓{k(chat.usage.output)}
          </span>
        )}
      </div>
    </footer>
  );
}
