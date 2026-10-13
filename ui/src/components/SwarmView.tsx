import { useEffect, useMemo, useRef, useState, type FormEvent } from "react";
import { AlertTriangle, FolderOpen, Pause, Play } from "lucide-react";
import { useStore } from "../store";
import type { Decision, PermissionPrompt, SwarmAgent, SwarmDetail, SwarmStatus, SwarmTask, SwarmTaskStatus } from "../bridge/protocol";
import { folderName } from "../format";
import { Markdown } from "./Markdown";

export const STATUS_LABELS: Record<SwarmStatus, string> = {
  running: "идёт",
  paused: "на паузе",
  stalled: "остановился",
  done: "готово",
  failed: "ошибка",
};

const AGENT_STATUS: Record<SwarmAgent["status"], string> = {
  idle: "ждёт",
  working: "работает",
  error: "ошибка",
};

const COLUMNS: { status: SwarmTaskStatus; title: string }[] = [
  { status: "pending", title: "Ожидают" },
  { status: "in_progress", title: "В работе" },
  { status: "done", title: "Готово" },
  { status: "failed", title: "Не вышло" },
];

const tokens = (n: number) => (n >= 1_000_000 ? `${(n / 1_000_000).toFixed(1)}M` : n >= 1000 ? `${Math.round(n / 1000)}k` : String(n));

/** Start page of the swarm section: the goal, the folder, the limits. */
export function SwarmWelcome() {
  const workspaces = useStore((s) => s.workspaces);
  const settings = useStore((s) => s.settings);
  const startSwarm = useStore((s) => s.startSwarm);
  const pickFolder = useStore((s) => s.pickFolder);
  const setSettingsOpen = useStore((s) => s.setSettingsOpen);
  const [folder, setFolder] = useState(workspaces[0]?.path ?? "");
  const [goal, setGoal] = useState("");
  const [workers, setWorkers] = useState(3);
  const [budget, setBudget] = useState(3000);
  const [minutes, setMinutes] = useState(120);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    if (!folder && workspaces[0]) setFolder(workspaces[0].path);
  }, [folder, workspaces]);

  const notReady = settings && !settings.ready;
  const submit = async (e: FormEvent) => {
    e.preventDefault();
    if (!folder || !goal.trim()) return;
    setBusy(true);
    await startSwarm({ workingDirectory: folder, goal: goal.trim(), workers, maxTokens: budget * 1000, maxMinutes: minutes });
    setBusy(false);
  };

  return (
    <div className="welcome">
      <h1 className="welcome__title">Рой агентов</h1>
      <p className="welcome__subtitle">Лид разбивает цель на задачи, воркеры делают их параллельно, каждый в своей git-ветке</p>

      {notReady && (
        <div className="banner">
          <AlertTriangle size={14} />
          <span>Модель не подключена. Укажите API-ключ или адрес локального сервера.</span>
          <button className="link" onClick={() => setSettingsOpen(true)}>
            Открыть настройки
          </button>
        </div>
      )}

      <form className="swarm-form" onSubmit={(e) => void submit(e)}>
        <label className="field">
          <span className="field__label">Папка проекта</span>
          <div className="input-row">
            <select className="input" value={folder} onChange={(e) => setFolder(e.target.value)}>
              {workspaces.length === 0 && <option value="">Сначала откройте папку</option>}
              {workspaces.map((w) => (
                <option key={w.path} value={w.path}>
                  {w.name} — {w.path}
                </option>
              ))}
            </select>
            <button type="button" className="btn btn--secondary" onClick={() => void pickFolder()} title="Открыть другую папку">
              <FolderOpen size={14} />
            </button>
          </div>
          <span className="field__hint">Нужен git-репозиторий хотя бы с одним коммитом. Воркеры не видят незакоммиченные правки.</span>
        </label>

        <label className="field">
          <span className="field__label">Цель</span>
          <textarea
            className="input swarm-form__goal"
            value={goal}
            onChange={(e) => setGoal(e.target.value)}
            rows={4}
            placeholder="Например: добавить тесты на парсер конфигурации и описать формат в README"
          />
        </label>

        <div className="swarm-form__limits">
          <label className="field">
            <span className="field__label">Воркеров не больше</span>
            <input className="input" type="number" min={1} max={8} value={workers} onChange={(e) => setWorkers(Number(e.target.value))} />
          </label>
          <label className="field">
            <span className="field__label">Токенов, тыс.</span>
            <input className="input" type="number" min={10} step={100} value={budget} onChange={(e) => setBudget(Number(e.target.value))} />
          </label>
          <label className="field">
            <span className="field__label">Минут</span>
            <input className="input" type="number" min={1} max={720} value={minutes} onChange={(e) => setMinutes(Number(e.target.value))} />
          </label>
        </div>
        <p className="field__hint">
          При достижении лимита рой встанет на паузу, его можно продолжить. Слияние веток в вашу папку лид делает только после вашего подтверждения.
        </p>

        <div>
          <button className="btn" type="submit" disabled={busy || !folder || !goal.trim()}>
            Запустить рой
          </button>
        </div>
      </form>
    </div>
  );
}

