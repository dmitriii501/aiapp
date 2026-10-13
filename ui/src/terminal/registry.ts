import { Terminal, type ITheme } from "@xterm/xterm";
import { FitAddon } from "@xterm/addon-fit";
import "@xterm/xterm/css/xterm.css";
import { bridge } from "../bridge/bridge";
import { Methods } from "../bridge/protocol";

/**
 * xterm.js instances live here, outside React: they keep their scrollback while the panel is hidden or another
 * tab is shown. The host owns the shell; this module only forwards keystrokes and sizes.
 */

interface Instance {
  term: Terminal;
  fit: FitAddon;
  pane: HTMLElement;
}

const instances = new Map<string, Instance>();
let host: HTMLElement | null = null;
let activeId: string | undefined;
let onToggle: () => void = () => {};
/** Output that arrived before the terminal was registered (the shell prints its prompt right away). */
const early = new Map<string, string[]>();

const DARK: ITheme = {
  background: "#1b1b1b",
  foreground: "#cccccc",
  cursor: "#cccccc",
  selectionBackground: "#264f78",
  black: "#000000",
  red: "#cd3131",
  green: "#0dbc79",
  yellow: "#e5e510",
  blue: "#2472c8",
  magenta: "#bc3fbc",
  cyan: "#11a8cd",
  white: "#e5e5e5",
  brightBlack: "#666666",
  brightRed: "#f14c4c",
  brightGreen: "#23d18b",
  brightYellow: "#f5f543",
  brightBlue: "#3b8eea",
  brightMagenta: "#d670d6",
  brightCyan: "#29b8db",
  brightWhite: "#ffffff",
};

const LIGHT: ITheme = {
  background: "#ffffff",
  foreground: "#3b3b3b",
  cursor: "#3b3b3b",
  selectionBackground: "#add6ff",
  black: "#000000",
  red: "#cd3131",
  green: "#00bc00",
  yellow: "#949800",
  blue: "#0451a5",
  magenta: "#bc05bc",
  cyan: "#0598bc",
  white: "#555555",
  brightBlack: "#666666",
  brightRed: "#cd3131",
  brightGreen: "#14ce14",
  brightYellow: "#b5ba00",
  brightBlue: "#0451a5",
  brightMagenta: "#bc05bc",
  brightCyan: "#0598bc",
  brightWhite: "#a5a5a5",
};

const scheme = window.matchMedia("(prefers-color-scheme: dark)");
const currentTheme = () => (scheme.matches ? DARK : LIGHT);
scheme.addEventListener("change", () => {
  for (const { term } of instances.values()) term.options.theme = currentTheme();
});

/** The element the panes are mounted in. Pass null when the panel goes away; terminals keep running. */
export function setHost(el: HTMLElement | null, toggle: () => void) {
  host = el;
  onToggle = toggle;
  if (el) {
    for (const { pane } of instances.values()) el.appendChild(pane);
    show(activeId);
  }
}

/** Starts a shell in `cwd`. The pane is mounted and fitted first, so the shell starts at the right size. */
export async function spawn(cwd: string): Promise<{ id: string; title: string }> {
  if (!host) throw new Error("Панель терминала не открыта.");

  const pane = document.createElement("div");
  pane.className = "terminal__pane";
  host.appendChild(pane);
  for (const other of instances.values()) other.pane.style.display = "none";

  const term = new Terminal({
    fontFamily: '"Cascadia Mono", "Cascadia Code", Consolas, monospace',
    fontSize: 13,
    cursorBlink: true,
    scrollback: 5000,
    theme: currentTheme(),
    allowProposedApi: false,
  });
  const fit = new FitAddon();
  term.loadAddon(fit);
  term.open(pane);
  fit.fit();

  try {
    const info = await bridge.call(Methods.TerminalOpen, { workingDirectory: cwd, columns: term.cols, rows: term.rows });
    const id = info.terminalId;
    instances.set(id, { term, fit, pane });
    for (const chunk of early.get(id) ?? []) term.write(chunk);
    early.delete(id);

    term.onData((data) => void bridge.call(Methods.TerminalInput, { terminalId: id, data }).catch(() => {}));
    term.onResize(({ cols, rows }) => void bridge.call(Methods.TerminalResize, { terminalId: id, columns: cols, rows }).catch(() => {}));
    term.attachCustomKeyEventHandler((e) => {
      if (e.type !== "keydown") return true;
      if (e.ctrlKey && e.key === "`") {
        onToggle();
        return false;
      }
      // Ctrl+C copies when there is a selection; otherwise it goes to the shell as an interrupt.
      if (e.ctrlKey && !e.shiftKey && e.key.toLowerCase() === "c" && term.hasSelection()) {
        void navigator.clipboard.writeText(term.getSelection()).catch(() => {});
        term.clearSelection();
        return false;
      }
      return true;
    });

    show(id);
    return { id, title: info.title };
  } catch (e) {
    term.dispose();
    pane.remove();
    show(activeId);
    throw e;
  }
}

export function write(id: string, data: string) {
  const instance = instances.get(id);
  if (instance) instance.term.write(data);
  else early.set(id, [...(early.get(id) ?? []), data]);
}

export function exited(id: string, code: number) {
  instances.get(id)?.term.write(`\r\n\x1b[2m[Процесс завершён, код ${code}]\x1b[0m\r\n`);
}

/** Shows one terminal and hides the others. */
export function show(id: string | undefined) {
  activeId = id;
  for (const [key, { pane, fit, term }] of instances) {
    const visible = key === id;
    pane.style.display = visible ? "" : "none";
    if (visible) {
      // Fit after layout, then take the keyboard.
      requestAnimationFrame(() => {
        try {
          fit.fit();
        } catch {
          // Not laid out yet; the resize observer fits again.
        }
        term.focus();
      });
    }
  }
}

export function fitActive() {
  if (!activeId) return;
  try {
    instances.get(activeId)?.fit.fit();
  } catch {
    // Hidden or zero-sized.
  }
}

export function focus(id: string | undefined) {
  if (id) instances.get(id)?.term.focus();
}

export function dispose(id: string) {
  const instance = instances.get(id);
  if (!instance) return;
  instance.term.dispose();
  instance.pane.remove();
  instances.delete(id);
  if (activeId === id) activeId = undefined;
}
