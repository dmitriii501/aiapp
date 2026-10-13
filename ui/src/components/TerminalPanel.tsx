import { useEffect, useRef, type PointerEvent as ReactPointerEvent } from "react";
import { Plus, X, ChevronDown } from "lucide-react";
import { useStore } from "../store";
import * as terminal from "../terminal/registry";

export function TerminalPanel() {
  const terminals = useStore((s) => s.terminals);
  const activeId = useStore((s) => s.activeTerminalId);
  const height = useStore((s) => s.terminalHeight);
  const { toggleTerminal, newTerminal, selectTerminal, closeTerminal, setTerminalHeight } = useStore.getState();
  const hostRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    const el = hostRef.current!;
    terminal.setHost(el, toggleTerminal);
    if (useStore.getState().terminals.length === 0) void newTerminal();
    const observer = new ResizeObserver(() => terminal.fitActive());
    observer.observe(el);
    return () => {
      observer.disconnect();
      terminal.setHost(null, toggleTerminal);
    };
  }, [toggleTerminal, newTerminal]);

  // Drag the top edge to resize.
  const startResize = (e: ReactPointerEvent<HTMLDivElement>) => {
    e.preventDefault();
    const startY = e.clientY;
    const startHeight = height;
    const move = (ev: PointerEvent) => setTerminalHeight(startHeight + (startY - ev.clientY));
    const up = () => {
      window.removeEventListener("pointermove", move);
      window.removeEventListener("pointerup", up);
    };
    window.addEventListener("pointermove", move);
    window.addEventListener("pointerup", up);
  };

  return (
    <section className="terminal" style={{ height }} aria-label="Терминал">
      <div className="terminal__resize" onPointerDown={startResize} role="separator" aria-orientation="horizontal" />
      <header className="terminal__tabs">
        <div className="terminal__tablist" role="tablist">
          {terminals.map((t) => (
            <div key={t.id} role="tab" aria-selected={t.id === activeId} className={`terminal__tab${t.id === activeId ? " terminal__tab--active" : ""}`}>
              <button className="terminal__tabbutton" onClick={() => selectTerminal(t.id)} title={t.title}>
                <span className="terminal__tabtitle">{t.title}</span>
                {t.exited && <span className="terminal__exited">завершён</span>}
              </button>
              <button className="icon-btn terminal__close" onClick={() => void closeTerminal(t.id)} aria-label="Закрыть терминал">
                <X size={14} />
              </button>
            </div>
          ))}
          <button className="icon-btn" onClick={() => void newTerminal()} aria-label="Новый терминал" title="Новый терминал">
            <Plus size={16} />
          </button>
        </div>
        <button className="icon-btn" onClick={toggleTerminal} aria-label="Скрыть панель" title="Скрыть панель (Ctrl+`)">
          <ChevronDown size={16} />
        </button>
      </header>
      <div className="terminal__host" ref={hostRef} />
    </section>
  );
}
