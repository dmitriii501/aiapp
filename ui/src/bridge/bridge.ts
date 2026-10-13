import type { MethodMap, NotificationMap } from "./protocol";
import { createMock } from "./mock";

// JSON-RPC 2.0 client over the WebView2 message channel. The UI only expresses intent;
// the core decides what actually happens (docs/ARCHITECTURE.md, "Мост UI ↔ ядро").

interface WebView2 {
  postMessage(message: unknown): void;
  addEventListener(type: "message", listener: (e: MessageEvent) => void): void;
}

export interface RpcMessage {
  jsonrpc: "2.0";
  id?: number | string;
  method?: string;
  params?: unknown;
  result?: unknown;
  error?: { code: number; message: string };
}

export class RpcError extends Error {
  constructor(
    readonly code: number,
    message: string,
  ) {
    super(message);
  }
}

type Listener = (payload: never) => void;

const webview = (window as unknown as { chrome?: { webview?: WebView2 } }).chrome?.webview;

class Bridge {
  readonly kind: "webview2" | "mock" = webview ? "webview2" : "mock";

  private nextId = 1;
  private pending = new Map<number, { resolve: (v: unknown) => void; reject: (e: Error) => void }>();
  private listeners = new Map<string, Set<Listener>>();
  private send: (message: RpcMessage) => void;

  constructor() {
    const receive = (m: RpcMessage) => this.receive(m);
    if (webview) {
      webview.addEventListener("message", (e) => receive(e.data as RpcMessage));
      this.send = (m) => webview.postMessage(m);
    } else {
      // `npm run dev` in a plain browser: an in-memory stand-in for the core.
      this.send = createMock(receive);
    }
  }

  call<M extends keyof MethodMap>(method: M, params: MethodMap[M]["params"]): Promise<MethodMap[M]["result"]> {
    const id = this.nextId++;
    const promise = new Promise<unknown>((resolve, reject) => this.pending.set(id, { resolve, reject }));
    this.send({ jsonrpc: "2.0", id, method, params });
    return promise as Promise<MethodMap[M]["result"]>;
  }

  on<N extends keyof NotificationMap>(method: N, listener: (payload: NotificationMap[N]) => void): () => void {
    const set = this.listeners.get(method) ?? new Set();
    set.add(listener as Listener);
    this.listeners.set(method, set);
    return () => set.delete(listener as Listener);
  }

  private receive(message: RpcMessage) {
    if (message.method) {
      this.listeners.get(message.method)?.forEach((l) => (l as (p: unknown) => void)(message.params));
      return;
    }
    if (typeof message.id !== "number") return;
    const pending = this.pending.get(message.id);
    if (!pending) return;
    this.pending.delete(message.id);
    if (message.error) pending.reject(new RpcError(message.error.code, message.error.message));
    else pending.resolve(message.result);
  }
}

export const bridge = new Bridge();

export function errorMessage(e: unknown): string {
  return e instanceof Error ? e.message : String(e);
}