export function SwarmView() {
  const swarm = useStore((s) => s.swarm);
  if (!swarm) return <div className="swarm swarm--loading">Загрузка…</div>;
  return <Swarm detail={swarm} />;
}

function Swarm({ detail }: { detail: SwarmDetail }) {
  const stopSwarm = useStore((s) => s.stopSwarm);
  const resumeSwarm = useStore((s) => s.resumeSwarm);
  const { summary } = detail;
  const finished = summary.status === "done";
  const [extra, setExtra] = useState(false);
  const share = Math.min(100, (summary.tokensUsed / Math.max(1, summary.maxTokens)) * 100);

  return (
    <div className="swarm">
      <header className="swarm__header">
        <div className="swarm__titleline">
          <h2 className="swarm__goal" title={summary.goal}>
            {summary.goal}
          </h2>
          <span className={`chip chip--${summary.running ? "running" : summary.status}`}>{summary.running ? "идёт" : STATUS_LABELS[summary.status]}</span>
          <span className="swarm__spacer" />
          {summary.running ? (
            <button className="btn btn--secondary" onClick={() => void stopSwarm()} title="Поставить рой на паузу">
              <Pause size={14} /> Пауза
            </button>
          ) : finished ? null : (
            <>
              <button className="btn" onClick={() => void resumeSwarm()}>
                <Play size={14} /> Продолжить
              </button>
              <button className="btn btn--secondary" onClick={() => setExtra((v) => !v)} title="Продолжить с большим бюджетом">
                + бюджет
              </button>
            </>
          )}
        </div>
        <div className="swarm__meta">
          <span title={summary.workingDirectory}>{folderName(summary.workingDirectory)}</span>
          <span>
            Токены {tokens(summary.tokensUsed)} / {tokens(summary.maxTokens)}
          </span>
          <span className="budget" aria-hidden>
            <span className={`budget__bar${share > 90 ? " budget__bar--high" : ""}`} style={{ width: `${share}%` }} />
          </span>
          <span>Вызовов модели {detail.modelCalls}</span>
          <span>
            Агентов {detail.agents.length} из {detail.maxAgents}
          </span>
        </div>
        {extra && !summary.running && !finished && <MoreBudget current={summary.maxTokens} onResume={(n) => void resumeSwarm(n).then(() => setExtra(false))} />}
      </header>

      {detail.report && (
        <section className={`swarm__report${summary.status === "failed" ? " swarm__report--failed" : ""}`}>
          <h3 className="swarm__heading">{summary.status === "done" ? "Отчёт лида" : "Что случилось"}</h3>
          {summary.status === "done" ? <Markdown text={detail.report} /> : <p>{detail.report}</p>}
        </section>
      )}

      {detail.pendingPermissions.length > 0 && <Approvals prompts={detail.pendingPermissions} />}

      <div className="swarm__body">
        <Board tasks={detail.tasks} />
        <div className="swarm__side">
          <Agents agents={detail.agents} tasks={detail.tasks} />
          <Feed detail={detail} />
        </div>
      </div>
    </div>
  );
}

function MoreBudget({ current, onResume }: { current: number; onResume: (maxTokens: number) => void }) {
  const [value, setValue] = useState(Math.round((current * 2) / 1000));
  return (
    <div className="swarm__more">
      <span>Новый бюджет, тыс. токенов:</span>
      <input className="input" type="number" min={10} step={100} value={value} onChange={(e) => setValue(Number(e.target.value))} />
      <button className="btn" onClick={() => onResume(value * 1000)}>
        Продолжить
      </button>
    </div>
  );
}

/** Questions from agents. Identical ones (the same command from several workers) are shown once and answered together. */
function Approvals({ prompts }: { prompts: PermissionPrompt[] }) {
  const respond = useStore((s) => s.respond);
  const groups = useMemo(() => {
    const map = new Map<string, PermissionPrompt[]>();
    for (const p of prompts) {
      const key = `${p.tool}|${p.subject}|${p.summary.replace(/^\[[^\]]+\]\s*/, "")}`;
      map.set(key, [...(map.get(key) ?? []), p]);
    }
    return [...map.values()];
  }, [prompts]);

  const answer = (group: PermissionPrompt[], decision: Decision) => group.forEach((p) => void respond(p.requestId, decision));

  return (
    <section className="swarm__approvals" aria-label="Нужно ваше решение">
      <h3 className="swarm__heading">Ждут решения: {prompts.length}</h3>
      {groups.map((group) => {
        const first = group[0];
        const who = group.map((p) => /^\[([^\]]+)\]/.exec(p.summary)?.[1] ?? "агент").join(", ");
        return (
          <div key={first.requestId} className="approval">
            <div className="approval__text">
              <strong>{who}</strong>
              <span className="approval__tool">{first.summary.replace(/^\[[^\]]+\]\s*/, "")}</span>
              <span className="approval__reason">{first.reason}</span>
            </div>
            <div className="approval__buttons">
              <button className="btn" onClick={() => answer(group, "allow")}>
                {group.length > 1 ? `Разрешить всем (${group.length})` : "Разрешить"}
              </button>
              <button className="btn btn--secondary" onClick={() => answer(group, "always")}>
                Всегда
              </button>
              <button className="btn btn--secondary" onClick={() => answer(group, "deny")}>
                Отклонить
              </button>
            </div>
          </div>
        );
      })}
    </section>
  );
}

