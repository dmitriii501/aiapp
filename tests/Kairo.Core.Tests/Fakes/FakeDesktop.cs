using Kairo.Core.Decisions;
using Kairo.PcControl;

namespace Kairo.Core.Tests.Fakes;

/// <summary>A desktop with one window whose elements the test edits; it records what was done to it.</summary>
internal sealed class FakeDesktop : IDesktop
{
    private int _version;

    public WindowInfo Window { get; set; } = new(0x10, 100, "notepad.exe", "Untitled - Notepad", true, false);
    public List<UiElement> Elements { get; set; } = [];
    public List<DesktopAction> Performed { get; } = [];
    public Func<DesktopAction, ActionOutcome>? OnPerform { get; set; }
    public bool Gone { get; set; }
    public bool Stop { get; set; }
    public int Observations => _version;

    public IReadOnlyList<WindowInfo> Windows() => Gone ? [] : [Window];

    public WindowInfo? Window_(long handle) => Gone || handle != Window.Handle ? null : Window;

    WindowInfo? IDesktop.Window(long handle) => Window_(handle);

    public Observation Observe(long handle) => new(++_version, Window, [.. Elements], false);

    public ActionOutcome Perform(long handle, DesktopAction action)
    {
        Performed.Add(action);
        // Like the real desktop: an element id from an older look is refused.
        if (action.Kind != DesktopActionKind.Hotkey && action.ObservationVersion != _version)
            return ActionOutcome.Fail($"the screen changed since it was read (look {action.ObservationVersion}, latest {_version}); observe again");
        return OnPerform?.Invoke(action) ?? ActionOutcome.Success();
    }

    public WindowInfo? Launch(string application, TimeSpan wait) => Window;

    public bool StopRequested() => Stop;

    public static UiElement El(string id, string role, string name, UiCapability can = UiCapability.None, string? value = null, bool enabled = true,
        bool password = false, bool readOnly = false, bool? isChecked = null, bool? expanded = null, int depth = 0) =>
        new(id, role, name, null, depth, can, enabled, false, password, readOnly, value, isChecked, expanded);
}

/// <summary>A decision model that answers from a script: each call gets the request and the number of the call.</summary>
internal sealed class ScriptedDecisionModel(Func<DecisionRequest, int, DecisionResult> script) : IDecisionModel
{
    public string Id => "scripted";
    public List<DecisionRequest> Requests { get; } = [];

    public Task<DecisionResult> DecideAsync(DecisionRequest request, CancellationToken ct)
    {
        Requests.Add(request);
        return Task.FromResult(script(request, Requests.Count));
    }

    /// <summary>An answer that picks the first option the predicate accepts (or the option with the exact key).</summary>
    public static DecisionResult Pick(DecisionRequest request, Func<ChoiceOption, bool> pick, double confidence = 0.95, double achieved = 0.05, double effect = 0.9)
    {
        var options = ((ChoiceQuestion)request.Questions["next"]).Options;
        var chosen = options.First(pick);
        var answers = new Dictionary<string, DecisionAnswer>
        {
            ["next"] = new ChoiceAnswer(chosen.Key, confidence, options.ToDictionary(o => o.Key, o => o == chosen ? confidence : 0.0)),
            ["achieved"] = new YesNoAnswer(achieved),
        };
        if (request.Questions.ContainsKey("effect"))
            answers["effect"] = new YesNoAnswer(effect);
        return new DecisionResult("scripted", answers, 100, TimeSpan.FromMilliseconds(5));
    }

    public static Func<ChoiceOption, bool> Key(string key) => o => o.Key == key;

    public static Func<ChoiceOption, bool> Starts(string prefix) => o => o.Key.StartsWith(prefix, StringComparison.Ordinal);
}
