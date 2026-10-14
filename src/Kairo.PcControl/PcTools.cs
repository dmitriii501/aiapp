using System.Text;
using System.Text.Json;
using Kairo.Core.Config;
using Kairo.Core.Decisions;
using Kairo.Core.Models;
using Kairo.Core.Tools;

namespace Kairo.PcControl;

/// <summary>
/// The tools of the big model (System 2) for driving the PC. They are <see cref="RiskLevel.System"/>: they ask even in the
/// mode without confirmations, unless the user set <c>[pc] autonomous</c> (then they count as <see cref="RiskLevel.Exec"/>: the
/// mode without confirmations lets them through, the other modes still ask). Only applications the user listed under <c>[pc] apps</c>. The listing of windows is the one
/// exception (it shows the allowed applications only), so the model can find out what it may touch.
/// </summary>
public static class PcTools
{
    private static int _warmed;
    private static DecisionHistory? _history;

    /// <summary>The decision log in the user's config folder, shared by every run in this process; null when the user turned calibration off.</summary>
    public static DecisionHistory? SharedHistory(KairoConfig config) =>
        config.PcCalibrate == false ? null : _history ??= DecisionHistory.Open(KairoConfig.UserConfigDirectory);

    /// <summary>The fast loop's options from the user's config: limits, the confidence bar (per program, from the log), the extra questions.</summary>
    public static PursueOptions OptionsFor(KairoConfig config, IDecisionLog? log)
    {
        var baseline = config.PcMinConfidence ?? 0.7;
        return new PursueOptions(
            MaxSteps: config.PcMaxSteps ?? 25,
            MinConfidence: baseline,
            SemanticChecks: config.PcSemanticChecks != false,
            ConfidenceFor: log is DecisionHistory history ? app => history.MinConfidenceFor(app, baseline) : null,
            Log: log);
    }

    /// <summary>The tools, or none when PC control is off or no application was listed.</summary>
    /// <param name="overlay">The on-screen banner; by default the real one when the real desktop is used (and <c>[pc] overlay</c> is not off).</param>
    public static IReadOnlyList<ITool> For(KairoConfig config, Func<IDecisionModel?> decisionModel, IDesktop? desktop = null, ControlOverlay? overlay = null, IDecisionLog? log = null)
    {
        if (config.PcEnabled != true || (config.PcApps.Count == 0 && config.PcAllApps != true))
            return [];
        // The first decision should not pay for DNS and the TLS handshake: open the connection while the model is still writing its plan.
        if (Interlocked.Exchange(ref _warmed, 1) == 0)
            _ = Task.Run(async () =>
            {
                try
                {
                    if (decisionModel() is IWarmUp warm)
                        await warm.WarmUpAsync().ConfigureAwait(false);
                }
                catch (Exception)
                {
                }
            });

        var session = new PcSession(
            desktop ?? UiaDesktop.Shared,
            new PcGuard(config.PcApps, config.PcAllApps == true),
            decisionModel,
            OptionsFor(config, log ?? (desktop is null ? SharedHistory(config) : null)),
            overlay ?? (desktop is null && config.PcOverlay != false ? ControlOverlay.Shared : null),
            vision: config.PcVision == true,
            autonomous: config.PcAutonomous == true);
        var tools = new List<ITool> { new PcWindowsTool(session), new PcLaunchTool(session), new PcObserveTool(session), new PcActTool(session), new PcPursueTool(session), new PcRunPolicyTool(session), new PcCheckTool(session), new PcRunsTool(session) };
        // Pictures only when the user said the model can read them: otherwise the tool would only waste tokens.
        if (config.PcVision == true)
            tools.Insert(3, new PcScreenshotTool(session));
        return tools;
    }
}

internal sealed class PcSession(IDesktop desktop, PcGuard guard, Func<IDecisionModel?> decisionModel, PursueOptions options, ControlOverlay? overlay = null, bool vision = false, bool autonomous = false)
{
    /// <summary>The user said the model can read pictures: screenshots and pixel actions are on.</summary>
    public bool Vision { get; } = vision;

    private static readonly IDisposable NoBanner = new Nothing();

    private sealed class Nothing : IDisposable
    {
        public void Dispose()
        {
        }
    }

    /// <summary>Shows the "Kairo is driving the PC" banner until the returned handle is disposed.</summary>
    public IDisposable Begin() => overlay?.Begin() ?? NoBanner;

    public IDesktop Desktop { get; } = desktop;
    public PcGuard Guard { get; } = guard;
    public Func<IDecisionModel?> DecisionModel { get; } = decisionModel;
    public PursueOptions Options { get; } = options;

    /// <summary>The window the call means: the one named by id or program, else the front allowed window, else the only allowed one.</summary>
    public WindowInfo Resolve(string? window)
    {
        var all = Desktop.Windows();
        WindowInfo? found;
        if (!string.IsNullOrWhiteSpace(window))
        {
            var wanted = window.Trim();
            found = all.FirstOrDefault(w => w.Id.Equals(wanted, StringComparison.OrdinalIgnoreCase));
            found ??= all.FirstOrDefault(w => w.Process == PcGuard.Normalize(wanted));
            if (found is null)
                throw new ToolInputException($"There is no window '{wanted}'. List them with PcWindows or start the program with PcLaunch.");
        }
        else
        {
            var allowed = all.Where(w => Guard.IsAllowed(w.Process)).ToList();
            found = allowed.FirstOrDefault(w => w.Foreground) ?? (allowed.Count == 1 ? allowed[0] : null);
            if (found is null)
            {
                throw new ToolInputException(allowed.Count == 0
                    ? "No window of an allowed application is open. Start one with PcLaunch."
                    : $"Several windows are open; name one with the window parameter: {string.Join(", ", allowed.Select(w => $"{w.Id} ({w.Process})"))}.");
            }
        }
        if (Guard.Refusal(found) is { } refusal)
            throw new ToolInputException(refusal);
        return found;
    }

