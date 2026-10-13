using System.Collections.Concurrent;
using Kairo.Core.Agents;
using Kairo.Core.Config;
using Kairo.Core.Models;
using Kairo.Core.Permissions;
using Kairo.Core.Rpc;
using Kairo.Core.Sessions;
using Kairo.Core.Tools;
using Kairo.Mcp;
using Kairo.Protocol;
using Kairo.Swarm;
using Kairo.Tools;

namespace Kairo.Host;

/// <summary>
/// Runs swarms for the UI: starts, pauses and resumes them in the background, keeps a short log of what the agents do,
/// holds their permission questions until the user answers, and pushes the swarm's state when it changes.
/// The UI sends a goal and limits; the host decides the folder rules, the model, the permissions and everything else.
/// </summary>
public sealed class SwarmService(
    SwarmStore swarms,
    SessionStore sessions,
    SettingsService settings,
    Func<SwarmDetail, Task> notify,
    Func<KairoConfig, IModelProvider>? createProvider = null,
    McpManager? mcp = null)
{
    public const int MaxLogLines = 200;
    private static readonly TimeSpan PushDelay = TimeSpan.FromMilliseconds(120);

    private readonly Func<KairoConfig, IModelProvider> _createProvider = createProvider ?? (c => ProviderFactory.Create(c, settings.Secrets));
    private readonly ConcurrentDictionary<string, Live> _live = new();
    private readonly ConcurrentDictionary<string, LogBuffer> _logs = new();
    private readonly ConcurrentDictionary<string, Pending> _pending = new();

    /// <summary>What the agents of a swarm did lately. Kept for the life of the app, so a finished swarm still shows it.</summary>
    private sealed class LogBuffer
    {
        public List<SwarmLogDto> Lines { get; } = [];
        public long Seq;
    }

    private sealed class Live(CancellationTokenSource cts, LogBuffer log)
    {
        public CancellationTokenSource Cts { get; } = cts;
        public LogBuffer Log { get; } = log;
        public Task? Run { get; set; }
        public int PushPending;
    }

    private sealed record Pending(string SwarmId, PermissionPrompt Prompt, TaskCompletionSource<PermissionAnswer> Answer);

    public bool AnyRunning() => !_live.IsEmpty;

    public SwarmListResult List() => new(swarms.List(50).Select(Summary).ToList());

    public SwarmDetail Get(string swarmId)
    {
        var info = swarms.Get(swarmId) ?? throw new RpcException(RpcErrorCodes.InvalidParams, "Рой не найден.");
        List<SwarmLogDto> log = [];
        if (_logs.TryGetValue(swarmId, out var buffer))
        {
            lock (buffer)
                log = [.. buffer.Lines];
        }

        return new SwarmDetail(
            Summary(info),
            info.Limits.MaxAgents, info.Limits.MaxMinutes, info.Limits.MaxConcurrency, info.ModelCalls, info.Summary,
            swarms.Agents(swarmId).Select(a => new SwarmAgentDto(a.Name, a.Role, a.Status, a.Branch, a.WorkingDirectory)).ToList(),
            swarms.Tasks(swarmId).Select(t => new SwarmTaskDto(t.Seq, t.Title, t.Description, t.Status, t.Assignee, t.DependsOn, t.Result)).ToList(),
            swarms.Messages(swarmId, 100).Select(m => new SwarmMessageDto(m.Id, m.From, m.To, m.Text, m.CreatedAt.ToString("O"))).ToList(),
            log,
            _pending.Values.Where(p => p.SwarmId == swarmId).Select(p => p.Prompt).ToList());
    }

    public async Task<SwarmSummary> StartAsync(SwarmStartParams p, CancellationToken ct)
    {
        var goal = p.Goal.Trim();
        if (goal.Length == 0)
            throw new RpcException(RpcErrorCodes.InvalidParams, "Опишите цель роя.");
        if (goal.Length > 4000)
            throw new RpcException(RpcErrorCodes.InvalidParams, "Цель слишком длинная (не больше 4000 символов).");
        if (!Directory.Exists(p.WorkingDirectory) || !sessions.IsKnownWorkspace(p.WorkingDirectory))
            throw new RpcException(RpcErrorCodes.InvalidParams, "Эта папка недоступна. Выберите её через «Новая сессия» → «Другая папка».");

        var defaults = new SwarmLimits();
        var limits = defaults with
        {
            MaxAgents = p.Workers is { } w ? Math.Clamp(w, 1, 8) + 1 : defaults.MaxAgents,
            MaxTokens = p.MaxTokens is { } t ? Math.Max(t, 10_000) : defaults.MaxTokens,
            MaxMinutes = p.MaxMinutes is { } m ? Math.Clamp(m, 1, 720) : defaults.MaxMinutes,
        };

        var cwd = Path.GetFullPath(p.WorkingDirectory);
        if (await GitWorkspace.CheckRepositoryAsync(cwd, ct).ConfigureAwait(false) is { } problem)
            throw new RpcException(RpcErrorCodes.InvalidRequest, "Рой работает в git worktree: папка должна быть git-репозиторием хотя бы с одним коммитом.\n" + problem);

        var config = LoadConfig(cwd);
        var provider = CreateProvider(config);
        var info = swarms.Create(cwd, goal, limits);
        Launch(info.Id, config, provider);
        return Summary(swarms.Get(info.Id)!);
    }

    public SwarmDetail Resume(string swarmId, long? maxTokens)
    {
        var info = swarms.Get(swarmId) ?? throw new RpcException(RpcErrorCodes.InvalidParams, "Рой не найден.");
        if (_live.ContainsKey(swarmId))
            throw new RpcException(RpcErrorCodes.InvalidRequest, "Этот рой уже работает.");
        if (info.Status == SwarmStatus.Done)
            throw new RpcException(RpcErrorCodes.InvalidRequest, "Рой уже завершён.");

        var config = LoadConfig(info.WorkingDirectory);
        var provider = CreateProvider(config);
        if (maxTokens is { } tokens && tokens != info.Limits.MaxTokens)
            swarms.SetLimits(swarmId, info.Limits with { MaxTokens = Math.Max(tokens, 10_000) });
        Launch(swarmId, config, provider);
        return Get(swarmId);
    }

    public void Stop(string swarmId)
    {
        if (_live.TryGetValue(swarmId, out var live))
            live.Cts.Cancel();
    }

    /// <summary>Pauses every running swarm and waits a moment for them to save their state (the app is closing).</summary>
    public void StopAll()
    {
        var runs = new List<Task>();
        foreach (var live in _live.Values)
        {
            try { live.Cts.Cancel(); } catch (ObjectDisposedException) { }
            if (live.Run is { } run)
                runs.Add(run);
        }
        try { Task.WaitAll([.. runs], TimeSpan.FromSeconds(5)); } catch (AggregateException) { }
    }

    /// <summary>Answers a swarm's question. False when the request is not a swarm's (the caller then tries the sessions).</summary>
    public bool TryRespond(string requestId, PermissionAnswer answer)
    {
        if (!_pending.TryGetValue(requestId, out var pending))
            return false;
        if (!pending.Answer.TrySetResult(answer))
            throw new RpcException(RpcErrorCodes.InvalidParams, "Запрос уже обработан.");
        return true;
    }

    // ---- Running ----

    private void Launch(string swarmId, KairoConfig config, IModelProvider provider)
    {
        var live = new Live(new CancellationTokenSource(), _logs.GetOrAdd(swarmId, _ => new LogBuffer()));
        _live[swarmId] = live;

        var setup = new SwarmSetup(
            ProviderFactory.ModelOf(config),
            Path.Combine(settings.UserConfigDirectory, "swarms"),
            new Prompt(this, swarmId),
            config.BuildRules(),
            () => [.. BuiltinTools.All(settings.WebSearch, userConfigDirectory: settings.UserConfigDirectory), .. mcp?.Tools ?? []],
            config.Effort,
            config.EffectiveContextWindow,
            config.EffectiveCompactAt,
            WorkerMode: config.Mode == PermissionMode.Bypass ? PermissionMode.Bypass : PermissionMode.AcceptEdits,
            UserConfigDirectory: settings.UserConfigDirectory,
            Hooks: config.Hooks.Count > 0 ? new ProcessHooks(config.Hooks) : null,
            Log: e => OnLog(swarmId, live, e));

        var runner = new SwarmRunner(swarms, sessions, provider, setup);
        live.Run = Task.Run(async () =>
        {
            try
            {
                await runner.RunAsync(swarmId, live.Cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                swarms.SetStatus(swarmId, SwarmStatus.Failed, ex is SwarmException ? ex.Message : $"Рой остановился с ошибкой: {ex.Message}");
            }
            finally
            {
                _live.TryRemove(swarmId, out _);
                foreach (var p in _pending.Values.Where(p => p.SwarmId == swarmId))
                    p.Answer.TrySetCanceled();
                live.Cts.Dispose();
                await PushNowAsync(swarmId).ConfigureAwait(false);
            }
        });
        SchedulePush(swarmId, live);
    }

    private void OnLog(string swarmId, Live live, SwarmEvent e)
    {
        lock (live.Log)
        {
            live.Log.Lines.Add(new SwarmLogDto(++live.Log.Seq, e.Kind, e.Agent, e.Text.Length > 400 ? e.Text[..400] + "…" : e.Text));
            if (live.Log.Lines.Count > MaxLogLines)
                live.Log.Lines.RemoveRange(0, live.Log.Lines.Count - MaxLogLines);
        }
        SchedulePush(swarmId, live);
    }

    /// <summary>Pushes at most one update per short interval, so a busy swarm does not flood the UI.</summary>
    private void SchedulePush(string swarmId, Live live)
    {
        if (Interlocked.Exchange(ref live.PushPending, 1) != 0)
            return;
        _ = Task.Delay(PushDelay).ContinueWith(async _ =>
        {
            Volatile.Write(ref live.PushPending, 0);
            await PushNowAsync(swarmId).ConfigureAwait(false);
        }, TaskScheduler.Default);
    }

    private async Task PushNowAsync(string swarmId)
    {
        try
        {
            await notify(Get(swarmId)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is RpcException or InvalidOperationException or ObjectDisposedException)
        {
            // The window is closing or the swarm was removed: nothing to show.
        }
    }

    private sealed class Prompt(SwarmService service, string swarmId) : IPermissionPrompt
    {
        public async Task<PermissionAnswer> AskAsync(PermissionRequest request, CancellationToken ct)
        {
            var requestId = Guid.NewGuid().ToString("N");
            var prompt = new PermissionPrompt(requestId, request.Tool, request.Call.Summary, request.Call.Subject,
                request.Call.Risk.ToString().ToLowerInvariant(), request.Reason, request.Diff is { Length: > 0 } ? request.Diff : null);
            var pending = new Pending(swarmId, prompt, new TaskCompletionSource<PermissionAnswer>(TaskCreationOptions.RunContinuationsAsynchronously));
            service._pending[requestId] = pending;

            try
            {
                await service.PushNowAsync(swarmId).ConfigureAwait(false);
                using (ct.Register(() => pending.Answer.TrySetCanceled(ct)))
                    return await pending.Answer.Task.ConfigureAwait(false);
            }
            finally
            {
                service._pending.TryRemove(requestId, out _);
                await service.PushNowAsync(swarmId).ConfigureAwait(false);
            }
        }
    }

    // ---- Helpers ----

    private SwarmSummary Summary(SwarmInfo s) =>
        new(s.Id, s.Goal, s.Status, s.WorkingDirectory, s.TokensUsed, s.Limits.MaxTokens, s.UpdatedAt.ToString("O"), _live.ContainsKey(s.Id));

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

    private IModelProvider CreateProvider(KairoConfig config)
    {
        try
        {
            return _createProvider(config);
        }
        catch (ProviderSetupException ex)
        {
            throw new RpcException(RpcErrorCodes.InvalidRequest, AgentHost.Describe(ex));
        }
    }
}
