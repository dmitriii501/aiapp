using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Kairo.Core.Agents;
using Kairo.Core.Config;
using Kairo.Core.Models;
using Kairo.Core.Secrets;
using Kairo.Core.Sessions;
using Kairo.Core.Tests.Fakes;
using Kairo.Host;
using Kairo.Protocol;
using static Kairo.Core.Tests.Fakes.ScriptedProvider;

namespace Kairo.Core.Tests;

public sealed class GoalTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly string _project;
    private readonly SessionStore _store;
    private readonly SettingsService _settings;
    private readonly ConcurrentQueue<AgentEventDto> _events = new();
    private readonly TimeSpan[] _savedDelays = AgentHost.GoalRetryDelays;
    private ScriptedProvider _model = new();

    public GoalTests()
    {
        _project = Path.Combine(_dir.Path, "project");
        Directory.CreateDirectory(_project);
        _store = new SessionStore(Path.Combine(_dir.Path, "data.db"));
        _settings = new SettingsService(Path.Combine(_dir.Path, "user"), new InMemorySecretStore());
        AgentHost.GoalRetryDelays = [TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(20)];
    }

    public void Dispose()
    {
        AgentHost.GoalRetryDelays = _savedDelays;
        _store.Dispose();
        _dir.Dispose();
    }

    private AgentHost CreateHost(Func<KairoConfig, IModelProvider>? provider = null) =>
        new(_store, _settings,
            notify: ev => { _events.Enqueue(ev); return Task.CompletedTask; },
            sessionsChanged: () => Task.CompletedTask,
            createProvider: provider ?? (_ => _model));

    private string NewSession(AgentHost host)
    {
        _store.TouchWorkspace(_project);
        return host.Create(_project).Id;
    }

    private async Task<string> Notice(string contains, int timeoutMs = 10_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (_events.FirstOrDefault(e => e.Type == "notice" && e.Text!.Contains(contains, StringComparison.Ordinal)) is { } found)
                return found.Text!;
            await Task.Delay(10);
        }
        throw new TimeoutException($"No notice with '{contains}'. Got: {string.Join(" | ", _events.Select(e => e.Type + ":" + (e.Text ?? e.Tool ?? e.Output ?? e.Status)))}");
    }

    private async Task Idle(AgentHost host, string id)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        await Task.Delay(50);
        while (host.IsRunning(id) && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        await Task.Delay(100); // a turn that was about to start next
        while (host.IsRunning(id) && DateTime.UtcNow < deadline)
            await Task.Delay(10);
    }

    /// <summary>A model whose first call fails, like a gateway that answers 529 once.</summary>
    private sealed class Flaky(IModelProvider inner, int failures) : IModelProvider
    {
        private int _calls;
        public string Id => inner.Id;

        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken ct = default) =>
            ++_calls <= failures ? Fail() : inner.StreamAsync(request, ct);

        private static async IAsyncEnumerable<ModelStreamEvent> Fail([EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            throw new HttpRequestException("529 overloaded");
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }
    }

    [Fact]
    public async Task A_goal_keeps_the_agent_going_until_it_says_it_is_done()
    {
        _model = new ScriptedProvider(
            Call("c1", "Glob", """{"pattern":"*.txt"}"""), Say("Нашёл, проверяю дальше."),     // turn 1 ends without the goal reached
            Call("c2", "GoalComplete", """{"evidence":"I listed the files and saw the result"}"""), Say("Готово."));
        var host = CreateHost();
        var id = NewSession(host);

        host.Send(id, "/goal найди текстовые файлы и проверь результат");
        var done = await Notice("Цель выполнена");
        await Idle(host, id);

        Assert.Contains("I listed the files", done);
        Assert.Equal(4, _model.Requests.Count); // turn 1: call + answer, turn 2: call + answer
        Assert.Equal(GoalStatus.Complete, _store.LoadGoal(id)!.Status);
        // The second turn was started by Kairo, not by the user.
        Assert.Contains(host.Open(id).Items, i => i.Kind == "user" && i.Text == "Цель ещё не выполнена — продолжай.");
        // Every request of an active goal carries its instructions.
        Assert.Contains(_model.Requests, r => r.Messages.Any(m => m.Parts.OfType<TextPart>().Any(p => p.Text.Contains("A goal is active"))));
    }

    [Fact]
    public async Task A_model_that_only_talks_twice_in_a_row_pauses_the_goal()
    {
        _model = new ScriptedProvider(Say("Думаю."), Say("Всё ещё думаю."), Say("(не должно быть вызвано)"));
        var host = CreateHost();
        var id = NewSession(host);

        host.Send(id, "/goal сделать невозможное");
        var paused = await Notice("Цель на паузе");
        await Idle(host, id);

        Assert.Contains("ничего не делал", paused);
        Assert.Equal(2, _model.Requests.Count);
        Assert.Equal(GoalStatus.Paused, _store.LoadGoal(id)!.Status);
    }

    [Fact]
    public async Task A_blocked_goal_tells_the_user_what_is_needed_and_stops()
    {
        _model = new ScriptedProvider(Call("c1", "GoalBlocked", """{"reason":"нужен пароль от сервера"}"""), Say("Жду."));
        var host = CreateHost();
        var id = NewSession(host);

        host.Send(id, "/goal выложить на сервер");
        var text = await Notice("нужна ваша помощь");
        await Idle(host, id);

        Assert.Contains("нужен пароль от сервера", text);
        Assert.Equal(2, _model.Requests.Count);
        Assert.Equal(GoalStatus.Blocked, _store.LoadGoal(id)!.Status);
    }

    [Fact]
    public async Task A_failed_turn_is_tried_again_and_the_goal_carries_on()
    {
        var inner = new ScriptedProvider(Call("c1", "GoalComplete", """{"evidence":"checked it myself, works"}"""), Say("Готово."));
        var flaky = new Flaky(inner, failures: 2);
        var host = CreateHost(_ => flaky);
        var id = NewSession(host);

        host.Send(id, "/goal сделать дело");
        await Notice("Цель выполнена");
        await Idle(host, id);

        Assert.Equal(2, _events.Count(e => e.Type == "error"));
        Assert.Equal(2, _events.Count(e => e.Type == "notice" && e.Text!.Contains("попробую снова")));
        Assert.Equal(GoalStatus.Complete, _store.LoadGoal(id)!.Status);
    }

    [Fact]
    public async Task Errors_that_do_not_stop_pause_the_goal_instead_of_looping_forever()
    {
        var host = CreateHost(_ => new Flaky(new ScriptedProvider(), failures: 100));
        var id = NewSession(host);

        host.Send(id, "/goal недостижимое");
        await Notice("не ответила");
        await Idle(host, id);

        Assert.Equal(GoalStatus.Paused, _store.LoadGoal(id)!.Status);
    }

    [Fact]
    public async Task Stopping_the_agent_pauses_the_goal_and_resume_continues_it()
    {
        // The model waits for a permission that nobody answers; the user stops the turn.
        _model = new ScriptedProvider(Call("c1", "Write", """{"file_path":"a.txt","content":"x"}"""), Say("..."));
        var host = CreateHost();
        var id = NewSession(host);
        host.SetMode(id, "default");
        host.Send(id, "/goal написать файл");
        while (!_events.Any(e => e.Type == "permission"))
            await Task.Delay(10);

        host.Cancel(id);
        await Notice("Цель на паузе");
        await Idle(host, id);

        Assert.Equal(GoalStatus.Paused, _store.LoadGoal(id)!.Status);

        _model = new ScriptedProvider(Call("c2", "GoalComplete", """{"evidence":"the file is there, I read it"}"""), Say("Готово."));
        host.Send(id, "/goal resume");
        await Notice("Цель выполнена");
        await Idle(host, id);
        Assert.Equal(GoalStatus.Complete, _store.LoadGoal(id)!.Status);
    }

    [Fact]
    public async Task A_stored_active_goal_comes_back_paused_after_a_restart_and_clear_drops_it()
    {
        var first = CreateHost();
        var id = NewSession(first);
        _store.SaveGoal(id, new GoalState("довести до конца"));

        var second = CreateHost(); // a fresh process: nothing is live yet
        second.Send(id, "/goal");
        var status = await Notice("на паузе");

        Assert.Contains("довести до конца", status);
        Assert.Contains("Kairo был перезапущен", status);

        second.Send(id, "/goal clear");
        await Notice("Цель снята");
        Assert.Null(_store.LoadGoal(id));
    }

    [Fact]
    public async Task A_goal_does_not_start_while_the_agent_works_and_the_tools_refuse_outside_a_goal()
    {
        _model = new ScriptedProvider(Call("c1", "GoalComplete", """{"evidence":"nothing to check, just testing"}"""), Say("ok"));
        var host = CreateHost();
        var id = NewSession(host);

        host.Send(id, "обычное сообщение");
        await Idle(host, id);

        // Without /goal the tool has nothing to end: it says so and the model carries on as usual.
        var result = host.Open(id).Items.FirstOrDefault(i => i.Kind == "tool");
        Assert.NotNull(result);
        Assert.Contains("no active goal", result!.Output ?? "");
    }

    [Fact]
    public async Task A_turn_is_not_cut_off_after_sixty_model_calls()
    {
        var script = new List<ModelResponse>();
        for (var i = 0; i < 70; i++)
            script.Add(Call($"c{i}", "Glob", """{"pattern":"*.nothing"}"""));
        script.Add(Say("Готово."));
        _model = new ScriptedProvider([.. script]);
        var host = CreateHost();
        var id = NewSession(host);

        host.Send(id, "много мелких шагов");
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!_events.Any(e => e.Type == "turn_finished") && DateTime.UtcNow < deadline)
            await Task.Delay(20);

        Assert.Equal("end_turn", _events.Last(e => e.Type == "turn_finished").Reason);
        Assert.Equal(71, _model.Requests.Count);
    }
}