function Board({ tasks }: { tasks: SwarmTask[] }) {
  return (
    <section className="board" aria-label="Доска задач">
      {COLUMNS.map((col) => {
        const items = tasks.filter((t) => t.status === col.status);
        return (
          <div key={col.status} className="board__column">
            <h3 className="swarm__heading">
              {col.title} <span className="board__count">{items.length}</span>
            </h3>
            {items.length === 0 && col.status === "pending" && tasks.length === 0 && <p className="board__empty">Лид ещё планирует.</p>}
            {items.map((t) => (
              <TaskCard key={t.seq} task={t} tasks={tasks} />
            ))}
          </div>
        );
      })}
    </section>
  );
}

function TaskCard({ task, tasks }: { task: SwarmTask; tasks: SwarmTask[] }) {
  const [open, setOpen] = useState(false);
  const blockedBy = task.dependsOn.filter((d) => tasks.find((t) => t.seq === d)?.status !== "done");
  return (
    <div className={`task task--${task.status}`} onClick={() => setOpen((v) => !v)}>
      <div className="task__title">
        <span className="task__num">#{task.seq}</span> {task.title}
      </div>
      <div className="task__meta">
        {task.assignee && <span className="task__who">{task.assignee}</span>}
        {blockedBy.length > 0 && <span className="task__blocked">ждёт {blockedBy.map((d) => `#${d}`).join(", ")}</span>}
      </div>
      {open && (
        <div className="task__details">
          {task.description && <p>{task.description}</p>}
          {task.result && (
            <p className="task__result">
              <strong>Итог:</strong> {task.result}
            </p>
          )}
        </div>
      )}
    </div>
  );
}

function Agents({ agents, tasks }: { agents: SwarmAgent[]; tasks: SwarmTask[] }) {
  return (
    <section className="agents" aria-label="Агенты">
      <h3 className="swarm__heading">Агенты</h3>
      {agents.map((a) => {
        const current = tasks.find((t) => t.assignee === a.name && t.status === "in_progress");
        return (
          <div key={a.name} className="agent" title={a.branch ?? a.workingDirectory}>
            <span className={`agent__dot agent__dot--${a.status}`} />
            <span className="agent__name">{a.name}</span>
            <span className="agent__state">{current ? `#${current.seq} ${current.title}` : AGENT_STATUS[a.status]}</span>
          </div>
        );
      })}
    </section>
  );
}

function Feed({ detail }: { detail: SwarmDetail }) {
  const [tab, setTab] = useState<"log" | "messages">("log");
  const ref = useRef<HTMLDivElement>(null);
  const count = tab === "log" ? detail.log.length : detail.messages.length;

  useEffect(() => {
    const el = ref.current;
    if (el) el.scrollTop = el.scrollHeight;
  }, [count, tab]);

  return (
    <section className="feed" aria-label="Лента">
      <div className="feed__tabs" role="tablist">
        <button role="tab" aria-selected={tab === "log"} className={`feed__tab${tab === "log" ? " feed__tab--active" : ""}`} onClick={() => setTab("log")}>
          Действия
        </button>
        <button role="tab" aria-selected={tab === "messages"} className={`feed__tab${tab === "messages" ? " feed__tab--active" : ""}`} onClick={() => setTab("messages")}>
          Сообщения <span className="board__count">{detail.messages.length}</span>
        </button>
      </div>
      <div className="feed__lines" ref={ref}>
        {tab === "log" && detail.log.length === 0 && <p className="board__empty">Пока тихо.</p>}
        {tab === "log" &&
          detail.log.map((l) => (
            <div key={l.seq} className={`feed__line feed__line--${l.kind}`}>
              <span className="feed__agent">{l.agent}</span>
              <span className="feed__text">{l.text}</span>
            </div>
          ))}
        {tab === "messages" && detail.messages.length === 0 && <p className="board__empty">Агенты пока не писали друг другу.</p>}
        {tab === "messages" &&
          detail.messages.map((m) => (
            <div key={m.id} className="feed__line feed__line--said">
              <span className="feed__agent">
                {m.from} → {m.to}
              </span>
              <span className="feed__text">{m.text}</span>
            </div>
          ))}
      </div>
    </section>
  );
}