    /// <summary>
    /// Makes sure the connection to the fast model is alive right before a run: the big model may have spent half a minute thinking, long
    /// enough for an idle connection to die. Capped, and a failure is ignored (the first decision then opens the connection itself).
    /// </summary>
    public async Task WarmAsync(IDecisionModel model, CancellationToken ct)
    {
        if (model is not IWarmUp warm)
            return;
        using var cap = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cap.CancelAfter(TimeSpan.FromMilliseconds(1500));
        try
        {
            await warm.WarmUpAsync(cap.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
        }
    }

    /// <summary>Whether the user let the PC tools run without questions in the mode without confirmations.</summary>
    public bool Autonomous { get; } = autonomous;

    public ToolAssessment Assess(string tool, string process, string summary) =>
        new(Autonomous ? RiskLevel.Exec : RiskLevel.System, process, $"{tool}({summary})");

    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, string> _lastDigest = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, Observation> _shown = new();

    /// <summary>Above this many elements a result after an action lists what changed instead of the whole window.</summary>
    public const int DiffAbove = 40;

    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, ulong> _lastShot = new();

    /// <summary>Remembers what the latest screenshot of the window looked like, to tell later whether a click changed it.</summary>
    public void NoteShot(long handle, Snapshot shot) => _lastShot[handle] = System.IO.Hashing.XxHash3.HashToUInt64(shot.Picture.Png);

    /// <summary>
    /// A screenshot after a click or typing by pixels. A page or an app answers after the click returns, so the picture is taken again
    /// (every 150 ms, up to <paramref name="waitMs"/>) until it, or the window's element list, differs from what was seen before.
    /// </summary>
    public Snapshot ShotAfterAction(long handle, int waitMs, out int waitedMs, out bool changed)
    {
        var beforePicture = _lastShot.GetValueOrDefault(handle);
        var beforeTree = _lastDigest.GetValueOrDefault(handle);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        Snapshot shot;
        while (true)
        {
            shot = Desktop.Capture(handle);
            var picture = System.IO.Hashing.XxHash3.HashToUInt64(shot.Picture.Png);
            changed = beforePicture == 0 || picture != beforePicture
                      || beforeTree is not null && Desktop.Observe(handle).Digest != beforeTree;
            if (changed || clock.ElapsedMilliseconds >= waitMs || Desktop.StopRequested())
                break;
            Thread.Sleep(150);
        }
        waitedMs = (int)clock.ElapsedMilliseconds;
        NoteShot(handle, shot);
        return shot;
    }

    private PcRunQueue? _runs;

    /// <summary>Runs of the fast loop that go on in the background while the big model plans the next step.</summary>
    public PcRunQueue Runs => _runs ??= new PcRunQueue(Begin);

    /// <summary>Keyboard and mouse serve one thing at a time: while a background run is pending, tools that act on the screen are refused.</summary>
    public void EnsureIdle(string tool)
    {
        if (_runs is { Busy: true })
            throw new ToolInputException($"A background run is still working on the screen. {tool} would fight it for the keyboard and the mouse: collect it with PcRuns (await, or cancel) first.");
    }

    /// <summary>The screen as the model is handed it after a fast run; it counts as shown, so later results are relative to it.</summary>
    public string ShownText(Observation look)
    {
        Shown(look);
        return "\nThe screen as it is now (look " + look.Version + "; ids are valid for PcAct):\n" + look.ToText();
    }

    /// <summary>The model has been given this look of the window (whole or as changes): later looks are shown relative to it.</summary>
    public void Shown(Observation look)
    {
        _lastDigest[look.Window.Handle] = look.Digest;
        _shown[look.Window.Handle] = look;
    }

    /// <summary>The digest of the latest look at the window, or null when it has not been looked at.</summary>
    public string? LastDigest(long handle) => _lastDigest.GetValueOrDefault(handle);

    /// <summary>
    /// Looks at the window again after an action. When the screen still looks as it did at the previous look, waits for it to change, up
    /// to <paramref name="waitMs"/>: a page that loads or an app that is slow answers later than the action returns. Returns the new look
    /// and how long it waited.
    /// </summary>
    public Observation LookAfterAction(long handle, int waitMs, out int waitedMs)
    {
        var before = LastDigest(handle);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var look = Desktop.Observe(handle);
        while (before is not null && look.Digest == before && clock.ElapsedMilliseconds < waitMs && !Desktop.StopRequested())
        {
            Thread.Sleep(Math.Min(150, Math.Max(1, waitMs - (int)clock.ElapsedMilliseconds)));
            look = Desktop.Observe(handle);
        }
        waitedMs = (int)clock.ElapsedMilliseconds;
        return look;
    }

    public string Screen(long handle, out Observation observation, string? query = null, Observation? taken = null, int waitedMs = 0, bool allowChangesOnly = false)
    {
        observation = taken ?? Desktop.Observe(handle);
        var unchanged = taken is not null && waitedMs > 0 && _lastDigest.GetValueOrDefault(handle) == observation.Digest;
        var known = _shown.GetValueOrDefault(handle);
        _lastDigest[handle] = observation.Digest;

        // A big window after an action: the ids the model knows still mean the same elements, so only what changed needs saying.
        string? changesOnly = null;
        if (allowChangesOnly && known is not null && observation.Elements.Count > DiffAbove && string.IsNullOrWhiteSpace(query))
        {
            var changes = Observation.Changes(known, observation) ?? "(nothing in the element list changed)\n";
            if (changes.Count(c => c == '\n') <= 45)
                changesOnly = changes;
        }
        if (string.IsNullOrWhiteSpace(query))
            _shown[handle] = observation;

        var text = changesOnly is not null
            ? $"Look {observation.Version} of {observation.Window.Id} ({observation.Window.Process}, \"{observation.Window.Title}\"): {observation.Elements.Count} elements. " +
              "Element ids stay the same while the element is where it was, so the ids from your last look still work. What changed since that look:\n" + changesOnly +
              "(PcObserve lists the whole window; with query it finds one element.)\n"
            : $"Look {observation.Version}. Element ids stay the same from look to look while the element is where it was; one that moved or went has no id any more.\n" +
              (string.IsNullOrWhiteSpace(query) ? observation.ToText() : Filtered(observation, query));
        if (unchanged)
            text += $"(the window looks the same as before, even after waiting {waitedMs} ms; the action may have had no effect, or the result is not in the element list)\n";
        if (observation.Elements.Count < 3)
        {
            text += Vision
                ? "The window shows almost no elements to UI Automation: use PcScreenshot to see it and PcAct click_at / type_focused to work with it.\n"
                : "The window shows almost no elements to UI Automation (a game or a custom-drawn window?); it cannot be driven this way.\n";
        }
        return text;
    }

    /// <summary>Only the elements whose role, name, id or value contains one of the words of the query (separated by |): a big window shown briefly.</summary>
    private static string Filtered(Observation observation, string query)
    {
        var words = query.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var sb = new StringBuilder();
        sb.Append("Window ").Append(observation.Window.Id).Append(" · ").Append(observation.Window.Process).Append(" · \"").Append(observation.Window.Title).Append("\"\n");
        var matches = 0;
        foreach (var e in observation.Elements)
        {
            var hay = $"{e.Role} {e.Name} {e.AutomationId} {e.Value}";
            if (words.Any(w => hay.Contains(w, StringComparison.OrdinalIgnoreCase)))
            {
                sb.Append(Observation.Line(e with { Depth = 0 })).Append('\n');
                matches++;
            }
        }
        sb.Append('(').Append(matches).Append(" of ").Append(observation.Elements.Count).Append(" elements match \"").Append(query).Append("\"; look without query for the whole window)\n");
        return sb.ToString();
    }
}

internal sealed class PcWindowsTool(PcSession session) : ITool
{
    public ToolSpec Spec { get; } = new(
        "PcWindows",
        "Lists the open windows of the applications Kairo may control on this PC (id, program, title, which one is in front). " +
        "Other programs are not listed. The user can always stop PC control with the Pause key.",
        ToolInput.Schema());

