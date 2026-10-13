import { useMemo } from "react";

type Row =
  | { kind: "hunk"; text: string }
  | { kind: "add" | "del" | "ctx"; text: string; oldNo?: number; newNo?: number }
  | { kind: "cut" };

/** Parses the unified hunks produced by the core (`@@ -a,b +c,d @@`, then ' ', '-' and '+' lines). */
function parse(diff: string): Row[] {
  const rows: Row[] = [];
  let oldNo = 0;
  let newNo = 0;
  for (const line of diff.split("\n")) {
    const head = line.match(/^@@ -(\d+),\d+ \+(\d+),\d+ @@/);
    if (head) {
      oldNo = Number(head[1]);
      newNo = Number(head[2]);
      if (rows.length > 0) rows.push({ kind: "hunk", text: "⋯" });
    } else if (line.startsWith("+")) {
      rows.push({ kind: "add", text: line.slice(1), newNo: newNo++ });
    } else if (line.startsWith("-")) {
      rows.push({ kind: "del", text: line.slice(1), oldNo: oldNo++ });
    } else if (line.startsWith(" ")) {
      rows.push({ kind: "ctx", text: line.slice(1), oldNo: oldNo++, newNo: newNo++ });
    } else if (line.startsWith("…")) {
      rows.push({ kind: "cut" });
    }
  }
  return rows;
}

export function diffStats(diff: string): { added: number; removed: number } {
  let added = 0;
  let removed = 0;
  for (const line of diff.split("\n")) {
    if (line.startsWith("+")) added++;
    else if (line.startsWith("-")) removed++;
  }
  return { added, removed };
}

const SIGN = { add: "+", del: "−", ctx: " " } as const;

export function DiffView({ diff, maxHeight }: { diff: string; maxHeight?: number }) {
  const rows = useMemo(() => parse(diff), [diff]);
  // Gutter wide enough for the largest line number in this diff.
  const digits = useMemo(() => String(Math.max(1, ...rows.map((r) => (r.kind === "add" || r.kind === "del" || r.kind === "ctx" ? Math.max(r.oldNo ?? 0, r.newNo ?? 0) : 0)))).length, [rows]);

  return (
    <div className="diff" style={{ maxHeight, ["--diff-digits" as string]: `${digits}ch` }} role="table" aria-label="Изменения в файле">
      {rows.map((row, i) => {
        if (row.kind === "hunk") return <div key={i} className="diff__sep">{row.text}</div>;
        if (row.kind === "cut") return <div key={i} className="diff__sep">Дальше изменения не показаны</div>;
        return (
          <div key={i} className={`diff__row diff__row--${row.kind}`} role="row">
            <span className="diff__no">{row.kind === "add" ? "" : row.oldNo}</span>
            <span className="diff__no">{row.kind === "del" ? "" : row.newNo}</span>
            <span className="diff__sign">{SIGN[row.kind]}</span>
            <span className="diff__text">{row.text || " "}</span>
          </div>
        );
      })}
    </div>
  );
}
