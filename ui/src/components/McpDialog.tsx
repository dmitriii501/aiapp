import { useEffect, useState, type FormEvent } from "react";
import { X } from "lucide-react";
import { useStore } from "../store";
import type { McpServerInfo, McpStatus } from "../bridge/protocol";

const STATUS_LABEL: Record<McpStatus, string> = {
  connecting: "Подключается…",
  ready: "Подключён",
  failed: "Ошибка",
  disabled: "Отключён",
};

function plural(n: number, one: string, few: string, many: string) {
  const mod10 = n % 10;
  const mod100 = n % 100;
  if (mod10 === 1 && mod100 !== 11) return one;
  if (mod10 >= 2 && mod10 <= 4 && (mod100 < 12 || mod100 > 14)) return few;
  return many;
}

export function McpDialog() {
  const mcp = useStore((s) => s.mcp);
  const setOpen = useStore((s) => s.setMcpOpen);
  const { mcpAdd, mcpRemove, mcpSetEnabled, mcpReload, mcpImportDesktop } = useStore.getState();

  const [kind, setKind] = useState<"command" | "url">("command");
  const [name, setName] = useState("");
  const [target, setTarget] = useState("");
  const [adding, setAdding] = useState(false);

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => e.key === "Escape" && setOpen(false);
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [setOpen]);

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    setAdding(true);
    const ok = await mcpAdd(name.trim(), kind === "command" ? { command: target.trim() } : { url: target.trim() });
    setAdding(false);
    if (ok) {
      setName("");
      setTarget("");
    }
  };

  const servers = mcp?.servers ?? [];

  return (
    <div className="overlay" onMouseDown={() => setOpen(false)}>
      <div className="dialog dialog--wide" role="dialog" aria-label="Серверы MCP" onMouseDown={(e) => e.stopPropagation()}>
        <header className="dialog__header">
          <span>Серверы MCP</span>
          <button type="button" className="icon-btn" onClick={() => setOpen(false)} aria-label="Закрыть">
            <X size={16} />
          </button>
        </header>

        <div className="dialog__body">
          <p className="field__hint mcp__lead">
            Серверы MCP дают агенту дополнительные инструменты. Каждый вызов их инструментов спрашивает подтверждение.
          </p>

          {servers.length === 0 ? (
            <div className="mcp__empty">Серверов нет. Добавьте сервер ниже или импортируйте настройки из Claude Desktop.</div>
          ) : (
            <ul className="mcp__list">
              {servers.map((s) => (
                <ServerRow key={s.name} server={s} onToggle={() => void mcpSetEnabled(s.name, s.status === "disabled")} onRemove={() => void mcpRemove(s.name)} />
              ))}
            </ul>
          )}

          <h3 className="dialog__section">Добавить сервер</h3>
          <form onSubmit={(e) => void submit(e)}>
            <div className="mcp__tabs" role="tablist">
              <button type="button" role="tab" aria-selected={kind === "command"} className={`mcp__tab${kind === "command" ? " mcp__tab--active" : ""}`} onClick={() => setKind("command")}>
                Локальный (команда)
              </button>
              <button type="button" role="tab" aria-selected={kind === "url"} className={`mcp__tab${kind === "url" ? " mcp__tab--active" : ""}`} onClick={() => setKind("url")}>
                Удалённый (адрес)
              </button>
            </div>
            <div className="field">
              <span className="field__label">Название</span>
              <input className="input" value={name} onChange={(e) => setName(e.target.value)} placeholder="filesystem" spellCheck={false} />
            </div>
            <div className="field">
              <span className="field__label">{kind === "command" ? "Команда запуска" : "Адрес сервера"}</span>
              <input
                className="input"
                value={target}
                onChange={(e) => setTarget(e.target.value)}
                placeholder={kind === "command" ? "npx -y @modelcontextprotocol/server-filesystem C:\\projects" : "https://example.com/mcp"}
                spellCheck={false}
              />
              <span className="field__hint">
                {kind === "command"
                  ? "Команда запускается с вашими правами. Добавляйте только серверы, которым доверяете."
                  : "Заголовки с токеном доступа задаются в файле mcp.json."}
              </span>
            </div>
            <button type="submit" className="btn" disabled={adding || !name.trim() || !target.trim()}>
              Добавить
            </button>
          </form>

          {mcp && (
            <p className="field__hint mcp__path">
              Переменные окружения и заголовки задаются в файле <span className="mcp__file">{mcp.configPath}</span>. После правки нажмите «Переподключить».
            </p>
          )}
        </div>

        <footer className="dialog__footer">
          <button type="button" className="btn btn--secondary" onClick={() => void mcpImportDesktop()}>
            Импорт из Claude Desktop
          </button>
          <button type="button" className="btn btn--secondary" onClick={() => void mcpReload()}>
            Переподключить
          </button>
        </footer>
      </div>
    </div>
  );
}

function ServerRow({ server, onToggle, onRemove }: { server: McpServerInfo; onToggle: () => void; onRemove: () => void }) {
  const [open, setOpen] = useState(false);
  const count = server.tools.length;

  return (
    <li className="mcp__row">
      <div className="mcp__main">
        <span className={`mcp__dot mcp__dot--${server.status}`} title={STATUS_LABEL[server.status]} />
        <span className="mcp__name">{server.name}</span>
        <span className="mcp__meta">{server.transport === "http" ? "удалённый" : server.transport === "stdio" ? "локальный" : "файл"}</span>
        {server.status === "ready" && (
          <button className="mcp__tools" onClick={() => setOpen((o) => !o)} aria-expanded={open}>
            {count} {plural(count, "инструмент", "инструмента", "инструментов")}
          </button>
        )}
        {server.status !== "ready" && <span className="mcp__meta">{STATUS_LABEL[server.status]}</span>}
        <span className="mcp__spacer" />
        {server.transport !== "file" && (
          <>
            <button className="mcp__action" onClick={onToggle}>
              {server.status === "disabled" ? "Включить" : "Отключить"}
            </button>
            <button className="mcp__action" onClick={onRemove}>
              Удалить
            </button>
          </>
        )}
      </div>
      {server.error && <div className="mcp__error">{server.error}</div>}
      {open && count > 0 && <div className="mcp__toolnames">{server.tools.join(", ")}</div>}
    </li>
  );
}