    public ToolAssessment Assess(JsonElement input, ToolContext ctx) => new(RiskLevel.Read, ctx.WorkingDirectory, "PcWindows");

    public Task<ToolResult> InvokeAsync(JsonElement input, ToolContext ctx, CancellationToken ct)
    {
        var all = session.Desktop.Windows();
        var allowed = all.Where(w => session.Guard.IsAllowed(w.Process)).ToList();
        var sb = new StringBuilder();
        if (allowed.Count == 0)
            sb.Append("No window of an allowed application is open.\n");
        foreach (var w in allowed)
            sb.Append(w.Id).Append(" · ").Append(w.Process).Append(" · \"").Append(w.Title).Append('"').Append(w.Foreground ? " (in front)" : "").Append(w.Minimized ? " (minimized)" : "").Append('\n');
        if (session.Guard.AllowAll)
            sb.Append("Every program may be controlled except terminals, password managers, system tools, code editors and Kairo itself (those are not shown).");
        else
            sb.Append("Allowed programs: ").Append(string.Join(", ", session.Guard.Allowed.Order())).Append(". Windows of other programs are not shown and cannot be controlled.");
        sb.Append('\n').Append(KnownFolders());
        return Task.FromResult(new ToolResult(sb.ToString()));
    }

    /// <summary>
    /// Where the user's folders really are. A desktop that OneDrive moved is not under the profile, and a file dialog opens in Documents:
    /// without these paths a model saves "to the desktop" into the wrong place.
    /// </summary>
    internal static string KnownFolders()
    {
        var folders = new List<string>();
        void Add(string name, string path)
        {
            if (!string.IsNullOrEmpty(path))
                folders.Add($"{name}={path}");
        }
        Add("Desktop", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
        Add("Documents", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        if (Directory.Exists(downloads))
            Add("Downloads", downloads);
        return "Folders (type these full paths into file dialogs; a dialog opens in its own default folder, not on the desktop): " + string.Join("; ", folders);
    }
}

internal sealed class PcLaunchTool(PcSession session) : ITool
{
    public ToolSpec Spec { get; } = new(
        "PcLaunch",
        "Starts an application from the allowed list (for example notepad.exe) and returns its window id. Use PcWindows first: if the program is already open, use that window.",
        ToolInput.Schema(("application", "string", "The program name from the allowed list, e.g. notepad.exe.", true)));

    public ToolAssessment Assess(JsonElement input, ToolContext ctx)
    {
        var app = ToolInput.RequiredString(input, "application");
        var name = PcGuard.Normalize(app);
        if (session.Guard.IsDenied(name))
            throw new ToolInputException($"{name} cannot be controlled (terminals, password managers and system tools are always out of reach).");
        if (!session.Guard.IsAllowed(name))
            throw new ToolInputException($"{name} is not in the list of applications Kairo may control. The user adds it under [pc] apps in config.toml.");
        if (session.Guard.AllowAll && !session.Guard.IsListed(name) && session.Desktop.LaunchRefusal(name) is { } refusal)
            throw new ToolInputException(refusal);
        session.EnsureIdle("PcLaunch");
        return session.Assess("PcLaunch", name, name);
    }

    public Task<ToolResult> InvokeAsync(JsonElement input, ToolContext ctx, CancellationToken ct)
    {
        using var banner = session.Begin();
        var name = PcGuard.Normalize(ToolInput.RequiredString(input, "application"));
        // Started by its bare name, as the user's PATH and App Paths resolve it: a path from the model is never run.
        var window = session.Desktop.Launch(name, TimeSpan.FromSeconds(10));
        return Task.FromResult(window is null
            ? ToolResult.Error($"{name} was started but showed no window within 10 s. Check PcWindows.")
            : new ToolResult($"Started {name}: window {window.Id} \"{window.Title}\". Look at it with PcObserve."));
    }
}

internal sealed class PcObserveTool(PcSession session) : ITool
{
    public ToolSpec Spec { get; } = new(
        "PcObserve",
        "Reads a window as compact text: every visible element with an id (e1, e2…), its kind, name, state and what can be done with it " +
        "(invoke, setvalue, toggle, expand, select, scroll). Menus and dialogs that are open are included. Element ids belong to this look " +
        "only: use them with PcAct before looking again. A big window (a messenger, a browser) costs a lot of context: pass query to list only the " +
        "elements that mention one of its words (separate words with |), e.g. \"input|send|search\". An app that shows no elements (a game, a " +
        "custom-drawn window) cannot be driven this way.",
        ToolInput.Schema(
            ("window", "string", "Window id from PcWindows, or the program name. Default: the allowed window in front.", false),
            ("query", "string", "Only elements whose kind, name, id or value contains one of these words (separated by |).", false)));

    public ToolAssessment Assess(JsonElement input, ToolContext ctx)
    {
        var window = session.Resolve(ToolInput.OptionalString(input, "window"));
        return session.Assess("PcObserve", window.Process, $"{window.Process}: {window.Title}");
    }

    public Task<ToolResult> InvokeAsync(JsonElement input, ToolContext ctx, CancellationToken ct)
    {
        using var banner = session.Begin();
        var window = session.Resolve(ToolInput.OptionalString(input, "window"));
        return Task.FromResult(new ToolResult(session.Screen(window.Handle, out _, ToolInput.OptionalString(input, "query"))));
    }
}

internal sealed class PcScreenshotTool(PcSession session) : ITool
{
    public ToolSpec Spec { get; } = new(
        "PcScreenshot",
        "Takes a picture of a window (brought to the front) for when UI Automation shows too little: a game, a custom-drawn window, an image. " +
        "The result is the picture and its number. Coordinates for PcAct click_at / double_click_at are pixels of THIS picture, origin at its " +
        "top-left; give the picture's number as look. Prefer PcObserve when the window has a tree: it is exact and much cheaper.",
        ToolInput.Schema(("window", "string", "Window id or program name. Default: the allowed window in front.", false)));

    public ToolAssessment Assess(JsonElement input, ToolContext ctx)
    {
        var window = session.Resolve(ToolInput.OptionalString(input, "window"));
        session.EnsureIdle("PcScreenshot");
        return session.Assess("PcScreenshot", window.Process, $"{window.Process}: {window.Title}");
    }

    public Task<ToolResult> InvokeAsync(JsonElement input, ToolContext ctx, CancellationToken ct)
    {
        using var banner = session.Begin();
        var window = session.Resolve(ToolInput.OptionalString(input, "window"));
        return Task.FromResult(Take(session, window));
    }

    internal static ToolResult Take(PcSession session, WindowInfo window, string? prefix = null, Snapshot? taken = null, string? note = null)
    {
        Snapshot shot;
        try
        {
            shot = taken ?? session.Desktop.Capture(window.Handle);
        }
        catch (InvalidOperationException ex)
        {
            return ToolResult.Error(ex.Message);
        }
        if (taken is null)
            session.NoteShot(window.Handle, shot);
        var p = shot.Picture;
        var text = $"{prefix}Screenshot {shot.Id} of {window.Id} ({window.Process}, \"{window.Title}\"): {p.Width}x{p.Height} px" +
                   (p.Scale < 1 ? $", the window shrunk to {p.Scale.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)} of its size" : "") +
                   $". For PcAct click_at / double_click_at use look={shot.Id} and x, y in this picture's pixels, origin at the top-left." + note;
        return new ToolResult(text, Images: [new ToolImage("image/png", Convert.ToBase64String(p.Png))]);
    }
}

internal sealed class PcActTool(PcSession session) : ITool
{
    private static readonly Dictionary<string, DesktopActionKind> Kinds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["invoke"] = DesktopActionKind.Invoke, ["click"] = DesktopActionKind.Invoke, ["set_value"] = DesktopActionKind.SetValue,
        ["type_text"] = DesktopActionKind.TypeText, ["toggle"] = DesktopActionKind.Toggle, ["expand"] = DesktopActionKind.Expand,
        ["collapse"] = DesktopActionKind.Collapse, ["select"] = DesktopActionKind.Select, ["scroll_down"] = DesktopActionKind.ScrollDown,
        ["scroll_up"] = DesktopActionKind.ScrollUp, ["focus"] = DesktopActionKind.Focus, ["hotkey"] = DesktopActionKind.Hotkey,
        ["click_at"] = DesktopActionKind.ClickAt, ["double_click_at"] = DesktopActionKind.DoubleClickAt, ["drag_at"] = DesktopActionKind.DragAt, ["type_focused"] = DesktopActionKind.TypeFocused,
    };

    private static bool IsPixel(DesktopActionKind kind) => kind is DesktopActionKind.ClickAt or DesktopActionKind.DoubleClickAt or DesktopActionKind.DragAt or DesktopActionKind.TypeFocused;

    public ToolSpec Spec { get; } = new(
        "PcAct",
        "Does one thing in a window, then returns the new look of the window. action: invoke (click a button or menu item), set_value " +
        "(replace the text of an edit), type_text (type into an element), toggle, expand, collapse, select, scroll_down, scroll_up, focus, " +
        "or hotkey (ctrl+s, alt+tab…). element is an id from a PcObserve/PcAct result: an element keeps its id from look to look while it stays where it is, so several actions in a row need no look in between; the id of an element that went is refused. type_text without element types into whatever has the keyboard focus (click or focus the field first). " +
        "If the window looks the same right after the action (a page that is loading), the tool waits for it to change before it returns; " +
        "wait (seconds, up to 10) sets how long: use it after Enter in an address bar or a search box instead of pausing yourself. A click_at waits the same way " +
        "(until the picture or the element list changes) and then returns the new picture, so a page that opens after the click is what you see. Password fields cannot be filled. With vision on, click_at / double_click_at (x, y in the pixels of a PcScreenshot, " +
        "look = its number), drag_at (press at x, y, move to x2, y2, let go: draw a shape, select, move a slider) and type_focused (text into whatever has the focus) work where the tree shows nothing.",
        ToolInput.Schema(
            ("action", "string", "invoke, set_value, type_text, toggle, expand, collapse, select, scroll_down, scroll_up, focus, hotkey, click_at, double_click_at, drag_at or type_focused.", true),
            ("x", "integer", "For click_at / double_click_at: x in the screenshot's pixels.", false),
            ("y", "integer", "For click_at / double_click_at: y in the screenshot's pixels.", false),
            ("x2", "integer", "For drag_at: where the drag ends, x in the screenshot's pixels.", false),
            ("y2", "integer", "For drag_at: where the drag ends, y in the screenshot's pixels.", false),
            ("element", "string", "Element id such as e12 (not for hotkey).", false),
            ("look", "integer", "For click_at / double_click_at: the screenshot's number. Not needed for element ids.", false),
            ("text", "string", "The text for set_value and type_text, or the key combination for hotkey.", false),
            ("wait", "number", "Seconds to wait for the window to change after the action (0–10). Default 0.4 for hotkey and invoke, 1 for click_at, else 0.15.", false),
            ("window", "string", "Window id or program name. Default: the allowed window in front.", false)));

    public ToolAssessment Assess(JsonElement input, ToolContext ctx)
    {
        var (window, action, _) = Parse(input);
        session.EnsureIdle("PcAct");
        var what = action.Kind == DesktopActionKind.Hotkey ? $"hotkey {action.Text}" : IsPixel(action.Kind) ? action.ToString() : $"{action.Kind} {action.ElementId}";
        return session.Assess("PcAct", window.Process, $"{window.Process}: {what}");
    }

    public Task<ToolResult> InvokeAsync(JsonElement input, ToolContext ctx, CancellationToken ct)
    {
        using var banner = session.Begin();
        var (window, action, _) = Parse(input);
        if (session.Desktop.StopRequested())
            return Task.FromResult(ToolResult.Error("The user pressed the stop key (Pause). Nothing was done."));

        var outcome = session.Desktop.Perform(window.Handle, action);
        if (!outcome.Ok)
            return Task.FromResult(ToolResult.Error($"Not done: {outcome.Message}. Look again with PcObserve."));
        var head = $"{action}: {outcome.Message}.\n\n";
        // After a pixel action the picture is what shows the effect (the tree may show nothing): hand it back in the same call.
        if (IsPixel(action.Kind) && session.Vision)
        {
            var pixelWait = ToolInput.OptionalNumber(input, "wait") is { } pixelSeconds
                ? (int)(Math.Clamp(pixelSeconds, 0, 10) * 1000)
                : action.Kind == DesktopActionKind.TypeFocused ? 300 : 1000;
            Snapshot after;
            int pixelWaited;
            bool pictureChanged;
            try
            {
                after = session.ShotAfterAction(window.Handle, pixelWait, out pixelWaited, out pictureChanged);
            }
            catch (InvalidOperationException ex)
            {
                return Task.FromResult(WindowGone(head, ex));
            }
            var note = pictureChanged ? "" : $" The picture looks the same as before the action, even after waiting {pixelWaited} ms: it may have had no effect, or the page is still loading (use wait).";
            var shot = PcScreenshotTool.Take(session, window, head, after, note);
            return Task.FromResult(shot.IsError ? shot : shot with { Content = shot.Content + "\n\n" + session.Screen(window.Handle, out _) });
        }
        var wait = ToolInput.OptionalNumber(input, "wait") is { } seconds
            ? (int)(Math.Clamp(seconds, 0, 10) * 1000)
            : action.Kind is DesktopActionKind.Hotkey or DesktopActionKind.Invoke ? 400 : 150;
        Observation look;
        int waited;
        try
        {
            look = session.LookAfterAction(window.Handle, wait, out waited);
        }
        catch (InvalidOperationException ex)
        {
            return Task.FromResult(WindowGone(head, ex));
        }
        return Task.FromResult(new ToolResult(head + session.Screen(window.Handle, out _, taken: look, waitedMs: waited, allowChangesOnly: true)));
    }

    /// <summary>The action went through and then the window was no longer there: closing a window or quitting a program is this, not a failure.</summary>
    private static ToolResult WindowGone(string head, InvalidOperationException ex) =>
        ex.Message.Contains("gone", StringComparison.OrdinalIgnoreCase)
            ? new ToolResult(head + "The window is gone now: the action closed it (or the program quit). Nothing more to look at; PcWindows lists what is open.")
            : ToolResult.Error(ex.Message);

    private (WindowInfo Window, DesktopAction Action, string Verb) Parse(JsonElement input)
    {
        var verb = ToolInput.RequiredString(input, "action").Trim();
        if (!Kinds.TryGetValue(verb, out var kind) || IsPixel(kind) && !session.Vision)
            throw new ToolInputException($"Unknown action '{verb}'. Use {string.Join(", ", Kinds.Where(k => session.Vision || !IsPixel(k.Value)).Select(k => k.Key))}.");
        var window = session.Resolve(ToolInput.OptionalString(input, "window"));
        var text = ToolInput.OptionalString(input, "text");
        var element = ToolInput.OptionalString(input, "element")?.Trim();

        if (kind == DesktopActionKind.Hotkey)
        {
            if (text is null || !PcGuard.IsValidHotkey(text))
                throw new ToolInputException("hotkey needs text such as ctrl+s: modifiers (ctrl, alt, shift) and one key. The Windows key is not allowed.");
            return (window, new DesktopAction(kind, ToolInput.OptionalInt(input, "look") ?? 0, null, text.Trim().ToLowerInvariant()), verb);
        }

        if (kind is DesktopActionKind.ClickAt or DesktopActionKind.DoubleClickAt or DesktopActionKind.DragAt)
        {
            var shot = ToolInput.OptionalInt(input, "look") ?? throw new ToolInputException("look is required: the number of the PcScreenshot the point comes from.");
            if (ToolInput.OptionalInt(input, "x") is not { } x || ToolInput.OptionalInt(input, "y") is not { } y)
                throw new ToolInputException($"{verb} needs x and y: pixels of the screenshot.");
            if (kind == DesktopActionKind.DragAt)
            {
                if (ToolInput.OptionalInt(input, "x2") is not { } x2 || ToolInput.OptionalInt(input, "y2") is not { } y2)
                    throw new ToolInputException("drag_at needs x2 and y2: where the drag ends, in the same screenshot's pixels.");
                return (window, new DesktopAction(kind, shot, null, null, x, y, x2, y2), verb);
            }
            return (window, new DesktopAction(kind, shot, null, null, x, y), verb);
        }
        if (kind == DesktopActionKind.TypeFocused)
        {
            if (string.IsNullOrEmpty(text))
                throw new ToolInputException("type_focused needs text.");
            if (text.Length > 5000)
                throw new ToolInputException("The text is longer than 5000 characters; type it in parts.");
            return (window, new DesktopAction(kind, 0, null, text), verb);
        }

        if (kind == DesktopActionKind.TypeText && element is null or "")
        {
            // No element named: the text goes where the keyboard focus is, as after a click on the field.
            if (string.IsNullOrEmpty(text))
                throw new ToolInputException("type_text needs text.");
            if (text.Length > 5000)
                throw new ToolInputException("The text is longer than 5000 characters; type it in parts.");
            return (window, new DesktopAction(DesktopActionKind.TypeFocused, 0, null, text), verb);
        }
        if (element is null or "")
            throw new ToolInputException($"{verb} needs an element id from the latest look.");
        if (kind is DesktopActionKind.SetValue or DesktopActionKind.TypeText && text is null)
            throw new ToolInputException($"{verb} needs text.");
        if (text is { Length: > 5000 })
            throw new ToolInputException("The text is longer than 5000 characters; set it in parts.");
        return (window, new DesktopAction(kind, ToolInput.OptionalInt(input, "look") ?? 0, element, text), verb);
    }
}

internal sealed class PcPursueTool(PcSession session) : ITool
{
    public ToolSpec Spec { get; } = new(
        "PcPursue",
        "Hands one sub-goal to the fast decision model, which works through it step by step at about a third of a second a step, without " +
        "asking you in between: it reads the window, picks the next click or typing from what the screen offers, does it and checks the " +
        "result. Use it for the routine parts of a task (open a menu, fill a form from prepared texts, save). Give a clear success_criterion " +
        "that can be seen on the screen after its last action (a menu that closes when an item is clicked cannot show the item's state). The fast model only types texts you prepare in `texts` and presses shortcuts you list in `hotkeys`. " +
        "It stops and gives the screen back to you when it is unsure, when a step looks hard to undo (delete, send, pay, sign out, close " +
        "without saving) or when nothing changes: then decide that step yourself with PcAct. Needs a Jev key; without it use PcObserve and PcAct.",
        ToolInput.Schema(
            ("subgoal", "string", "What to achieve, in one sentence, e.g. 'Save the document as report.txt on the desktop'.", true),
            ("success_criterion", "string", "How the screen looks when it is done, e.g. 'The title bar shows report.txt'.", true),
            ("texts", "object", "Named texts the fast model may type, e.g. {\"filename\": \"report.txt\"}.", false),
            ("hotkeys", "string", "Shortcuts it may press, comma separated, e.g. 'ctrl+s, enter'.", false),
            ("max_steps", "integer", "Most steps to take (default 25, at most 60).", false),
            ("background", "boolean", "Start it and return at once: it runs while you plan the next sub-goal (queue that with background=true too); PcRuns await collects the results. Default false.", false),
            ("window", "string", "Window id or program name. Default: the allowed window in front.", false)));

