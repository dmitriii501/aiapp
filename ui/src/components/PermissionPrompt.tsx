import { useEffect, useState } from "react";
import { useStore } from "../store";
import { fromTerminal } from "../keys";
import type { Decision, PermissionPrompt as Prompt } from "../bridge/protocol";
import { DiffView, diffStats } from "./DiffView";

const TITLES: Record<string, string> = {
  write: "Изменение файла",
  exec: "Команда",
  read: "Чтение файла",
  network: "Сетевой доступ",
  system: "Действие в системе",
};

const OPTIONS: { decision: Decision; label: string }[] = [
  { decision: "allow", label: "Да" },
  { decision: "always", label: "Да, и не спрашивать до конца сессии" },
  { decision: "deny", label: "Нет" },
];

/**
 * Shown in place of the input while the agent waits, as in Claude Code.
 * Keys: 1–3 or ↑/↓ + Enter to choose, Esc to decline.
 */
export function PermissionPrompt({ prompt }: { prompt: Prompt }) {
  const respond = useStore((s) => s.respond);
  const [selected, setSelected] = useState(0);
  const [done, setDone] = useState(false);

  const answer = (decision: Decision) => {
    if (done) return;
    setDone(true);
    void respond(prompt.requestId, decision);
  };

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (useStore.getState().settingsOpen || fromTerminal(e)) return;
      if (e.key === "ArrowDown" || e.key === "ArrowUp") {
        e.preventDefault();
        setSelected((s) => (s + (e.key === "ArrowDown" ? 1 : OPTIONS.length - 1)) % OPTIONS.length);
      } else if (e.key === "Enter") {
        e.preventDefault();
        answer(OPTIONS[selected].decision);
      } else if (e.key === "Escape") {
        e.preventDefault();
        answer("deny");
      } else if (["1", "2", "3"].includes(e.key)) {
        e.preventDefault();
        answer(OPTIONS[Number(e.key) - 1].decision);
      }
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  });

  const outside = prompt.risk === "read" && prompt.reason.includes("outside");

  return (
    <div className="permission" role="dialog" aria-label="Подтверждение действия">
      <div className="permission__title">{TITLES[prompt.risk] ?? "Действие"}</div>
      {prompt.diff ? (
        <>
          <div className="permission__file">
            <span className="permission__path">{prompt.subject}</span>
            <DiffStats diff={prompt.diff} />
          </div>
          <DiffView diff={prompt.diff} maxHeight={260} />
        </>
      ) : (
        <pre className="permission__subject">{prompt.subject}</pre>
      )}
      {outside && <div className="permission__note">Файл находится вне папки проекта.</div>}
      <div className="permission__question">Разрешить?</div>
      <div className="permission__options" role="listbox">
        {OPTIONS.map((o, i) => (
          <button
            key={o.decision}
            role="option"
            aria-selected={i === selected}
            className={`permission__option${i === selected ? " permission__option--selected" : ""}`}
            onMouseEnter={() => setSelected(i)}
            onClick={() => answer(o.decision)}
            disabled={done}
          >
            <span className="permission__pointer">{i === selected ? "❯" : ""}</span>
            {i + 1}. {o.label}
            {o.decision === "deny" && <span className="permission__key">(Esc)</span>}
          </button>
        ))}
      </div>
    </div>
  );
}

function DiffStats({ diff }: { diff: string }) {
  const { added, removed } = diffStats(diff);
  return (
    <span className="diff-stats">
      <span className="diff-stats__add">+{added}</span> <span className="diff-stats__del">−{removed}</span>
    </span>
  );
}
