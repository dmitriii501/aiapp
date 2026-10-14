using System.Text.Json;
using Kairo.Core.Config;
using Kairo.Core.Decisions;
using Kairo.Core.Tests.Fakes;
using Kairo.Core.Tools;
using Kairo.PcControl;
using static Kairo.Core.Tests.Fakes.FakeDesktop;
using static Kairo.Core.Tests.Fakes.ScriptedDecisionModel;

namespace Kairo.Core.Tests;

/// <summary>A decision model that answers when the test says so.</summary>
internal sealed class GatedModel(Func<DecisionRequest, int, DecisionResult> script) : IDecisionModel
{
    private readonly TaskCompletionSource _open = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _calls;

    public string Id => "gated";

    public int Calls => _calls;

    public void Open() => _open.TrySetResult();

    public async Task<DecisionResult> DecideAsync(DecisionRequest request, CancellationToken ct)
    {
        var n = Interlocked.Increment(ref _calls);
        await _open.Task.WaitAsync(ct).ConfigureAwait(false);
        return script(request, n);
    }
}

public sealed class PcRunsTests
{
    private readonly FakeDesktop _desktop = new();

    private static KairoConfig Config()
    {
        var dir = Directory.CreateTempSubdirectory("kairo-runs-").FullName;
        File.WriteAllText(Path.Combine(dir, "config.toml"), "[pc]\nenabled = true\napps = [\"notepad.exe\"]");
        return KairoConfig.Load(dir, dir, includeProject: false);
    }

    private IReadOnlyList<ITool> Tools(IDecisionModel? model) => PcTools.For(Config(), () => model, _desktop, null, new DecisionHistory());