    public ToolAssessment Assess(JsonElement input, ToolContext ctx)
    {
        if (!ToolInput.OptionalBool(input, "background"))
            session.EnsureIdle("PcPursue");
        var window = session.Resolve(ToolInput.OptionalString(input, "window"));
        var goal = ToolInput.RequiredString(input, "subgoal").Trim().ReplaceLineEndings(" ");
        return session.Assess("PcPursue", window.Process, $"{window.Process}: {(goal.Length > 100 ? goal[..100] + "…" : goal)}");
    }

    public async Task<ToolResult> InvokeAsync(JsonElement input, ToolContext ctx, CancellationToken ct)
    {
        var background = ToolInput.OptionalBool(input, "background");
        using var banner = background ? null : session.Begin();
        var window = session.Resolve(ToolInput.OptionalString(input, "window"));
        if (session.DecisionModel() is not { } model)
            return ToolResult.Error("The fast decision model is not set up (no Jev key). Use PcObserve and PcAct to do it step by step.");

        await session.WarmAsync(model, ct).ConfigureAwait(false);
        var plan = new PursuePlan(
            ToolInput.RequiredString(input, "subgoal").Trim(),
            ToolInput.RequiredString(input, "success_criterion").Trim(),
            ReadTexts(input),
            (ToolInput.OptionalString(input, "hotkeys") ?? "").Split([',', ';'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
        var options = session.Options with { MaxSteps = Math.Clamp(ToolInput.OptionalInt(input, "max_steps") ?? session.Options.MaxSteps, 1, 60) };

        if (background)
        {
            var queued = session.Runs.Enqueue(plan.Subgoal, async runCt =>
            {
                var done = await new PursueLoop(session.Desktop, session.Guard, model, options).RunAsync(window.Handle, plan, runCt).ConfigureAwait(false);
                return (done.Status == PursueStatus.Done, Describe(session, done));
            }, ct);
            return new ToolResult(Queued(session, queued, plan.Subgoal));
        }

        var report = await new PursueLoop(session.Desktop, session.Guard, model, options).RunAsync(window.Handle, plan, ct).ConfigureAwait(false);
        return new ToolResult(Describe(session, report), IsError: report.Status is PursueStatus.Failed);
    }

    internal static string Queued(PcSession session, int id, string title) =>
        $"Queued as run r{id} (\"{(title.Length > 80 ? title[..80] + "…" : title)}\"). It runs in the background" +
        (session.Runs.Snapshot().Count(r => !r.Finished) > 1 ? ", after the runs queued before it" : "") +
        ". Plan the next step now and queue it too, or collect the results with PcRuns (await). Do not act on the screen yourself in the meantime.";

    private static Dictionary<string, string> ReadTexts(JsonElement input)
    {
        var texts = new Dictionary<string, string>();
        if (input.TryGetProperty("texts", out var t) && t.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in t.EnumerateObject().Take(20))
            {
                if (p.Value.ValueKind == JsonValueKind.String && p.Value.GetString() is { Length: > 0 and <= 2000 } text)
                    texts[p.Name] = text;
            }
        }
        return texts;
    }

    internal static string Describe(PcSession session, PursueReport report)
    {
        var sb = new StringBuilder();
        sb.Append("Result: ").Append(report.Status.ToString().ToLowerInvariant()).Append(" — ").Append(report.Reason).Append('\n');
        sb.Append(report.Steps.Count).Append(" step(s), ").Append(report.TotalMs).Append(" ms in all.\n");
        foreach (var s in report.Steps)
        {
            sb.Append(s.Number).Append(". ").Append(s.Action).Append(" (confidence ").Append(s.Confidence.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture))
              .Append(s.Executed ? ", done" : ", not done").Append("; ").Append(s.Note).Append("; looked ").Append(s.ObserveMs).Append(" ms, decided ")
              .Append(s.DecideMs).Append(" ms, acted ").Append(s.ActMs).Append(" ms)\n");
        }
        if (report.Pending is not null)
            sb.Append("The fast model wanted: ").Append(report.Pending).Append(". Decide this step yourself (PcAct), or call PcPursue again with a narrower sub-goal.\n");
        if (report.Last is { } last && report.Status != PursueStatus.Done)
            sb.Append(session.ShownText(last));
        return sb.ToString();
    }
}

internal sealed class PcRunPolicyTool(PcSession session) : ITool
{
    public ToolSpec Spec { get; } = new(
        "PcRunPolicy",
        "Compile a whole routine job into a policy and let the fast model carry it out without you: use it for a task of several sub-goals " +
        "(open a dialog, fill it from prepared texts, confirm, save). The user sees the whole policy before it runs. It is data, not code:\n" +
        "{ \"goal\": \"...\", \"target\": {\"app\": \"notepad.exe\"}, \"texts\": {\"name\": \"report.txt\"}, " +
        "\"steps\": [ {\"id\": \"open\", \"goal\": \"Open the Save As dialog\", \"until\": \"A Save As dialog is open\", \"hotkeys\": [\"ctrl+shift+s\"]}, " +
        "{\"id\": \"name\", \"goal\": \"Type the file name and save\", \"until\": \"The title shows report.txt\", \"texts\": [\"name\"], \"hotkeys\": [\"enter\"]} ], " +
        "\"reflexes\": [ {\"name\": \"keep\", \"when\": {\"window_title_contains\": \"Confirm\", \"element\": {\"role\": \"button\", \"name_contains\": \"Yes\"}}, \"do\": {\"invoke\": {\"role\": \"button\", \"name_contains\": \"Yes\"}}} ], " +
        "\"limits\": {\"max_steps\": 60, \"max_seconds\": 120} }. " +
        "A step needs goal and until (how the screen looks when it is done); texts and hotkeys name only what the fast model may type or press. " +
        "A reflex answers a window you already know (invoke an element found by role/name, or a hotkey) with no model call and no delay; the user " +
        "approves reflexes with the policy, so only put a risky click there when you mean it. Without reflexes the fast model still hands back " +
        "anything unsure or hard to undo. The run stops at the first step that does not finish and returns the screen: continue from there with PcAct. " +
        "Needs a Jev key and a program from the allowed list.",
        ToolInput.Schema(
            ("policy", "object", "The policy (kairo.policy/v0), as described above.", true),
            ("background", "boolean", "Start it and return at once: it runs while you write the next policy (queue that with background=true too); PcRuns await collects the results. Default false.", false),
            ("window", "string", "Window id or program name. Default: the policy's program.", false)));

