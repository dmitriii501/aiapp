import { memo, useState } from "react";
import { useStore, type Item } from "../store";
import type { ToolStatus } from "../bridge/protocol";
import { DiffView, diffStats } from "./DiffView";
import { Markdown } from "./Markdown";

export function Transcript({ items }: { items: Item[] }) {
  return (
    <>
      {items.map((item) => (item.kind === "permission" ? null : <Row key={item.id} item={item} />))}
    </>
  );
}

const Row = memo(function Row({ item }: { item: Item }) {
  switch (item.kind) {
    case "user":
      return (
        <div className="entry entry--user">
          <span className="entry__marker">&gt;</span>
          <div className="entry__body">
            {item.text}
            {item.checkpointId && item.files ? <ChangedFiles checkpointId={item.checkpointId} files={item.files} /> : null}
          </div>
        </div>
      );
    case "assistant":
      return (
        <div className="entry entry--assistant">
          <span className="entry__marker">●</span>
          <div className="entry__body">
            <Markdown text={item.text} />
          </div>
        </div>
      );
    case "tool":
      return <ToolEntry {...item} />;
    case "error":
      return (
        <div className="entry entry--error">
          <span className="entry__marker">●</span>
          <div className="entry__body">{item.text}</div>
        </div>
      );
    case "notice":
      return <div className="entry entry--notice">{item.text}</div>;
    default:
      return null;
  }
});

function filesWord(n: number) {
  const mod10 = n % 10;
  const mod100 = n % 100;
  if (mod10 === 1 && mod100 !== 11) return "файл";
  if (mod10 >= 2 && mod10 <= 4 && (mod100 < 12 || mod100 > 14)) return "файла";
  return "файлов";
}

/** Under a prompt: how many files the agent changed for it, and the way back. */
function ChangedFiles({ checkpointId, files }: { checkpointId: string; files: number }) {
  const running = useStore((s) => (s.currentId ? s.chats[s.currentId]?.running : false));
  const openRollback = useStore((s) => s.openRollback);
  return (
    <div className="entry__changes">
      Изменено {files} {filesWord(files)}
      <button className="entry__rollback" disabled={running} title={running ? "Дождитесь конца ответа" : undefined} onClick={() => void openRollback(checkpointId)}>
        Откатить…
      </button>
    </div>
  );
}

type RowStatus = ToolStatus | "waiting";

/** "Edit(src/a.cs) — create" → ["Edit", "src/a.cs"] */
function parseSummary(tool: string, summary: string): string {
  const m = summary.match(/^\w+\((.*)\)(?:\s+—.*)?$/s);
  return m ? m[1] : summary === tool ? "" : summary;
}

/** One-line result under a tool call, like Claude Code's "⎿ Read 42 lines". */
function resultLine(tool: string, status: RowStatus, output?: string, diff?: string): string {
  if (status === "running") return "Выполняется…";
  if (status === "waiting") return "Ждёт подтверждения";
  if (status === "denied") return "Отклонено";
  if (status === "interrupted") return "Прервано";
  const text = (output ?? "").trim();
  const first = text.split("\n", 1)[0];
  if (status === "error") return first || "Ошибка";

  const lines = text ? text.split("\n").length : 0;
  switch (tool) {
    case "Read": {
      const count = text.split("\n").filter((l) => /^\s*\d+\t/.test(l)).length;
      return text.endsWith("…") ? `Прочитано больше ${count} строк` : `Прочитано строк: ${count}`;
    }
    case "Glob":
      return first === "No files found." ? "Файлы не найдены" : `Найдено файлов: ${lines}`;
    case "Grep":
      return first === "No matches." ? "Совпадений нет" : `Совпадений: ${lines}`;
    case "Edit":
    case "Write": {
      if (!diff) return tool === "Edit" ? "Файл изменён" : "Файл записан";
      const { added, removed } = diffStats(diff);
      return `Добавлено строк: ${added}, удалено: ${removed}`;
    }
    case "Shell":
      return text === "(no output)" ? "Нет вывода" : lines > 1 ? `${first} (+${lines - 1} строк)` : first;
    default:
      return first;
  }
}

function ToolEntry({ tool, summary, status, output, diff }: { tool: string; summary: string; status: RowStatus; output?: string; diff?: string }) {
  const [open, setOpen] = useState(false);
  const [diffHidden, setDiffHidden] = useState(false);
  const detail = parseSummary(tool, summary);
  const finished = status !== "running" && status !== "waiting";
  // A change is always shown; other output is behind a toggle.
  const hasDiff = !!diff && status === "done";
  const expandable = finished && (hasDiff || !!output);
  const expanded = hasDiff ? !diffHidden : open;
  const toggle = () => (hasDiff ? setDiffHidden((h) => !h) : setOpen((o) => !o));

  return (
    <div className={`entry entry--tool entry--${status}`}>
      <span className="entry__marker">●</span>
      <div className="entry__body">
        <div className="tool-call">
          <span className="tool-call__name">{tool}</span>
          {detail && <span className="tool-call__args">({detail})</span>}
        </div>
        <button className="tool-result" onClick={() => expandable && toggle()} disabled={!expandable} aria-expanded={expanded}>
          <span className="tool-result__elbow">⎿</span>
          <span className="tool-result__text">{resultLine(tool, status, output, diff)}</span>
          {expandable && <span className="tool-result__toggle">{expanded ? "скрыть" : "показать"}</span>}
        </button>
        {hasDiff && expanded && <DiffView diff={diff!} />}
        {!hasDiff && open && output && <pre className="tool-output">{output}</pre>}
      </div>
    </div>
  );
}
