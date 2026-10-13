import { useEffect, useState } from "react";
import { Plus } from "lucide-react";
import { useStore } from "../store";
import { relativeTime } from "../format";
import { STATUS_LABELS } from "./SwarmView";

export function SwarmSidebar() {
  const swarms = useStore((s) => s.swarms);
  const currentSwarmId = useStore((s) => s.currentSwarmId);
  const openSwarm = useStore((s) => s.openSwarm);
  const closeSwarm = useStore((s) => s.closeSwarm);
  const [, tick] = useState(0);

  useEffect(() => {
    const t = setInterval(() => tick((n) => n + 1), 60_000);
    return () => clearInterval(t);
  }, []);

  return (
    <aside className="sidebar">
      <header className="sidebar__header">
        <span>Рои</span>
        <button className="icon-btn" title="Новый рой" onClick={closeSwarm}>
          <Plus size={16} />
        </button>
      </header>
      <div className="tree" role="tree">
        {swarms.length === 0 && <p className="tree__empty">Роев пока нет. Нажмите «+», чтобы запустить первый.</p>}
        {swarms.map((s) => (
          <div
            key={s.id}
            role="treeitem"
            className={`tree__row tree__row--session${s.id === currentSwarmId ? " tree__row--selected" : ""}`}
            onClick={() => void openSwarm(s.id)}
            title={s.goal}
          >
            <span className={`tree__dot${s.running ? " tree__dot--running" : ""}`} />
            <span className="tree__label">{s.goal}</span>
            <span className="tree__meta">{s.running ? "идёт" : `${STATUS_LABELS[s.status]} · ${relativeTime(s.updatedAt)}`}</span>
          </div>
        ))}
      </div>
    </aside>
  );
}
