using System.Collections.Concurrent;
using Kairo.Core.Agents;
using Kairo.Core.Checkpoints;
using Kairo.Core.Config;
using Kairo.Core.Context;
using Kairo.Core.Models;
using Kairo.Core.Permissions;
using Kairo.Core.Rpc;
using Kairo.Core.Sessions;
using Kairo.Core.Tools;
using Kairo.Mcp;
using Kairo.Protocol;
using Kairo.Tools;

namespace Kairo.Host;

/// <summary>
/// Runs agent sessions for a UI: one turn at a time per session, in the background, streaming
/// <see cref="AgentEventDto"/>s and holding permission requests until the user answers.
/// Everything the UI asks for is validated here — the UI has no authority of its own.
/// </summary>
public sealed class AgentHost(
    SessionStore store,
    SettingsService settings,
    Func<AgentEventDto, Task> notify,
    Func<Task> sessionsChanged,
    Func<KairoConfig, IModelProvider>? createProvider = null,
    McpManager? mcp = null)
{
    public const string NewSessionTitle = "Новая сессия";

    private readonly Func<AgentEventDto, Task> _notify = notify;
    private readonly ConcurrentDictionary<string, LiveSession> _live = new();
    private readonly ConcurrentDictionary<string, PendingPermission> _pending = new();
    private readonly Func<KairoConfig, IModelProvider> _createProvider = createProvider ?? (c => ProviderFactory.Create(c, settings.Secrets));

    /// <summary>Built-in tools plus whatever MCP servers are connected right now.</summary>
    private ToolRegistry Tools() => new(BuiltinTools.All().Concat(mcp?.Tools ?? []));

    private sealed class LiveSession(SessionInfo info, List<ChatMessage> history, PermissionEngine permissions, ToolContext tools)
    {
        public SessionInfo Info { get; set; } = info;
        public List<ChatMessage> History { get; } = history;
        public PermissionEngine Permissions { get; } = permissions;
        public ToolContext Tools { get; } = tools;
        public CancellationTokenSource? Turn { get; set; }

        /// <summary>Told to the model with the next prompt: the user rolled back some of its file changes.</summary>
        public string? PendingNote { get; set; }
        public Lock Gate { get; } = new();
    }

    private sealed record PendingPermission(string SessionId, PermissionPrompt Prompt, TaskCompletionSource<PermissionAnswer> Answer);

    // ---- Sessions ----

    /// <summary>True while any session has a turn in progress (including one waiting for a permission).</summary>
    public bool AnyRunning() => _live.Values.Any(l => l.Turn is not null);

    public IReadOnlyList<SessionSummary> List() => store.List(100).Select(Summary).ToList();

    public SessionSummary Create(string workingDirectory)
    {
        if (!Directory.Exists(workingDirectory) || !store.IsKnownWorkspace(workingDirectory))
            throw new RpcException(RpcErrorCodes.InvalidParams, "Эта папка недоступна. Выберите её через «Новая сессия» → «Другая папка».");

        var config = LoadConfig(workingDirectory);
        var info = store.Create(workingDirectory, ProviderFactory.ModelOf(config), NewSessionTitle);
        _ = sessionsChanged();
        return Summary(info);
    }

    public SessionDetail Open(string sessionId)
    {
        var live = GetLive(sessionId);
        lock (live.Gate)
        {
            var config = LoadConfig(live.Info.WorkingDirectory);
            return new SessionDetail(
                Summary(live.Info),
                Transcript.Build(live.History, Tools(), live.Tools, store.Checkpoints.List(sessionId).ToDictionary(c => c.Turn)),
                SettingsService.ModeName(live.Permissions.Mode),
                ProviderFactory.ModelOf(config),
                _pending.Values.Where(p => p.SessionId == sessionId).Select(p => p.Prompt).ToList());
        }
    }

    public void SetMode(string sessionId, string mode) =>
        GetLive(sessionId).Permissions.Mode = SettingsService.ParseMode(mode);

    public void Send(string sessionId, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new RpcException(RpcErrorCodes.InvalidParams, "Пустое сообщение.");

        var live = GetLive(sessionId);
        var cwd = live.Info.WorkingDirectory;
        var config = LoadConfig(cwd);

        IModelProvider provider;
        try
        {
            provider = _createProvider(config);
        }
        catch (ProviderSetupException ex)
        {
            throw new RpcException(RpcErrorCodes.InvalidRequest, Describe(ex));
        }

        CancellationTokenSource turn;
        lock (live.Gate)
        {
            if (live.Turn is not null)
                throw new RpcException(RpcErrorCodes.InvalidRequest, "Агент ещё работает в этой сессии. Дождитесь ответа или остановите его.");
            turn = live.Turn = new CancellationTokenSource();
        }

        // Files the agent changes in this turn are saved first, so the user can roll them back.
        var turnNumber = live.History.Count(Transcript.IsPrompt) + 1;
        var recorder = new TurnRecorder(store.Checkpoints, sessionId, turnNumber);
        recorder.Recorded += c => _ = _notify(new AgentEventDto(sessionId, "checkpoint", CheckpointId: c.Id, Files: c.Files));
        live.Tools.History = recorder;
        var note = live.PendingNote;
        live.PendingNote = null;

        var agent = new Agent(
            provider,
            Tools(),
            live.Permissions,
            new Prompt(this, sessionId, live.Tools),
            live.Tools,
            SystemPrompt.Build(cwd, SystemPrompt.FindInstructionFiles(cwd, settings.UserConfigDirectory), DateTimeOffset.Now),
            new AgentOptions(ProviderFactory.ModelOf(config), config.Effort),
            live.History);

        agent.MessageAppended += message =>
        {
            lock (live.Gate)
                live.History.Add(message);
            store.Append(sessionId, message);
            if (live.Info.Title == NewSessionTitle && Transcript.IsPrompt(message) && message.UserTypedText is { Length: > 0 } first)
            {
                store.SetTitle(sessionId, TitleFrom(first));
                live.Info = live.Info with { Title = TitleFrom(first) };
            }
        };

        _ = Task.Run(() => RunTurnAsync(live, agent, text, note, turn, ProviderFactory.ProviderOf(config)));
    }

    public void Cancel(string sessionId)
    {
        if (_live.TryGetValue(sessionId, out var live))
            live.Turn?.Cancel();
    }

    public void RespondPermission(string requestId, string decision)
    {
        var answer = decision switch
        {
            "allow" => PermissionAnswer.Allow,
            "always" => PermissionAnswer.AllowAlways,
            "deny" => PermissionAnswer.Deny,
            _ => throw new RpcException(RpcErrorCodes.InvalidParams, $"Неизвестное решение «{decision}»."),
        };
        if (!_pending.TryGetValue(requestId, out var pending) || !pending.Answer.TrySetResult(answer))
            throw new RpcException(RpcErrorCodes.InvalidParams, "Запрос уже обработан.");
    }

    // ---- Checkpoints ----

    public CheckpointPreviewResult PreviewCheckpoint(string checkpointId)
    {
        var (checkpoint, live) = FindCheckpoint(checkpointId);
        var files = Rollback.Preview(store.Checkpoints, checkpoint)
            .Select(f => new CheckpointFileDto(f.Path, live.Tools.DisplayPath(f.Path), f.Action.ToString().ToLowerInvariant(), f.Diff))
            .ToList();
        return new CheckpointPreviewResult(checkpointId, files);
    }

    public CheckpointRollbackResult RollbackCheckpoint(string checkpointId, IReadOnlyList<string> paths, bool overwriteConflicts)
    {
        var (checkpoint, live) = FindCheckpoint(checkpointId);
        lock (live.Gate)
        {
            if (live.Turn is not null)
                throw new RpcException(RpcErrorCodes.InvalidRequest, "Агент ещё работает в этой сессии. Остановите его, затем откатывайте изменения.");
        }

        var result = Rollback.Apply(store.Checkpoints, checkpoint, paths, overwriteConflicts);

        // The model's earlier reads of these files are out of date now: make it read them again before editing.
        foreach (var path in paths)
            live.Tools.ReadFiles.Remove(Path.GetFullPath(path));
        if (result.Restored + result.Deleted > 0)
        {
            live.PendingNote = "The user rolled back some of your file changes: "
                + $"{result.Restored} file(s) restored to their earlier content and {result.Deleted} file(s) that you created were deleted "
                + $"({string.Join(", ", paths.Select(p => live.Tools.DisplayPath(Path.GetFullPath(p))))}). "
                + "Do not assume the contents you saw earlier. Read a file again before editing it.";
        }
        return new CheckpointRollbackResult(result.Restored, result.Deleted, result.Skipped.Select(p => live.Tools.DisplayPath(p)).ToList());
    }

    private (CheckpointInfo Checkpoint, LiveSession Live) FindCheckpoint(string checkpointId)
    {
        var checkpoint = store.Checkpoints.Get(checkpointId) ?? throw new RpcException(RpcErrorCodes.InvalidParams, "Точка отката не найдена.");
        return (checkpoint, GetLive(checkpoint.SessionId));
    }

    public bool IsRunning(string sessionId) => _live.TryGetValue(sessionId, out var live) && live.Turn is not null;

    // ---- Turn ----

    private async Task RunTurnAsync(LiveSession live, Agent agent, string text, string? note, CancellationTokenSource turn, string providerId)
    {
        var id = live.Info.Id;
        long input = 0, output = 0;
        var reason = "end_turn";

        await _notify(new AgentEventDto(id, "turn_started")).ConfigureAwait(false);
        await sessionsChanged().ConfigureAwait(false);
        try
        {
            await foreach (var ev in agent.RunAsync(text, note, turn.Token).ConfigureAwait(false))
            {
                switch (ev)
                {
                    case AgentTextDelta d:
                        await _notify(new AgentEventDto(id, "text", Text: d.Text)).ConfigureAwait(false);
                        break;
                    case ToolCallStarted s:
                        await _notify(new AgentEventDto(id, "tool_started", ToolCallId: s.Id, Tool: s.Tool, Summary: s.Summary)).ConfigureAwait(false);
                        break;
                    case ToolCallFinished f:
                        await _notify(new AgentEventDto(id, "tool_finished", ToolCallId: f.Id, Tool: f.Tool,
                            Output: Transcript.Clip(f.Result.Content),
                            Status: f.Denied ? "denied" : f.Result.IsError ? "error" : "done",
                            Diff: f.Result.Diff is { Length: > 0 } diff ? diff : null)).ConfigureAwait(false);
                        break;
                    case ModelCallCompleted m:
                        input += m.Usage.InputTokens ?? 0;
                        output += m.Usage.OutputTokens ?? 0;
                        break;
                    case AgentFinished f:
                        reason = f.Reason;
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            reason = "cancelled";
        }
        catch (Exception ex)
        {
            reason = "error";
            await _notify(new AgentEventDto(id, "error", Text: ErrorText.Describe(ex, providerId))).ConfigureAwait(false);
        }
        finally
        {
            lock (live.Gate)
                live.Turn = null;
            turn.Dispose();
        }

        await _notify(new AgentEventDto(id, "turn_finished", Reason: reason, InputTokens: input, OutputTokens: output)).ConfigureAwait(false);
        await sessionsChanged().ConfigureAwait(false);
    }

    private sealed class Prompt(AgentHost host, string sessionId, ToolContext ctx) : IPermissionPrompt
    {
        public async Task<PermissionAnswer> AskAsync(PermissionRequest request, CancellationToken ct)
        {
            var requestId = Guid.NewGuid().ToString("N");
            var subject = request.Call.Risk == RiskLevel.Exec ? request.Call.Subject : ctx.DisplayPath(request.Call.Subject);
            var prompt = new PermissionPrompt(requestId, request.Tool, request.Call.Summary, subject,
                request.Call.Risk.ToString().ToLowerInvariant(), request.Reason, request.Diff is { Length: > 0 } ? request.Diff : null);
            var pending = new PendingPermission(sessionId, prompt, new TaskCompletionSource<PermissionAnswer>(TaskCreationOptions.RunContinuationsAsynchronously));
            host._pending[requestId] = pending;

            try
            {
                await host._notify(new AgentEventDto(sessionId, "permission", Permission: prompt)).ConfigureAwait(false);
                using (ct.Register(() => pending.Answer.TrySetCanceled(ct)))
                    return await pending.Answer.Task.ConfigureAwait(false);
            }
            finally
            {
                host._pending.TryRemove(requestId, out _);
                await host._notify(new AgentEventDto(sessionId, "permission_resolved", RequestId: requestId)).ConfigureAwait(false);
            }
        }
    }

    // ---- Helpers ----

    private LiveSession GetLive(string sessionId) =>
        _live.GetOrAdd(sessionId, id =>
        {
            var info = store.Get(id) ?? throw new RpcException(RpcErrorCodes.InvalidParams, "Сессия не найдена.");
            var stored = store.LoadMessages(id);
            var history = HistoryRepair.Repair(stored);
            foreach (var extra in history.Skip(stored.Count))
                store.Append(id, extra);

            var config = LoadConfig(info.WorkingDirectory);
            var permissions = new PermissionEngine(config.BuildRules(), config.Mode ?? PermissionMode.Default);
            return new LiveSession(info, [.. history], permissions, new ToolContext(info.WorkingDirectory));
        });

    private KairoConfig LoadConfig(string workingDirectory)
    {
        try
        {
            return settings.LoadFor(workingDirectory);
        }
        catch (FormatException ex)
        {
            throw new RpcException(RpcErrorCodes.InvalidRequest, $"Ошибка в настройках: {ex.Message}");
        }
    }

    private SessionSummary Summary(SessionInfo s)
    {
        var title = _live.TryGetValue(s.Id, out var live) ? live.Info.Title : s.Title;
        return new SessionSummary(s.Id, title, s.WorkingDirectory, s.UpdatedAt.ToString("O"), IsRunning(s.Id));
    }

    internal static string TitleFrom(string text)
    {
        var t = text.ReplaceLineEndings(" ").Trim();
        return t.Length <= 60 ? t : t[..60].TrimEnd() + "…";
    }

    public static string Describe(ProviderSetupException ex) => ex.Problem switch
    {
        ProviderProblem.MissingKey => "Не задан API-ключ. Добавьте его в настройках.",
        ProviderProblem.MissingBaseUrl => "Не задан адрес API. Укажите его в настройках.",
        ProviderProblem.MissingModel => "Не указана модель. Укажите её в настройках.",
        _ => $"Неизвестный провайдер «{ex.ProviderId}».",
    };
}
