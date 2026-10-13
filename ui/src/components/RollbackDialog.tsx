import { useEffect, useMemo, useState } from "react";
import { X } from "lucide-react";
import { useStore } from "../store";
import type { CheckpointFile, RollbackAction } from "../bridge/protocol";
import { DiffView, diffStats } from "./DiffView";

const LABEL: Record<RollbackAction, string> = {
  restore: "Вернуть прежнее содержимое",
  delete: "Удалить: файл создал агент",
  conflict: "Файл изменили после агента",
  unchanged: "Уже в прежнем состоянии",
  unavailable: "Нельзя откатить: файл слишком большой",
};

const selectable = (f: CheckpointFile) => f.action === "restore" || f.action === "delete" || f.action === "conflict";

export function RollbackDialog() {
  const preview = useStore((s) => s.rollback);
  const close = useStore((s) => s.closeRollback);
  const apply = useStore((s) => s.applyRollback);

  // Plain changes are selected by default; files someone else changed are not.
  const [selected, setSelected] = useState<Set<string>>(
    () => new Set((preview?.files ?? []).filter((f) => f.action === "restore" || f.action === "delete").map((f) => f.path)),
  );
  const [open, setOpen] = useState<string | undefined>();
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => e.key === "Escape" && close();
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [close]);

  const files = preview?.files ?? [];
  const conflictChosen = useMemo(() => files.some((f) => f.action === "conflict" && selected.has(f.path)), [files, selected]);
  if (!preview) return null;

  const toggle = (path: string) =>
    setSelected((s) => {
      const next = new Set(s);
      if (!next.delete(path)) next.add(path);
      return next;
    });

  const submit = async () => {
    setBusy(true);
    await apply([...selected], conflictChosen);
    setBusy(false);
  };

  return (
    <div className="overlay" onMouseDown={close}>
      <div className="dialog dialog--wide" role="dialog" aria-label="Откат изменений" onMouseDown={(e) => e.stopPropagation()}>
        <header className="dialog__header">
          <span>Откатить изменения</span>
          <button type="button" className="icon-btn" onClick={close} aria-label="Закрыть">
            <X size={16} />
          </button>
        </header>

        <div className="dialog__body">
          <p className="field__hint rollback__lead">
            Выбранные файлы вернутся к состоянию до этого запроса. Правки тех же файлов из следующих запросов тоже отменятся. Действия команд (Shell) не откатываются.
          </p>

          <ul className="rollback__list">
            {files.map((f) => {
              const { added, removed } = f.diff ? diffStats(f.diff) : { added: 0, removed: 0 };
              return (
                <li key={f.path} className="rollback__row">
                  <div className="rollback__main">
                    <input
                      type="checkbox"
                      checked={selected.has(f.path)}
                      disabled={!selectable(f)}
                      onChange={() => toggle(f.path)}
                      aria-label={f.displayPath}
                    />
                    <span className="rollback__path" title={f.path}>
                      {f.displayPath}
                    </span>
                    <span className={`rollback__action rollback__action--${f.action}`}>{LABEL[f.action]}</span>
                    {f.diff && (
                      <button className="mcp__tools" onClick={() => setOpen(open === f.path ? undefined : f.path)} aria-expanded={open === f.path}>
                        <span className="diff-stats__add">+{added}</span> <span className="diff-stats__del">−{removed}</span>
                      </button>
                    )}
                  </div>
                  {f.action === "conflict" && selected.has(f.path) && (
                    <div className="rollback__warning">Откат перезапишет эти изменения: их нельзя будет вернуть.</div>
                  )}
                  {open === f.path && f.diff && <DiffView diff={f.diff} maxHeight={240} />}
                </li>
              );
            })}
          </ul>
        </div>

        <footer className="dialog__footer">
          <button type="button" className="btn btn--secondary" onClick={close}>
            Отмена
          </button>
          <button type="button" className="btn" disabled={busy || selected.size === 0} onClick={() => void submit()}>
            Откатить выбранные ({selected.size})
          </button>
        </footer>
      </div>
    </div>
  );
}
