using Kairo.Core.Decisions;
using Kairo.Core.Tests.Fakes;
using Kairo.PcControl;
using static Kairo.Core.Tests.Fakes.FakeDesktop;
using static Kairo.Core.Tests.Fakes.ScriptedDecisionModel;

namespace Kairo.Core.Tests;

public sealed class PursueResilienceTests
{
    private readonly FakeDesktop _desktop = new();
    private readonly PcGuard _guard = new(["notepad.exe"]);

    private void Editor()
    {
        _desktop.Elements = [El("e1", "edit", "Text editor", UiCapability.SetValue)];
        _desktop.OnPerform = a =>
        {
            _desktop.Elements[0] = _desktop.Elements[0] with { Value = a.Text };
            return ActionOutcome.Success();
        };
    }

    private static readonly PursuePlan Plan = new("Write", "Text is there", new Dictionary<string, string> { ["t"] = "hello" });

    [Theory]
    [InlineData(DecisionFailure.Timeout)]
    [InlineData(DecisionFailure.Busy)]
    [InlineData(DecisionFailure.Unavailable)]
    public async Task One_lost_answer_is_asked_again_and_the_run_goes_on(DecisionFailure kind)
    {
        Editor();
        var calls = 0;
        var model = new ScriptedDecisionModel((req, _) =>
        {
            if (++calls == 1)
                throw new DecisionException("lost", kind);
            return calls == 2 ? Pick(req, Starts("type:")) : Pick(req, Key("done"), achieved: 0.99);
        });

        var report = await new PursueLoop(_desktop, _guard, model).RunAsync(0x10, Plan, default);

        Assert.Equal(PursueStatus.Done, report.Status);
        Assert.Equal(3, calls);
        Assert.Equal("hello", _desktop.Elements[0].Value);
    }

    [Fact]
    public async Task Two_lost_answers_in_a_row_hand_the_run_to_the_big_model()
    {
        Editor();
        var model = new ScriptedDecisionModel((_, _) => throw new DecisionException("down", DecisionFailure.Timeout));

        var report = await new PursueLoop(_desktop, _guard, model).RunAsync(0x10, Plan, default);

        Assert.Equal(PursueStatus.Escalated, report.Status);
        Assert.Equal(2, model.Requests.Count);
        Assert.Empty(_desktop.Performed);
    }

    [Theory]
    [InlineData(DecisionFailure.Unauthorized)]
    [InlineData(DecisionFailure.Invalid)]
    public async Task A_refused_key_or_a_bad_question_is_not_asked_twice(DecisionFailure kind)
    {
        Editor();
        var model = new ScriptedDecisionModel((_, _) => throw new DecisionException("no", kind));

        await new PursueLoop(_desktop, _guard, model).RunAsync(0x10, Plan, default);

        Assert.Single(model.Requests);
    }

    [Fact]
    public async Task Cancelling_while_the_model_thinks_ends_the_run_as_aborted()
    {
        Editor();
        using var cts = new CancellationTokenSource();
        var model = new CancellingModel(cts);

        var report = await new PursueLoop(_desktop, _guard, model).RunAsync(0x10, Plan, cts.Token);

        Assert.Equal(PursueStatus.Aborted, report.Status);
        Assert.Empty(_desktop.Performed);
    }

    private sealed class CancellingModel(CancellationTokenSource source) : IDecisionModel
    {
        public string Id => "cancelling";

        public Task<DecisionResult> DecideAsync(DecisionRequest request, CancellationToken ct)
        {
            source.Cancel();
            ct.ThrowIfCancellationRequested();
            throw new InvalidOperationException();
        }
    }
}
