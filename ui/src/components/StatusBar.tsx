import { ArrowUpCircle, Cpu, Folder, Plug, SquareTerminal } from "lucide-react";
import { useStore } from "../store";
import { folderName } from "../format";

const k = (n: number) => (n >= 1000 ? `${(n / 1000).toFixed(1)}k` : String(n));

export function StatusBar() {
  const settings = useStore((s) => s.settings);
  const session = useStore((s) => s.sessions.find((x) => x.id === s.currentId));
  const chat = useStore((s) => (s.currentId ? s.chats[s.currentId] : undefined));
  const mcp = useStore((s) => s.mcp);
  const setMcpOpen = useStore((s) => s.setMcpOpen);
  const update = useStore((s) => s.update);
  const setUpdateOpen = useStore((s) => s.setUpdateOpen);
  const terminalOpen = useStore((s) => s.terminalOpen);
  const toggleTerminal = useStore((s) => s.toggleTerminal);

  const enabled = mcp?.servers.filter((s) => s.status !== "disabled") ?? [];
  const ready = enabled.filter((s) => s.status === "ready").length;
  const failed = enabled.some((s) => s.status === "failed");

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
        <button className="statusbar__item statusbar__button" title="Терминал (Ctrl+`)" aria-pressed={terminalOpen} onClick={toggleTerminal}>
          <SquareTerminal size={13} />
          Терминал
        </button>
        <button
          className={`statusbar__item statusbar__button${failed ? " statusbar__button--warn" : ""}`}
          title={failed ? "Часть серверов MCP не подключилась" : "Серверы MCP"}
          onClick={() => setMcpOpen(true)}
        >
          <Plug size={13} />
          {enabled.length === 0 ? "MCP" : `MCP: ${ready}/${enabled.length}`}
        </button>
        {update && (
          <button
            className={`statusbar__item statusbar__button${update.state === "available" || update.state === "downloading" ? " statusbar__button--accent" : ""}`}
            title="Обновления"
            onClick={() => setUpdateOpen(true)}
          >
            {update.state === "available" ? (
              <>
                <ArrowUpCircle size={13} />
                Обновление {update.version}
              </>
            ) : update.state === "downloading" ? (
              <>Загрузка {update.progress ?? 0}%</>
            ) : (
              <>v{update.currentVersion}</>
            )}
          </button>
        )}
        {chat?.usage && (
          <span className="statusbar__item" title="Токены последнего ответа: вход / выход">
            ↑{k(chat.usage.input)} ↓{k(chat.usage.output)}
          </span>
        )}
      </div>
    </footer>
  );
}
