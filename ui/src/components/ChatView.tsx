import { useEffect, useLayoutEffect, useRef, useState, type KeyboardEvent as ReactKeyboardEvent } from "react";
import { useStore, type Chat } from "../store";
import { fromTerminal } from "../keys";
import { folderName } from "../format";
import { Transcript } from "./Transcript";
import { PermissionPrompt } from "./PermissionPrompt";
import { MODE_LABELS, MODE_MARKERS } from "./modes";

export function ChatView({ sessionId }: { sessionId: string }) {
  const session = useStore((s) => s.sessions.find((x) => x.id === sessionId));
  const chat = useStore((s) => s.chats[sessionId]);

  return (
    <div className="session">
      <header className="session__tabs">
        <div className="tab tab--active" title={session?.workingDirectory}>
          <span className="tab__title">{session?.title ?? "Сессия"}</span>
        </div>
        {session && <span className="session__path">{session.workingDirectory}</span>}
      </header>
      {chat ? <Body chat={chat} folder={session ? folderName(session.workingDirectory) : ""} /> : <div className="session__loading" />}
    </div>
  );
}

function Body({ chat, folder }: { chat: Chat; folder: string }) {
  const cancel = useStore((s) => s.cancel);
  const cycleMode = useStore((s) => s.cycleMode);
  const settingsOpen = useStore((s) => s.settingsOpen);
  const scroller = useRef<HTMLDivElement>(null);
  const stick = useRef(true);
  const permission = chat.items.find((i) => i.kind === "permission");

  // Follow the output unless the user scrolled up to read.
  useLayoutEffect(() => {
    const el = scroller.current;
    if (el && stick.current) el.scrollTop = el.scrollHeight;
  }, [chat.items, permission]);

  // Shift+Tab cycles the permission mode; Esc interrupts (the permission prompt handles its own Esc).
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (settingsOpen || fromTerminal(e)) return;
      if (e.key === "Tab" && e.shiftKey) {
        e.preventDefault();
        void cycleMode();
      } else if (e.key === "Escape" && chat.running && !permission) {
        e.preventDefault();
        void cancel();
      }
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [chat.running, permission, settingsOpen, cancel, cycleMode]);

  const onScroll = () => {
    const el = scroller.current;
    if (el) stick.current = el.scrollHeight - el.scrollTop - el.clientHeight < 60;
  };

  const last = chat.items[chat.items.length - 1];
  const streaming = last?.kind === "assistant" && last.streaming;

  return (
    <>
      <div className="session__scroll" ref={scroller} onScroll={onScroll}>
        <div className="transcript">
          {chat.items.length === 0 && <p className="transcript__empty">Новая сессия в папке «{folder}». Опишите задачу ниже.</p>}
          <Transcript items={chat.items} />
          {chat.running && !permission && !streaming && <Working startedAt={chat.startedAt} />}
        </div>
      </div>
      <div className="session__bottom">
        {permission?.kind === "permission" ? <PermissionPrompt key={permission.prompt.requestId} prompt={permission.prompt} /> : <Composer running={chat.running} />}
        <div className="hints">
          <span className={`hints__mode hints__mode--${chat.mode}`}>
            {MODE_MARKERS[chat.mode] && <span className="hints__marker">{MODE_MARKERS[chat.mode]}</span>}
            {MODE_LABELS[chat.mode]}
            <span className="hints__key"> · Shift+Tab — сменить</span>
          </span>
          <span>{chat.running ? "Esc — прервать" : "Enter — отправить · Shift+Enter — новая строка"}</span>
        </div>
      </div>
    </>
  );
}

const SPINNER = ["·", "✢", "✳", "✶", "✻", "✽", "✻", "✶", "✳", "✢"];

function Working({ startedAt }: { startedAt?: number }) {
  const [frame, setFrame] = useState(0);
  useEffect(() => {
    const t = setInterval(() => setFrame((f) => f + 1), 120);
    return () => clearInterval(t);
  }, []);
  const seconds = startedAt ? Math.max(0, Math.floor((Date.now() - startedAt) / 1000)) : 0;

  return (
    <div className="working" aria-live="polite">
      <span className="working__glyph">{SPINNER[frame % SPINNER.length]}</span>
      <span>Работаю…</span>
      <span className="working__meta">
        {seconds} с · Esc — прервать
      </span>
    </div>
  );
}

function Composer({ running }: { running: boolean }) {
  const send = useStore((s) => s.send);
  const [text, setText] = useState("");
  const area = useRef<HTMLTextAreaElement>(null);

  useEffect(() => area.current?.focus(), [running]);

  // Grow with the content up to a limit.
  useLayoutEffect(() => {
    const el = area.current;
    if (!el) return;
    el.style.height = "auto";
    el.style.height = `${Math.min(el.scrollHeight, 220)}px`;
  }, [text]);

  const onKeyDown = (e: ReactKeyboardEvent<HTMLTextAreaElement>) => {
    if (e.key === "Enter" && !e.shiftKey && !e.nativeEvent.isComposing) {
      e.preventDefault();
      const value = text.trim();
      if (!value || running) return;
      setText("");
      void send(value);
    }
  };

  return (
    <div className="prompt">
      <span className="prompt__caret">&gt;</span>
      <textarea
        ref={area}
        className="prompt__input"
        value={text}
        onChange={(e) => setText(e.target.value)}
        onKeyDown={onKeyDown}
        placeholder={running ? "Агент работает. Можно набрать следующее сообщение." : "Опишите задачу"}
        rows={1}
        spellCheck={false}
      />
    </div>
  );
}
