using System.Text;
using Kairo.Cli;
using Kairo.Core.Agents;
using Kairo.Core.Config;
using Kairo.Core.Context;
using Kairo.Core.Models;
using Kairo.Core.Permissions;
using Kairo.Core.Sessions;
using Kairo.Core.Tools;
using Kairo.Host;
using Kairo.Mcp;
using Kairo.Tools;
using Kairo.Windows;

Console.OutputEncoding = Encoding.UTF8;
Console.InputEncoding = Encoding.UTF8;

CliOptions options;
try
{
    options = CliOptions.Parse(args);
}
catch (ArgumentException ex)
{
    return Fail(ex.Message);
}

if (options.Help)
{
    Console.WriteLine(CliOptions.Usage);
    return 0;
}

var cwd = Path.GetFullPath(options.WorkingDirectory ?? Environment.CurrentDirectory);
var userConfigDir = KairoConfig.UserConfigDirectory;
using var store = new SessionStore(Environment.GetEnvironmentVariable("KAIRO_DATA_DB") ?? SessionStore.DefaultPath);

if (options.Command == "sessions")
{
    foreach (var s in store.List())
        Console.WriteLine($"{s.Id}  {s.UpdatedAt.LocalDateTime:yyyy-MM-dd HH:mm}  {s.Model,-20}  {s.Title}  [{s.WorkingDirectory}]");
    return 0;
}

KairoConfig config;
PermissionRules rules;
PermissionMode mode;
try
{
    config = KairoConfig.Load(cwd, userConfigDir);
    rules = config.BuildRules();
    rules.Allow.AddRange(options.Allow.Select(PermissionRule.Parse));
    mode = options.Mode is { } m ? KairoConfig.ParseMode(m) : config.Mode ?? PermissionMode.Default;
}
catch (FormatException ex)
{
    return Fail(ex.Message);
}

// Provider and model: flags > project config > user config > defaults. Keys: env var, then Credential Manager.
config.Provider = options.Provider ?? config.Provider;
config.Model = options.Model ?? config.Model;
if (options.BaseUrl is { } url)
{
    if (ProviderFactory.ProviderOf(config) == ProviderFactory.Anthropic)
        config.AnthropicBaseUrl = url;
    else
        config.BaseUrl = url;
}
IModelProvider provider;
try
{
    provider = ProviderFactory.Create(config, new CredentialStore());
}
catch (ProviderSetupException ex)
{
    return Fail(ex.Problem switch
    {
        ProviderProblem.MissingKey => "No API key. Set ANTHROPIC_API_KEY or add a key in the Kairo app settings.",
        ProviderProblem.MissingBaseUrl => "openai-compatible needs a base URL (--base-url or config.toml).",
        ProviderProblem.MissingModel => "openai-compatible needs a model (--model or config.toml).",
        _ => $"Unknown provider '{ex.ProviderId}'.",
    });
}
var model = ProviderFactory.ModelOf(config);

// Session: new, --continue, or --resume.
SessionInfo? session = null;
IReadOnlyList<ChatMessage> history = [];
if (options.Resume is { } resumeId)
{
    session = store.Get(resumeId);
    if (session is null)
        return Fail($"Session '{resumeId}' not found. Run 'kairo sessions'.");
}
else if (options.Continue)
{
    session = store.Latest(cwd);
    if (session is null)
        return Fail("No previous session in this directory.");
}
if (session is not null)
{
    cwd = session.WorkingDirectory;
    history = HistoryRepair.Repair(store.LoadMessages(session.Id));
}

var interactive = options.Prompt is null;
var ui = new ConsoleUi(interactive);

await using var mcp = new McpManager(new McpConfigStore(McpConfigStore.DefaultPath));
if (!options.NoMcp)
{
    await mcp.ReloadAsync();
    foreach (var failed in mcp.Servers.Where(s => s.Status == McpStatus.Failed))
        ConsoleUi.Write(ConsoleColor.Yellow, $"MCP «{failed.Name}»: {failed.Error}\n");
}
var instructions = SystemPrompt.FindInstructionFiles(cwd, userConfigDir);
var agent = new Agent(
    provider,
    new ToolRegistry(BuiltinTools.All().Concat(mcp.Tools)),
    new PermissionEngine(rules, mode),
    ui,
    new ToolContext(cwd),
    SystemPrompt.Build(cwd, instructions, DateTimeOffset.Now, mode),
    new AgentOptions(model, options.Effort ?? config.Effort, ContextWindow: config.EffectiveContextWindow, CompactAt: config.EffectiveCompactAt),
    Compaction.Effective(history));

