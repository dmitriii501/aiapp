using System.Diagnostics;
using System.Text.Json;
using Kairo.Core.Decisions;
using Kairo.Core.Models;
using Kairo.Core.Tools;

namespace Kairo.PcControl;

/// <summary>
/// A quick yes/no from the fast model: does the window show what the big model expects. About a third of a second and a few dozen
/// tokens, where reading the window and thinking about it costs seconds. It looks at the element list only; what it cannot see (a
/// picture, a game) needs PcScreenshot.
/// </summary>
internal sealed class PcCheckTool(PcSession session) : ITool
{
    /// <summary>Below this the list is taken not to show what the criterion is about.</summary>
    public const double DecidableBelow = 0.4;

    public ToolSpec Spec { get; } = new(
        "PcCheck",
        "Asks the fast decision model whether a window shows what you expect right now, and returns a probability in about a third of a " +
        "second. Use it to confirm that a step worked or that the task is finished instead of reading the whole window yourself: " +
        "criterion is how the screen looks when it is so (for example 'The title bar shows report.txt' or 'A Save As dialog is open'). " +
        "0.9 or more is reliable; under 0.9 look yourself (PcObserve or PcScreenshot). It sees the element list, not pictures. Needs a Jev key.",
        ToolInput.Schema(
            ("criterion", "string", "How the screen looks when the thing is true.", true),
            ("window", "string", "Window id or program name. Default: the allowed window in front.", false)));

    public ToolAssessment Assess(JsonElement input, ToolContext ctx)
    {
        var window = session.Resolve(ToolInput.OptionalString(input, "window"));
        ToolInput.RequiredString(input, "criterion");
        return session.Assess("PcCheck", window.Process, $"{window.Process}: {window.Title}");
    }

    public async Task<ToolResult> InvokeAsync(JsonElement input, ToolContext ctx, CancellationToken ct)
    {
        var window = session.Resolve(ToolInput.OptionalString(input, "window"));
        var criterion = ToolInput.RequiredString(input, "criterion").Trim();
        if (session.DecisionModel() is not { } model)
            return ToolResult.Error("The fast decision model is not set up (no Jev key). Look with PcObserve instead.");

        var clock = Stopwatch.StartNew();
        var look = session.Desktop.Observe(window.Handle);
        var state = JsonSerializer.Serialize(new Dictionary<string, string?>
        {
            ["success_criterion"] = criterion,
            ["app"] = look.Window.Process,
            ["window_title"] = look.Window.Title,
            ["screen"] = look.ToText(),
        }, PcJson.Default.DictionaryStringString);

        double probability;
        double decidable = 1;
        try
        {
            // Two questions in one request: is it so, and can this list say.
            var request = new DecisionRequest(DecisionState.Json(state), new Dictionary<string, DecisionQuestion>
            {
                ["achieved"] = StepQuestions.Achieved(),
                [StepQuestions.DecidableId] = StepQuestions.Decidable(),
            });
            var answer = await model.DecideAsync(request, ct).ConfigureAwait(false);
            probability = answer.YesNo("achieved").Probability;
            if (answer.Answers.TryGetValue(StepQuestions.DecidableId, out var d) && d is YesNoAnswer decidableAnswer)
                decidable = decidableAnswer.Probability;
        }
        catch (DecisionException ex)
        {
            return ToolResult.Error($"The fast model could not answer ({ex.Message}). Look yourself with PcObserve.");
        }

        var p = probability.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
        // A "no" about something the element list cannot show is not a "no": the model must not act on it (resend, retry) as if it were.
        if (probability < 0.9 && decidable < DecidableBelow)
        {
            var q = decidable.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
            return new ToolResult($"cannot tell (it is so: {p}, the list can decide: {q}) — the {look.Elements.Count} elements of {look.Window.Process} do not show what \"{criterion}\" is about. " +
                                  "This is NOT \"no\": do not repeat the action because of it. Look with PcScreenshot (or PcObserve with a query).");
        }
        var verdict = probability >= 0.9 ? "yes" : probability <= 0.1 ? "no" : "not clear";
        var advice = probability is > 0.1 and < 0.9 ? " Not sure enough: look yourself (PcObserve, or PcScreenshot for what the list does not show)." : "";
        return new ToolResult($"{verdict} ({p}) — \"{criterion}\" in {look.Window.Process} \"{look.Window.Title}\", {look.Elements.Count} elements, {clock.ElapsedMilliseconds} ms.{advice}");
    }
}