    public ToolAssessment Assess(JsonElement input, ToolContext ctx)
    {
        if (!ToolInput.OptionalBool(input, "background"))
            session.EnsureIdle("PcRunPolicy");
        var policy = Parse(input);
        var window = session.Resolve(ToolInput.OptionalString(input, "window") ?? policy.App);
        var goal = policy.Goal.ReplaceLineEndings(" ");
        return new ToolAssessment(session.Autonomous ? RiskLevel.Exec : RiskLevel.System, window.Process, $"PcRunPolicy({window.Process}: {(goal.Length > 100 ? goal[..100] + "…" : goal)})", policy.Describe());
    }

    public async Task<ToolResult> InvokeAsync(JsonElement input, ToolContext ctx, CancellationToken ct)
    {
        var background = ToolInput.OptionalBool(input, "background");
        using var banner = background ? null : session.Begin();
        var policy = Parse(input);
        var window = session.Resolve(ToolInput.OptionalString(input, "window") ?? policy.App);
        if (session.DecisionModel() is not { } model)
            return ToolResult.Error("The fast decision model is not set up (no Jev key). Use PcObserve and PcAct to do it step by step.");

        await session.WarmAsync(model, ct).ConfigureAwait(false);
        if (background)
        {
            var queued = session.Runs.Enqueue(policy.Goal, async runCt =>
            {
                var done = await new PolicyRunner(session.Desktop, session.Guard, model, session.Options).RunAsync(policy, window.Handle, runCt).ConfigureAwait(false);
                return (done.Status == PursueStatus.Done, Describe(session, done));
            }, ct);
            return new ToolResult(PcPursueTool.Queued(session, queued, policy.Goal));
        }
        var report = await new PolicyRunner(session.Desktop, session.Guard, model, session.Options).RunAsync(policy, window.Handle, ct).ConfigureAwait(false);
        return new ToolResult(Describe(session, report), IsError: report.Status == PursueStatus.Failed);
    }