// Persist every message; repaired messages that were not stored yet are saved too.
var persisted = session is null ? 0 : store.LoadMessages(session.Id).Count;
foreach (var extra in history.Skip(persisted))
    store.Append(session!.Id, extra);
agent.MessageAppended += message =>
{
    session ??= store.Create(cwd, model, Title(message.Text));
    store.Append(session.Id, message);
};

if (!interactive)
    return await RunTurnAsync(options.Prompt!, CancellationToken.None) ? 0 : 1;

// Interactive loop. Ctrl+C cancels the running turn; at the prompt it exits.
ConsoleUi.Write(ConsoleColor.Magenta, "Kairo");
ConsoleUi.Write(ConsoleColor.DarkGray, $"  {provider.Id} · {model} · mode {mode} · {cwd}\n");
if (instructions.Count > 0)
    ConsoleUi.Write(ConsoleColor.DarkGray, $"  instructions: {string.Join(", ", instructions.Select(i => Path.GetFileName(Path.GetDirectoryName(i.Path)) + "/" + Path.GetFileName(i.Path)))}\n");
if (session is not null)
    ConsoleUi.Write(ConsoleColor.DarkGray, $"  resumed session {session.Id} ({history.Count} messages)\n");
ConsoleUi.Write(ConsoleColor.DarkGray, "  /exit to quit, /session for the session id, /compact [focus] to free context, Ctrl+C to interrupt\n\n");

CancellationTokenSource? turn = null;
Console.CancelKeyPress += (_, e) =>
{
    if (turn is { IsCancellationRequested: false })
    {
        e.Cancel = true;
        turn.Cancel();
    }
};

while (true)
{
    ConsoleUi.Write(ConsoleColor.Magenta, "› ");
    var line = Console.ReadLine();
    if (line is null || line.Trim() is "/exit" or "/quit")
        break;
    if (line.Trim() == "/session")
    {
        Console.WriteLine(session?.Id ?? "(no messages yet)");
        continue;
    }
    if (string.IsNullOrWhiteSpace(line))
        continue;
    if (line.Trim() == "/compact" || line.TrimStart().StartsWith("/compact ", StringComparison.Ordinal))
    {
        turn = new CancellationTokenSource();
        await CompactAsync(line.Trim()["/compact".Length..].Trim(), turn.Token);
        turn.Dispose();
        turn = null;
        continue;
    }

    turn = new CancellationTokenSource();
    await RunTurnAsync(line, turn.Token);
    turn.Dispose();
    turn = null;
    Console.WriteLine();
}
return 0;

async Task CompactAsync(string focus, CancellationToken ct)
{
    if (Compaction.Effective(agent.History).Count < 2)
    {
        ConsoleUi.Write(ConsoleColor.DarkGray, "  nothing to compact yet\n");
        return;
    }
    try
    {
        ui.Render(new ContextCompacting());
        ui.Render(await agent.CompactAsync(focus.Length > 0 ? focus : null, ct));
    }
    catch (OperationCanceledException)
    {
        ConsoleUi.Write(ConsoleColor.Yellow, "\n[interrupted]\n");
    }
    catch (Exception ex) when (ex is ModelProviderException or HttpRequestException || ex.GetType().Namespace?.StartsWith("Anthropic", StringComparison.Ordinal) == true)
    {
        ConsoleUi.Write(ConsoleColor.Red, $"\nerror: {ex.Message}\n");
    }
}

async Task<bool> RunTurnAsync(string text, CancellationToken ct)
{
    try
    {
        await foreach (var ev in agent.RunAsync(text, ct: ct))
            ui.Render(ev);
        ui.PrintUsage(model);
        return true;
    }
    catch (OperationCanceledException)
    {
        ConsoleUi.Write(ConsoleColor.Yellow, "\n[interrupted]\n");
        return false;
    }
    catch (Exception ex) when (ex is ModelProviderException or HttpRequestException || ex.GetType().Namespace?.StartsWith("Anthropic", StringComparison.Ordinal) == true)
    {
        ConsoleUi.Write(ConsoleColor.Red, $"\nerror: {ex.Message}\n");
        return false;
    }
}

static string Title(string text)
{
    var t = text.ReplaceLineEndings(" ").Trim();
    return t.Length <= 60 ? t : t[..60] + "…";
}

static int Fail(string message)
{
    Console.Error.WriteLine($"error: {message}");
    return 1;
}
