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

    /// <summary>Called with the number of the look about to be returned, before the elements are read: lets a test change the screen "in time".</summary>
    public Action<int>? OnObserve { get; set; }

    public Observation Observe(long handle)
    {
        OnObserve?.Invoke(_version + 1);
        return new(++_version, Window, [.. Elements], false);
    }

    public ActionOutcome Perform(long handle, DesktopAction action)
    {
        Performed.Add(action);
        // Like the real desktop: a click names the latest screenshot; an element id works while the element is still on the screen.
        if (action.Kind is DesktopActionKind.ClickAt or DesktopActionKind.DoubleClickAt)
        {
            if (action.ObservationVersion != _shotId)
                return ActionOutcome.Fail("that is not the latest screenshot of this window; take a new one with PcScreenshot");
        }
        else if (action.Kind is not (DesktopActionKind.Hotkey or DesktopActionKind.TypeFocused) && Elements.All(e => e.Id != action.ElementId))
            return ActionOutcome.Fail($"there is no element {action.ElementId} on the screen any more (it went, moved or was renamed); observe again");
        return OnPerform?.Invoke(action) ?? ActionOutcome.Success();
    }

    public string? LaunchRefusalText { get; set; }

    public string? LaunchRefusal(string name) => LaunchRefusalText;

    public WindowInfo? Launch(string application, TimeSpan wait) => Window;

    private int _shotId;
    public int Captures => _shotId;

    /// <summary>The picture never changes (a click that did nothing).</summary>
    public bool StaticPicture { get; set; }

    // The picture differs after every action, as a real screen does when something happened.
    public Snapshot Capture(long handle) => new(++_shotId, new Screenshot([137, 80, 78, 71, 13, 10, 26, (byte)(10 + (StaticPicture ? 0 : Performed.Count))], 200, 100, 0.5, 10, 20));

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