    private static ITool Tool(IReadOnlyList<ITool> tools, string name) => tools.Single(t => t.Spec.Name == name);

    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);

    private ToolContext Ctx => new(Path.GetTempPath());

    private void Changing()
    {
        var n = 0;
        _desktop.Elements = [El("e1", "button", "Open", UiCapability.Invoke)];
        _desktop.OnPerform = _ =>
        {
            n++;
            _desktop.Elements = [El("e1", "button", $"Open {n}", UiCapability.Invoke)];
            return ActionOutcome.Success();
        };
    }

    private static object Pursue(bool background = true) => new { subgoal = "Open it", success_criterion = "It is open", background };

    // ---- PcCheck ----

    [Fact]
    public async Task A_check_returns_the_fast_models_probability_with_advice_when_it_is_unsure()
    {
        _desktop.Elements = [El("e1", "button", "Save", UiCapability.Invoke)];
        double answer = 0.95;
        var model = new ScriptedDecisionModel((_, _) => new DecisionResult("scripted", new Dictionary<string, DecisionAnswer> { ["achieved"] = new YesNoAnswer(answer) }, 10, TimeSpan.Zero));
        var check = Tool(Tools(model), "PcCheck");

        var sure = await check.InvokeAsync(Json(new { criterion = "A Save button is shown" }), Ctx, default);
        answer = 0.5;
        var unsure = await check.InvokeAsync(Json(new { criterion = "A Save button is shown" }), Ctx, default);
        answer = 0.03;
        var no = await check.InvokeAsync(Json(new { criterion = "A Save button is shown" }), Ctx, default);

        Assert.StartsWith("yes (0.95)", sure.Content);
        Assert.StartsWith("not clear (0.50)", unsure.Content);
        Assert.Contains("look yourself", unsure.Content);
        Assert.StartsWith("no (0.03)", no.Content);
        Assert.Contains("A Save button is shown", model.Requests[0].State.Value.GetProperty("success_criterion").GetString());
        Assert.Contains("[e1] button \"Save\"", model.Requests[0].State.Value.GetProperty("screen").GetString());
    }

    [Fact]
    public async Task A_check_without_a_fast_model_says_to_look_instead()
    {
        _desktop.Elements = [El("e1", "button", "Save", UiCapability.Invoke)];

        var result = await Tool(Tools(null), "PcCheck").InvokeAsync(Json(new { criterion = "x" }), Ctx, default);

        Assert.True(result.IsError);
        Assert.Contains("PcObserve", result.Content);
    }

    [Fact]
    public async Task A_check_is_not_refused_while_a_run_is_going_and_needs_no_confirmation_beyond_looking()
    {
        Changing();
        var gated = new GatedModel((req, _) => Pick(req, Key("done"), achieved: 0.95));
        var tools = Tools(gated);
        await Tool(tools, "PcPursue").InvokeAsync(Json(Pursue()), Ctx, default);

        var assess = Tool(tools, "PcCheck").Assess(Json(new { criterion = "x" }), Ctx);

        Assert.NotNull(assess);
        gated.Open();
        await Tool(tools, "PcRuns").InvokeAsync(Json(new { }), Ctx, default);
    }

    // ---- background runs ----

    [Fact]
    public async Task A_background_pursuit_returns_at_once_and_the_result_is_collected_later()
    {
        Changing();
        var gated = new GatedModel((req, n) => n == 1 ? Pick(req, Starts("invoke:")) : Pick(req, Key("done"), achieved: 0.95));
        var tools = Tools(gated);

        var queued = await Tool(tools, "PcPursue").InvokeAsync(Json(Pursue()), Ctx, default);

        Assert.False(queued.IsError, queued.Content);
        Assert.Contains("Queued as run r1", queued.Content);
        Assert.Empty(_desktop.Performed); // the model has not answered yet: the call did not wait for it

        gated.Open();
        var result = await Tool(tools, "PcRuns").InvokeAsync(Json(new { action = "await", timeout = 10 }), Ctx, default);

        Assert.Contains("r1 [done]", result.Content);
        Assert.Contains("Result: done", result.Content);
        Assert.Single(_desktop.Performed);
        var again = await Tool(tools, "PcRuns").InvokeAsync(Json(new { action = "status" }), Ctx, default);
        Assert.Contains("r1 [done]", again.Content);
    }

    [Fact]
    public async Task While_a_run_is_pending_tools_that_act_on_the_screen_are_refused_but_looking_is_not()
    {
        Changing();
        var gated = new GatedModel((req, _) => Pick(req, Key("done"), achieved: 0.95));
        var tools = Tools(gated);
        await Tool(tools, "PcPursue").InvokeAsync(Json(Pursue()), Ctx, default);

        var act = Assert.Throws<ToolInputException>(() => Tool(tools, "PcAct").Assess(Json(new { action = "hotkey", text = "ctrl+s" }), Ctx));
        var launch = Assert.Throws<ToolInputException>(() => Tool(tools, "PcLaunch").Assess(Json(new { application = "notepad.exe" }), Ctx));
        var foreground = Assert.Throws<ToolInputException>(() => Tool(tools, "PcPursue").Assess(Json(Pursue(background: false)), Ctx));

        Assert.All(new[] { act, launch, foreground }, e => Assert.Contains("PcRuns", e.Message));
        Assert.NotNull(Tool(tools, "PcObserve").Assess(Json(new { }), Ctx));
        Assert.NotNull(Tool(tools, "PcPursue").Assess(Json(Pursue()), Ctx)); // another background run may be queued

        gated.Open();
        await Tool(tools, "PcRuns").InvokeAsync(Json(new { timeout = 10 }), Ctx, default);
        Assert.NotNull(Tool(tools, "PcAct").Assess(Json(new { action = "hotkey", text = "ctrl+s" }), Ctx)); // idle again
    }

    [Fact]
    public async Task A_run_that_does_not_finish_cancels_the_ones_queued_behind_it()
    {
        Changing();
        var gated = new GatedModel((req, _) => Pick(req, Key("stuck")));
        var tools = Tools(gated);
        await Tool(tools, "PcPursue").InvokeAsync(Json(new { subgoal = "First", success_criterion = "x", background = true }), Ctx, default);
        await Tool(tools, "PcPursue").InvokeAsync(Json(new { subgoal = "Second", success_criterion = "y", background = true }), Ctx, default);
        gated.Open();

        var result = await Tool(tools, "PcRuns").InvokeAsync(Json(new { timeout = 10 }), Ctx, default);

        Assert.Contains("r1 [failed] First", result.Content);
        Assert.Contains("r2 [skipped] Second", result.Content);
        Assert.Contains("planned for the screen", result.Content);
        Assert.Equal(1, gated.Calls); // the second never asked the model anything
    }

    [Fact]
    public async Task Runs_go_one_after_another_in_the_order_they_were_queued()
    {
        Changing();
        var order = new List<string>();
        var model = new ScriptedDecisionModel((req, _) =>
        {
            order.Add(req.State.Value.GetProperty("subgoal").GetString()!);
            return Pick(req, Key("done"), achieved: 0.95);
        });
        var tools = Tools(model);
        foreach (var goal in new[] { "one", "two", "three" })
            await Tool(tools, "PcPursue").InvokeAsync(Json(new { subgoal = goal, success_criterion = "c", background = true }), Ctx, default);

        var result = await Tool(tools, "PcRuns").InvokeAsync(Json(new { timeout = 10 }), Ctx, default);

        Assert.Equal(["one", "two", "three"], order);
        Assert.Contains("r3 [done] three", result.Content);
    }

    [Fact]
    public async Task Cancelling_stops_the_going_run_and_the_queued_ones_and_frees_the_screen()
    {
        Changing();
        var gated = new GatedModel((req, _) => Pick(req, Key("done"), achieved: 0.95));
        var tools = Tools(gated);
        await Tool(tools, "PcPursue").InvokeAsync(Json(new { subgoal = "First", success_criterion = "x", background = true }), Ctx, default);
        await Tool(tools, "PcPursue").InvokeAsync(Json(new { subgoal = "Second", success_criterion = "y", background = true }), Ctx, default);

        var result = await Tool(tools, "PcRuns").InvokeAsync(Json(new { action = "cancel" }), Ctx, default);

        Assert.Contains("Cancelled 2 run(s)", result.Content);
        Assert.Contains("r1 [cancelled]", result.Content);
        Assert.Contains("r2 [cancelled]", result.Content);
        Assert.NotNull(Tool(tools, "PcAct").Assess(Json(new { action = "hotkey", text = "ctrl+s" }), Ctx));
    }

    [Fact]
    public async Task Ending_the_turn_cancels_the_runs_it_started()
    {
        Changing();
        var gated = new GatedModel((req, _) => Pick(req, Key("done"), achieved: 0.95));
        var tools = Tools(gated);
        using var turn = new CancellationTokenSource();
        await Tool(tools, "PcPursue").InvokeAsync(Json(Pursue()), Ctx, turn.Token);

        turn.Cancel();
        var result = await Tool(tools, "PcRuns").InvokeAsync(Json(new { timeout = 5 }), Ctx, default);

        Assert.Contains("r1 [cancelled]", result.Content);
    }

    [Fact]
    public async Task A_policy_can_run_in_the_background_too()
    {
        _desktop.Elements = [El("e1", "edit", "Text", UiCapability.SetValue)];
        var model = new ScriptedDecisionModel((req, n) => n == 1 ? Pick(req, Starts("type:")) : Pick(req, Key("done"), achieved: 0.95));
        var tools = Tools(model);
        var policy = new
        {
            goal = "Write hi",
            target = new { app = "notepad.exe" },
            texts = new { greeting = "hi" },
            steps = new[] { new { id = "write", goal = "Type the greeting", until = "The editor holds hi", texts = new[] { "greeting" } } },
        };

        var queued = await Tool(tools, "PcRunPolicy").InvokeAsync(Json(new { policy, background = true }), Ctx, default);
        var result = await Tool(tools, "PcRuns").InvokeAsync(Json(new { timeout = 10 }), Ctx, default);

        Assert.Contains("Queued as run r1", queued.Content);
        Assert.Contains("r1 [done] Write hi", result.Content);
    }

    [Fact]
    public async Task With_no_runs_the_tool_says_so()
    {
        var result = await Tool(Tools(null), "PcRuns").InvokeAsync(Json(new { }), Ctx, default);

        Assert.Contains("no background runs", result.Content);
    }

    [Fact]
    public async Task The_banner_stays_up_for_the_whole_queue_and_goes_when_it_is_empty()
    {
        var begun = 0;
        var ended = 0;
        var queue = new PcRunQueue(() =>
        {
            Interlocked.Increment(ref begun);
            return new Disposer(() => Interlocked.Increment(ref ended));
        });
        var gate = new TaskCompletionSource();
        queue.Enqueue("a", async _ => { await gate.Task; return (true, "a"); }, default);
        queue.Enqueue("b", _ => Task.FromResult((true, "b")), default);

        Assert.Equal(1, begun);
        Assert.Equal(0, ended);
        gate.SetResult();
        await queue.AwaitAsync(null, TimeSpan.FromSeconds(5), default);
        await Task.Delay(100);

        Assert.Equal(1, begun);
        Assert.Equal(1, ended);
        Assert.False(queue.Busy);
    }

    private sealed class Disposer(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}
