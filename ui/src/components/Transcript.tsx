import { memo, useState } from "react";
import type { Item } from "../store";
import type { ToolStatus } from "../bridge/protocol";
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
          <div className="entry__body">{item.text}</div>
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

type RowStatus = ToolStatus | "waiting";

/** "Edit(src/a.cs) — create" → ["Edit", "src/a.cs"] */
function parseSummary(tool: string, summary: string): string {
  const m = summary.match(/^\w+\((.*)\)(?:\s+—.*)?$/s);
  return m ? m[1] : summary === tool ? "" : summary;
}

/** One-line result under a tool call, like Claude Code's "⎿ Read 42 lines". */
function resultLine(tool: string, status: RowStatus, output?: string): string {
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
      return "Файл изменён";
    case "Write":
      return "Файл записан";
    case "Shell":
      return text === "(no output)" ? "Нет вывода" : lines > 1 ? `${first} (+${lines - 1} строк)` : first;
    default:
      return first;
  }
}

function ToolEntry({ tool, summary, status, output }: { tool: string; summary: string; status: RowStatus; output?: string }) {
  const [open, setOpen] = useState(false);
  const detail = parseSummary(tool, summary);
  const expandable = !!output && status !== "running" && status !== "waiting";

  return (
    <div className={`entry entry--tool entry--${status}`}>
      <span className="entry__marker">●</span>
      <div className="entry__body">
        <div className="tool-call">
          <span className="tool-call__name">{tool}</span>
          {detail && <span className="tool-call__args">({detail})</span>}
        </div>
        <button className="tool-result" onClick={() => expandable && setOpen((o) => !o)} disabled={!expandable} aria-expanded={open}>
          <span className="tool-result__elbow">⎿</span>
          <span className="tool-result__text">{resultLine(tool, status, output)}</span>
          {expandable && <span className="tool-result__toggle">{open ? "скрыть" : "показать"}</span>}
        </button>
        {open && output && <pre className="tool-output">{output}</pre>}
      </div>
    </div>
  );
}
