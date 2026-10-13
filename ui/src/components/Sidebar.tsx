import { useEffect, useMemo, useRef, useState } from "react";
import { ChevronRight, FolderOpen, Plus } from "lucide-react";
import { useStore } from "../store";
import type { SessionSummary } from "../bridge/protocol";
import { folderName, relativeTime } from "../format";

interface Group {
  path: string;
  sessions: SessionSummary[];
}

/** Sessions grouped by project folder, most recently used folder first — like an explorer tree. */
function groupByFolder(sessions: SessionSummary[]): Group[] {
  const groups = new Map<string, Group>();
  for (const s of sessions) {
    const key = s.workingDirectory.toLowerCase();
    if (!groups.has(key)) groups.set(key, { path: s.workingDirectory, sessions: [] });
    groups.get(key)!.sessions.push(s);
  }
  return [...groups.values()];
}

export function Sidebar() {
  const sessions = useStore((s) => s.sessions);
  const currentId = useStore((s) => s.currentId);
  const openSession = useStore((s) => s.openSession);
  const startSession = useStore((s) => s.startSession);
  const groups = useMemo(() => groupByFolder(sessions), [sessions]);
  const [collapsed, setCollapsed] = useState<Set<string>>(new Set());
  const [, tick] = useState(0);

  // Keep relative times fresh.
  useEffect(() => {
    const t = setInterval(() => tick((n) => n + 1), 60_000);
    return () => clearInterval(t);
  }, []);

  const toggle = (path: string) =>
    setCollapsed((c) => {
      const next = new Set(c);
      if (next.has(path)) next.delete(path);
      else next.add(path);
      return next;
    });

  return (
    <aside className="sidebar">
      <header className="sidebar__header">
        <span>Сессии</span>
        <NewSessionMenu />
      </header>

      <div className="tree" role="tree">
        {groups.length === 0 && <p className="tree__empty">Сессий пока нет. Нажмите «+», чтобы начать.</p>}
        {groups.map((g) => {
          const open = !collapsed.has(g.path);
          return (
            <div key={g.path} role="treeitem" aria-expanded={open}>
              <div className="tree__row tree__row--folder" title={g.path} onClick={() => toggle(g.path)}>
                <ChevronRight size={14} className={`tree__chevron${open ? " tree__chevron--open" : ""}`} />
                <span className="tree__label">{folderName(g.path)}</span>
                <button
                  className="tree__action"
                  title="Новая сессия в этой папке"
                  onClick={(e) => {
                    e.stopPropagation();
                    void startSession(g.path);
                  }}
                >
                  <Plus size={14} />
                </button>
              </div>
              {open &&
                g.sessions.map((s) => (
                  <div
                    key={s.id}
                    role="treeitem"
                    className={`tree__row tree__row--session${s.id === currentId ? " tree__row--selected" : ""}`}
                    onClick={() => void openSession(s.id)}
                    title={s.title}
                  >
                    <span className={`tree__dot${s.running ? " tree__dot--running" : ""}`} />
                    <span className="tree__label">{s.title}</span>
                    <span className="tree__meta">{relativeTime(s.updatedAt)}</span>
                  </div>
                ))}
            </div>
          );
        })}
      </div>
    </aside>
  );
}

function NewSessionMenu() {
  const workspaces = useStore((s) => s.workspaces);
  const startSession = useStore((s) => s.startSession);
  const pickFolder = useStore((s) => s.pickFolder);
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (!open) return;
    const close = (e: MouseEvent | KeyboardEvent) => {
      if (e instanceof KeyboardEvent ? e.key === "Escape" : !ref.current?.contains(e.target as Node)) setOpen(false);
    };
    window.addEventListener("mousedown", close);
    window.addEventListener("keydown", close);
    return () => {
      window.removeEventListener("mousedown", close);
      window.removeEventListener("keydown", close);
    };
  }, [open]);

  const choose = (action: () => Promise<void>) => {
    setOpen(false);
    void action();
  };

  return (
    <div className="dropdown" ref={ref}>
      <button className="icon-btn" title="Новая сессия" onClick={() => (workspaces.length ? setOpen((o) => !o) : void pickFolder())}>
        <Plus size={16} />
      </button>
      {open && (
        <div className="menu" role="menu">
          <div className="menu__label">Новая сессия в папке</div>
          {workspaces.map((w) => (
            <button key={w.path} role="menuitem" className="menu__item" title={w.path} onClick={() => choose(() => startSession(w.path))}>
              <span className="menu__text">{w.name}</span>
              <span className="menu__hint">{w.path}</span>
            </button>
          ))}
          <div className="menu__sep" />
          <button role="menuitem" className="menu__item" onClick={() => choose(pickFolder)}>
            <FolderOpen size={14} />
            <span className="menu__text">Открыть папку…</span>
          </button>
        </div>
      )}
    </div>
  );
}
