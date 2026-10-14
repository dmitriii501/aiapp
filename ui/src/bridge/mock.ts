import type { RpcMessage } from "./bridge";
import type { AgentEvent, SwarmDetail, SwarmLogLine, McpListResult, McpServerInfo, Mode, PermissionPrompt, SessionSummary, Settings, TranscriptItem, UpdateStatus, Workspace } from "./protocol";
import { Methods, Notifications } from "./protocol";

// In-memory stand-in for the core, so the UI can be developed with `npm run dev` in a browser.
// It mimics the real event flow: text, a tool call that needs approval, then a reply.

export function createMock(reply: (m: RpcMessage) => void): (m: RpcMessage) => void {
  const settings: Settings = {
    provider: "anthropic",
    model: "claude-opus-5-5",
    mode: "default",
    providers: [
      { id: "anthropic", hasKey: true },
      { id: "openai-compatible", hasKey: false },
    ],
    ready: true,
  };
  const workspaces: Workspace[] = [{ path: "C:\\Users\\dev\\projects\\kairo", name: "kairo" }];
  const sessions = new Map<string, { summary: SessionSummary; items: TranscriptItem[]; mode: Mode }>();
  const waiting = new Map<string, (decision: string) => void>();
  let updateStatus: UpdateStatus = {
    state: "available",
    currentVersion: "0.1.0",
    version: "0.2.0",
    notes: "- Встроенный терминал\n- Серверы MCP\n- Показ изменений файлов перед подтверждением",
  };
  async function runUpdate() {
    for (let progress = 0; progress <= 100; progress += 10) {
      updateStatus = { ...updateStatus, state: "downloading", progress };
      notify(Notifications.UpdateChanged, updateStatus);
      await sleep(250);
    }
    updateStatus = { state: "idle", currentVersion: "0.2.0" };
    notify(Notifications.UpdateChanged, updateStatus);
  }
  // A fake shell: echoes what is typed, so the terminal UI can be developed without the host.
  const shells = new Map<string, string>();
  const PROMPT = "PS C:\\Users\\dev\\projects\\kairo> ";
  const shellOut = (terminalId: string, data: string) => notify(Notifications.TerminalOutput, { terminalId, data });
  const mcpServers: McpServerInfo[] = [
    { name: "filesystem", transport: "stdio", status: "ready", tools: ["read_file", "write_file", "list_directory", "search_files"] },
    { name: "github", transport: "http", status: "ready", tools: ["create_issue", "list_pull_requests", "get_file_contents"] },
    { name: "postgres", transport: "stdio", status: "failed", error: "spawn psql-mcp ENOENT", tools: [] },
  ];
  const mcpList = (imported?: string[]): McpListResult => ({ servers: mcpServers.map((s) => ({ ...s })), configPath: "C:\\Users\\dev\\AppData\\Roaming\\Kairo\\mcp.json", imported });

  const notify = (method: string, params: unknown) => reply({ jsonrpc: "2.0", method, params });
  const event = (e: AgentEvent) => notify(Notifications.AgentEvent, e);
  const sleep = (ms: number) => new Promise((r) => setTimeout(r, ms));

  /** A plan-mode turn: a task list, then the plan the user has to approve. */
  async function runPlan(id: string, s: { summary: SessionSummary; items: TranscriptItem[]; mode: Mode }) {
    const todos = JSON.stringify([
      { content: "Найти все вызовы старой функции", status: "completed" },
      { content: "Составить план изменений", status: "in_progress" },
      { content: "Применить правки и запустить тесты", status: "pending" },
    ]);
    event({ sessionId: id, type: "tool_started", toolCallId: "p1", tool: "TodoWrite", summary: "TodoWrite(1/3)", detail: todos });
    await sleep(200);
    event({ sessionId: id, type: "tool_finished", toolCallId: "p1", tool: "TodoWrite", status: "done", output: "Todo list updated (1 of 3 done)." });

    const plan = "## План\n\n1. `src/Orders.cs`: переименовать `Calc` в `CalculateTotal`.\n2. `src/Checkout.cs`, `tests/OrdersTests.cs`: обновить вызовы (4 места).\n3. Запустить `dotnet test`.\n\nФайлы вне этих трёх не трогаю.";
    event({ sessionId: id, type: "tool_started", toolCallId: "p2", tool: "ExitPlanMode", summary: "ExitPlanMode", detail: plan });
    const permission: PermissionPrompt = {
      requestId: "r" + Date.now(),
      tool: "ExitPlanMode",
      summary: "ExitPlanMode",
      subject: ".",
      risk: "plan",
      reason: "plan approval",
      detail: plan,
    };
    event({ sessionId: id, type: "permission", permission });
    const decision = await new Promise<string>((resolve) => waiting.set(permission.requestId, resolve));
    event({ sessionId: id, type: "permission_resolved", requestId: permission.requestId });
    const denied = decision === "deny";
    event({ sessionId: id, type: "tool_finished", toolCallId: "p2", tool: "ExitPlanMode", status: denied ? "denied" : "done", output: denied ? "Permission denied (the user declined)." : "The user approved the plan." });
    if (!denied) {
      s.mode = decision === "always" ? "acceptEdits" : "default";
      event({ sessionId: id, type: "mode", text: s.mode });
    }
    for (const word of (denied ? "Хорошо, доработаю план." : "Приступаю к выполнению.").split(/(?<=\s)/)) {
      await sleep(25);
      event({ sessionId: id, type: "text", text: word });
    }
    s.summary.running = false;
    event({ sessionId: id, type: "turn_finished", reason: "end_turn", inputTokens: 900, outputTokens: 120 });
    notify(Notifications.SessionsChanged, {});
  }

  async function runTurn(id: string, text: string) {
    const s = sessions.get(id)!;
    s.items.push({ kind: "user", text });
    if (s.summary.title === "Новая сессия") s.summary.title = text.slice(0, 60);
    s.summary.running = true;
    event({ sessionId: id, type: "turn_started" });
    notify(Notifications.SessionsChanged, {});

    if (text.toLowerCase().startsWith("план")) {
      await runPlan(id, s);
      return;
    }

    for (const chunk of ["Посмотрю, ", "как устроен ", "проект.\n\n"]) {
      await sleep(120);
      event({ sessionId: id, type: "text", text: chunk });
    }
    event({ sessionId: id, type: "tool_started", toolCallId: "t1", tool: "Glob", summary: "Glob(**/*.cs)" });
    await sleep(300);
    event({ sessionId: id, type: "tool_finished", toolCallId: "t1", tool: "Glob", status: "done", output: "src/Program.cs\nsrc/App.cs" });

    event({ sessionId: id, type: "tool_started", toolCallId: "t2", tool: "Edit", summary: "Edit(src/Program.cs)" });
    const diff = [
      "@@ -1,7 +1,8 @@",
      " using System;",
      " ",
      " namespace Demo;",
      " ",
      " internal static class Program",
      " {",
      "-    private static void Main() => Console.WriteLine(\"Hello, world\");",
      "+    private static void Main()",
      "+    {",
      "+        Console.WriteLine(\"Hello, Kairo\");",
      "+    }",
      " }",
    ].join("\n");
    const permission: PermissionPrompt = {
      requestId: "r" + Date.now(),
      tool: "Edit",
      summary: "Edit(src/Program.cs)",
      subject: "src/Program.cs",
      risk: "write",
      reason: "write needs approval",
      diff,
    };
    event({ sessionId: id, type: "permission", permission });
    const decision = await new Promise<string>((resolve) => waiting.set(permission.requestId, resolve));
    event({ sessionId: id, type: "permission_resolved", requestId: permission.requestId });
    const denied = decision === "deny";
    event({
      sessionId: id,
      type: "tool_finished",
      toolCallId: "t2",
      tool: "Edit",
      status: denied ? "denied" : "done",
      output: denied ? "Permission denied (the user declined)." : "Edited src/Program.cs (1 replacement).",
      diff: denied ? undefined : diff,
    });

    if (!denied) event({ sessionId: id, type: "checkpoint", checkpointId: "cp1", files: 2 });

    const answer = denied
      ? "Хорошо, файл не трогаю. Могу предложить изменение текстом."
      : "Готово. Изменил **src/Program.cs**:\n\n```csharp\nConsole.WriteLine(\"Hello, Kairo\");\n```\n\n| Файл | Изменение |\n|---|---|\n| `src/Program.cs` | строка 3 |";
    for (const word of answer.split(/(?<=\s)/)) {
      await sleep(25);
      event({ sessionId: id, type: "text", text: word });
    }
    s.summary.running = false;
    s.summary.updatedAt = new Date().toISOString();
    event({ sessionId: id, type: "turn_finished", reason: "end_turn", inputTokens: 1200, outputTokens: 90 });
    notify(Notifications.SessionsChanged, {});
  }

  async function runCompact(id: string, s: { summary: SessionSummary; items: TranscriptItem[]; mode: Mode }) {
    s.summary.running = true;
    event({ sessionId: id, type: "turn_started" });
    event({ sessionId: id, type: "compacting" });
    await sleep(900);
    const summary = "Запрос: заменить приветствие в Program.cs на «Hello, Kairo».\n\nФайлы: src/Program.cs — строка 3 изменена.\n\nТекущее состояние: правка сделана и проверена.\nСледующий шаг: ждать новой задачи.";
    s.items = [{ kind: "compaction", text: summary }];
    event({ sessionId: id, type: "compacted", text: summary, tokensBefore: 84000, tokensAfter: 1800 });
    s.summary.running = false;
    event({ sessionId: id, type: "turn_finished", reason: "compacted" });
    notify(Notifications.SessionsChanged, {});
  }

  // ---- A made-up swarm: a lead and two coders, one shared question, then a report ----

  const swarmDetails = new Map<string, SwarmDetail>();
  const pushSwarm = (d: SwarmDetail) => notify(Notifications.SwarmChanged, structuredClone(d));

  async function runSwarm(id: string) {
    const d = swarmDetails.get(id)!;
    let seq = 0;
    const log = (kind: SwarmLogLine["kind"], agent: string, text: string) => {
      d.log.push({ seq: ++seq, kind, agent, text });
      pushSwarm(d);
    };
    const task = (n: number, status: "pending" | "in_progress" | "done", result?: string) => {
      const t = d.tasks.find((x) => x.seq === n)!;
      t.status = status;
      if (result) t.result = result;
      d.summary.tokensUsed += 31_000;
      d.modelCalls += 3;
      pushSwarm(d);
    };
    const setAgent = (name: string, status: "idle" | "working") => {
      d.agents.find((a) => a.name === name)!.status = status;
    };

    setAgent("lead", "working");
    log("tool", "lead", "Glob(**/*.cs)");
    await sleep(500);
    for (const [n, title, description, deps] of [
      [1, "Тесты на парсер конфигурации", "Покрыть Parse и ошибки в tests/ConfigTests.cs", []],
      [2, "Описать формат в README", "Раздел «Конфигурация» с примером config.toml", []],
      [3, "Прогнать сборку и тесты", "dotnet build и dotnet test, исправить найденное", [1, 2]],
    ] as [number, string, string, number[]][]) {
      d.tasks.push({ seq: n, title, description, status: "pending", dependsOn: deps });
    }
    log("tool", "lead", "task_create ×3");
    for (const name of ["coder-1", "coder-2"]) {
      d.agents.push({ name, role: "coder", status: "idle", branch: `kairo/${id}/${name}`, workingDirectory: `C:\\Users\\dev\\.kairo\\swarms\\${id}\\${name}` });
      log("spawn", name, `${name} started on branch kairo/${id}/${name}`);
      await sleep(250);
    }
    d.tasks[0].assignee = "coder-1";
    d.tasks[1].assignee = "coder-2";
    setAgent("lead", "idle");
    log("said", "lead", "Задачи розданы, жду отчётов.");
    await sleep(500);

    setAgent("coder-1", "working");
    setAgent("coder-2", "working");
    task(1, "in_progress");
    task(2, "in_progress");
    log("tool", "coder-1", "Read(src/Config/Parser.cs)");
    log("tool", "coder-2", "Read(README.md)");
    await sleep(700);
    log("tool", "coder-1", "Shell(dotnet test --filter Config)");

    // Both workers want to run the same command: one question for the user.
    const prompts = ["coder-1", "coder-2"].map((who) => ({
      requestId: Math.random().toString(16).slice(2, 10),
      tool: "Shell",
      summary: `[${who}] Shell(dotnet test --filter Config)`,
      subject: "dotnet test --filter Config",
      risk: "exec" as const,
      reason: "exec needs approval",
    }));
    d.pendingPermissions = prompts;
    pushSwarm(d);
    await Promise.all(prompts.map((pr) => new Promise<string>((resolve) => waiting.set(pr.requestId, resolve))));
    d.pendingPermissions = [];
    pushSwarm(d);

    log("tool", "coder-2", "Edit(README.md)");
    await sleep(600);
    d.messages.push({ id: 1, from: "coder-1", to: "lead", text: "Парсер падает на пустом файле: добавил проверку и тест.", at: new Date().toISOString() });
    task(2, "done", "Добавил раздел «Конфигурация» с примером.");
    setAgent("coder-2", "idle");
    log("said", "coder-2", "README готов.");
    await sleep(600);
    task(1, "done", "5 тестов, все проходят; исправил падение на пустом файле.");
    setAgent("coder-1", "idle");
    log("said", "coder-1", "Тесты готовы.");
    await sleep(400);

    setAgent("lead", "working");
    log("tool", "lead", "merge_agent(coder-1 → kairo)");
    log("tool", "lead", "merge_agent(coder-2 → kairo)");
    await sleep(600);
    d.tasks[2].assignee = "coder-1";
    setAgent("coder-1", "working");
    task(3, "in_progress");
    await sleep(700);
    task(3, "done", "Сборка и 41 тест проходят.");
    setAgent("coder-1", "idle");
    setAgent("lead", "idle");
    d.summary.status = "done";
    d.summary.running = false;
    d.report = "Добавлены тесты на парсер конфигурации (5 штук) и раздел «Конфигурация» в README. Исправлено падение на пустом файле. Ветки coder-1 и coder-2 слиты в основную папку; сборка и тесты проходят.";
    log("end", "lead", "the lead finished the swarm");
  }

  function handle(m: RpcMessage): unknown {
    const p = (m.params ?? {}) as Record<string, string>;
    switch (m.method) {
      case Methods.AppInfo:
        return { name: "Kairo", version: "dev", runtime: "browser", os: navigator.platform, host: "mock" };
      case Methods.SettingsGet:
        return settings;
      case Methods.SettingsUpdate:
        Object.assign(settings, Object.fromEntries(Object.entries(p).filter(([, v]) => v !== undefined)));
        return settings;
      case Methods.SecretSet:
        if (p.provider === "brave") {
          settings.braveKey = p.key.length > 0;
          return settings;
        }
        if (p.provider === "jev") {
          settings.jevKey = p.key.length > 0;
          return settings;
        }
        settings.providers = settings.providers.map((x) => (x.id === p.provider ? { ...x, hasKey: p.key.length > 0 } : x));
        settings.ready = settings.providers.some((x) => x.id === settings.provider && x.hasKey);
        return settings;
      case Methods.SandboxReadAdd:
        settings.sandboxRead = [...(settings.sandboxRead ?? []), "C:/Users/dev/AppData/Local/Programs/Python/Python312"];
        return settings;
      case Methods.SandboxReadRemove:
        settings.sandboxRead = (settings.sandboxRead ?? []).filter((x) => x !== p.path);
        return settings;
      case Methods.WorkspacePick:
        return { workspace: workspaces[0] };
      case Methods.WorkspaceList:
        return { workspaces };
      case Methods.SessionList:
        return { sessions: [...sessions.values()].map((s) => s.summary).reverse() };
      case Methods.SessionCreate: {
        const id = Math.random().toString(16).slice(2, 14);
        const summary = { id, title: "Новая сессия", workingDirectory: p.workingDirectory, updatedAt: new Date().toISOString(), running: false };
        sessions.set(id, { summary, items: [], mode: settings.mode });
        notify(Notifications.SessionsChanged, {});
        return summary;
      }
      case Methods.SessionOpen: {
        const s = sessions.get(p.sessionId)!;
        return { session: s.summary, items: s.items, mode: s.mode, model: settings.model, pendingPermissions: [] };
      }
      case Methods.SessionSend:
        if (p.text.trim() === "/help") {
          event({ sessionId: p.sessionId, type: "notice", text: "Команды:\n  /help — список команд и скиллов\n  /compact — сжать контекст сессии\n  /remember — записать заметку в память Kairo\nСкиллы (вызываются по имени или сами):\n  review — проверка изменений перед коммитом" });
          return { started: false };
        }
        void runTurn(p.sessionId, p.text);
        return { started: true };
      case Methods.SessionCompact:
        void runCompact(p.sessionId, sessions.get(p.sessionId)!);
        return { success: true };
      case Methods.SessionSetMode:
        sessions.get(p.sessionId)!.mode = p.mode as Mode;
        return { success: true };
      case Methods.SessionCancel:
        return { success: true };
      case Methods.PermissionRespond:
        waiting.get(p.requestId)?.(p.decision);
        waiting.delete(p.requestId);
        return { success: true };
      case Methods.CheckpointPreview:
        return {
          checkpointId: p.checkpointId,
          files: [
            {
              path: "C:\\Users\\dev\\projects\\kairo\\src\\Program.cs",
              displayPath: "src/Program.cs",
              action: "restore",
              diff: "@@ -5,6 +5,4 @@\n internal static class Program\n {\n-    private static void Main()\n-    {\n-        Console.WriteLine(\"Hello, Kairo\");\n-    }\n+    private static void Main() => Console.WriteLine(\"Hello, world\");\n }",
            },
            { path: "C:\\Users\\dev\\projects\\kairo\\notes.md", displayPath: "notes.md", action: "delete", diff: "@@ -1,2 +0,0 @@\n-# Заметки\n-Черновик" },
            { path: "C:\\Users\\dev\\projects\\kairo\\config.json", displayPath: "config.json", action: "conflict", diff: "@@ -1,1 +1,1 @@\n-{\"debug\": true}\n+{\"debug\": false}" },
          ],
        };
      case Methods.CheckpointRollback:
        return { restored: 1, deleted: 1, skipped: [] };
      case Methods.UpdateGet:
      case Methods.UpdateCheck:
        return updateStatus;
      case Methods.UpdateInstall:
        void runUpdate();
        return { success: true };
      case Methods.TerminalOpen: {
        const terminalId = "term" + Math.random().toString(16).slice(2, 10);
        shells.set(terminalId, "");
        setTimeout(() => shellOut(terminalId, "\x1b[2mPowerShell (макет: команды не выполняются в браузере)\x1b[0m\r\n\r\n" + PROMPT), 40);
        return { terminalId, title: `PowerShell · ${p.workingDirectory.split("\\").pop()}` };
      }
      case Methods.TerminalInput: {
        let line = shells.get(p.terminalId) ?? "";
        for (const ch of p.data) {
          if (ch === "\r") {
            shellOut(p.terminalId, "\r\n" + (line.trim() ? `\x1b[31mмакет: «${line.trim()}» не выполнена\x1b[0m\r\n` : "") + PROMPT);
            line = "";
          } else if (ch === "\x7f") {
            if (line) shellOut(p.terminalId, "\b \b");
            line = line.slice(0, -1);
          } else {
            shellOut(p.terminalId, ch);
            line += ch;
          }
        }
        shells.set(p.terminalId, line);
        return { success: true };
      }
      case Methods.TerminalResize:
        return { success: true };
      case Methods.TerminalClose:
        shells.delete(p.terminalId);
        return { success: true };
      case Methods.SwarmList:
        return { swarms: [...swarmDetails.values()].map((d) => d.summary).reverse() };
      case Methods.SwarmGet:
        return swarmDetails.get(p.swarmId)!;
      case Methods.SwarmStart: {
        const params = m.params as { workingDirectory: string; goal: string; maxTokens?: number };
        const id = Math.random().toString(16).slice(2, 10);
        const detail: SwarmDetail = {
          summary: { id, goal: params.goal, status: "running", workingDirectory: params.workingDirectory, tokensUsed: 0, maxTokens: params.maxTokens ?? 3_000_000, updatedAt: new Date().toISOString(), running: true },
          maxAgents: 4, maxMinutes: 120, maxConcurrency: 3, modelCalls: 0,
          agents: [{ name: "lead", role: "lead", status: "idle", workingDirectory: params.workingDirectory }],
          tasks: [], messages: [], log: [], pendingPermissions: [],
        };
        swarmDetails.set(id, detail);
        void runSwarm(id);
        return detail.summary;
      }
      case Methods.SwarmStop:
      case Methods.SwarmResume:
        return p.swarmId ? swarmDetails.get(p.swarmId) ?? { success: true } : { success: true };
      case Methods.McpList:
      case Methods.McpReload:
        return mcpList();
      case Methods.McpAdd: {
        if (mcpServers.some((s) => s.name === p.name)) throw new Error(`Сервер «${p.name}» уже есть. Удалите его или выберите другое имя.`);
        mcpServers.push({ name: p.name, transport: p.url ? "http" : "stdio", status: "ready", tools: ["example_tool"] });
        return mcpList();
      }
      case Methods.McpRemove:
        mcpServers.splice(mcpServers.findIndex((s) => s.name === p.name), 1);
        return mcpList();
      case Methods.McpSetEnabled: {
        const server = mcpServers.find((s) => s.name === p.name)!;
        server.status = (p as unknown as { enabled: boolean }).enabled ? "ready" : "disabled";
        return mcpList();
      }
      case Methods.McpImportDesktop:
        return mcpList([]);
      default:
        throw new Error(`Method '${m.method}' not found.`);
    }
  }

  return (m) => {
    queueMicrotask(() => {
      try {
        reply({ jsonrpc: "2.0", id: m.id, result: handle(m) });
      } catch (e) {
        reply({ jsonrpc: "2.0", id: m.id, error: { code: -32601, message: (e as Error).message } });
      }
    });
  };
}