    private PcPolicy Parse(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object || !input.TryGetProperty("policy", out var raw))
            throw new ToolInputException("policy is required.");
        // Models often send the object as a string of JSON; accept both.
        var json = raw;
        if (raw.ValueKind == JsonValueKind.String)
        {
            try
            {
                json = JsonDocument.Parse(raw.GetString() ?? "").RootElement.Clone();
            }
            catch (JsonException ex)
            {
                throw new ToolInputException($"policy is a string that is not valid JSON: {ex.Message}");
            }
        }

        var parsed = PolicyParser.Parse(json, session.Guard);
        if (parsed.Policy is null)
            throw new ToolInputException("The policy is not valid:\n- " + string.Join("\n- ", parsed.Errors));
        return parsed.Policy;
    }

    internal static string Describe(PcSession session, PolicyReport report)
    {
        var sb = new StringBuilder();
        sb.Append("Result: ").Append(report.Status.ToString().ToLowerInvariant()).Append(" — ").Append(report.Reason).Append('\n');
        sb.Append(report.Completed).Append('/').Append(report.Total).Append(" step(s) done, ").Append(report.ReflexFires).Append(" reflex firing(s), ")
          .Append(report.ElapsedMs).Append(" ms in all.\n");
        foreach (var s in report.Steps)
        {
            var actions = s.Report.Steps.Count(x => x.Executed);
            sb.Append("- [").Append(s.Id).Append("] ").Append(s.Goal).Append(": ").Append(s.Report.Status.ToString().ToLowerInvariant())
              .Append(" (").Append(actions).Append(" action(s), ").Append(s.Report.TotalMs).Append(" ms)\n");
        }
        if (report.Pending is not null)
            sb.Append("The fast model wanted: ").Append(report.Pending).Append(". Decide this step yourself (PcAct), then continue the remaining steps.\n");
        if (report.Last is { } last && report.Status != PursueStatus.Done)
            sb.Append(session.ShownText(last));
        return sb.ToString();
    }
}
