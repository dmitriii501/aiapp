# Kairo

Open-source AI super-app for Windows: coding agent (Claude Code / Codex / Cursor class), PC control, MCP, agent swarm, agent catalog. BYOK first. Status: M1 (agent loop in the CLI). Design docs live in `docs/` (Russian for now; translated before the public release).

## Layout

- `Kairo.Core` — provider-neutral messages and `IModelProvider` (`Models/`), the agent loop and context compaction (`Agents/`), checkpoints and rollback of Write/Edit changes (`Checkpoints/`), permissions, `ITool`, system prompt + `AGENTS.md` discovery (`Context/`), SQLite sessions, `config.toml`.
- `Kairo.Providers` — Anthropic (official SDK) and OpenAI-compatible (raw HTTP + SSE) adapters.
- `Kairo.Tools` — Read, Write, Edit, Glob, Grep, Shell, TodoWrite, ExitPlanMode (`ToolAssessment.Detail` carries what the UI shows for the last two).
- `Kairo.Mcp` — MCP client (official SDK, `ModelContextProtocol.Core`): `McpConfigStore` (`%APPDATA%\Kairo\mcp.json`, Claude Desktop format), `McpManager` (connections, `mcp__server__tool` tools, always `exec` risk). Project-level `.mcp.json` is deliberately not read.
- `Kairo.Host` — runs sessions for a UI: background turns, `AgentEventDto` streaming, pending permissions, settings (`config.toml` + secret store), `ProviderFactory` (shared with the CLI). `HostMethods` is the complete list of methods the UI can call.
- `Kairo.Windows` — Credential Manager (`CredentialStore`; keys `Kairo/<provider>`, env vars override) and ConPTY (`ConPtyFactory`, shells in a kill-on-close job object). `TerminalService` in `Kairo.Host` is the only protocol path where UI input reaches a shell: user typing only, host-chosen program and folder (docs/SECURITY-MODEL.md → Терминал).
- `Kairo.Host` also owns `UpdateService` (checks and installs updates only on the user's request, never while an agent works); the Velopack part is `src/Kairo.Desktop/VelopackUpdater.cs` and the desktop has its own `Program.cs` entry point (Velopack must run first). The update feed never comes from a project config. See docs/RELEASING.md.
- `Kairo.Cli` — `kairo` (interactive, `-p`, `-c`, `-r`, `sessions`).
- `Kairo.Protocol` + `Kairo.Desktop` + `ui/` — desktop shell, JSON-RPC bridge, React UI (`ui/src/store.ts` holds state and handles agent events; `ui/src/bridge/mock.ts` stands in for the core in a plain browser).
- Tests use `ScriptedProvider` (scripted model) and `FakeHttpHandler` (canned SSE) — no network.

## UI

- Interaction model follows Claude Code; look follows a classic code editor (ADR-0004). Solid colors, 1px borders, 22px rows, Segoe UI + Cascadia Mono, one accent, no decorative animation. Tokens live at the top of `ui/src/styles.css`.
- Russian copy, short and concrete. No marketing phrasing.
- Visual checks without a model: `npm --prefix ui run build`, then headless Edge on `ui/dist/index.html#demo` (permission card), `#demo-done` (finished answer) or `#settings`, with `--allow-file-access-from-files --virtual-time-budget=4000 --screenshot=...`. The demo hashes only work with the mock bridge.

## Stack

- **Core** — C# / .NET 10, `src/Kairo.*`. Libraries are `IsAotCompatible`: no reflection, System.Text.Json source generators only.
- **Desktop shell** — WinUI 3 (`src/Kairo.Desktop`, unpackaged, self-contained Windows App SDK) hosting one WebView2 under a native title bar.
- **UI** — React + TypeScript + Vite in `ui/` (react-markdown, zustand, lucide-react).
- **Bridge** — JSON-RPC 2.0 over WebView2 web messages. Types live in `src/Kairo.Protocol`; `ui/src/bridge/protocol.ts` mirrors them by hand for now.

Architecture: `docs/ARCHITECTURE.md`. Decisions: `docs/adr/`. Unresolved questions: `docs/OPEN-QUESTIONS.md`.

## Commands

```
dotnet build Kairo.slnx            # everything; Desktop also builds ui/ when its sources change
dotnet test Kairo.slnx
dotnet run --project src/Kairo.Cli                  # interactive agent; needs ANTHROPIC_API_KEY
dotnet run --project src/Kairo.Cli -- -p "task"     # one-shot; approvals are denied unless --allow'ed
npm --prefix ui run dev            # UI alone in a browser, with a mock bridge
```

Desktop exe: `src/Kairo.Desktop/bin/x64/Debug/net10.0-windows10.0.26100.0/win-x64/Kairo.exe`. Set `KAIRO_UI_DEV_URL=http://localhost:5173` to load the Vite dev server inside the shell; `-p:KairoSkipUi=true` skips the UI build.

## Rules

- **The UI has no authority.** Protocol methods express intent (`permission.respond`), never commands (`shell.run`). New methods go in `Kairo.Protocol.Methods`, get registered explicitly on `RpcDispatcher`, and are mirrored in `protocol.ts`. No `AddHostObjectToScript`.
- **Security first.** Any new capability that lets the agent touch the system declares its risk level (`docs/SECURITY-MODEL.md`).
- **Agent loop invariants:** history is append-only (Claude rejects edited thinking history); every tool call gets exactly one result, including on denial and cancellation; tool input that failed strict JSON parsing (`InvalidToolInput`) or came from a `max_tokens`/`refusal` response is never executed.
- New tools implement `ITool` (`Assess` classifies risk and the subject that permission rules match; `InvokeAsync` runs it) and are added to `BuiltinTools.All()`.
- **Providers** go through `IModelProvider`. Anthropic uses the official `Anthropic` SDK (not trim/AOT safe — see ADR-0001); other vendors use raw HTTP + `SseReader`. Default Claude model `claude-opus-5-5`.
- Code and comments in English; Conventional Commits (`feat:`, `fix:`, `docs:` …).
