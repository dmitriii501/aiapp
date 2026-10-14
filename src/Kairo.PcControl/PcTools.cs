using System.Text;
using System.Text.Json;
using Kairo.Core.Config;
using Kairo.Core.Decisions;
using Kairo.Core.Models;
using Kairo.Core.Tools;

namespace Kairo.PcControl;

/// <summary>
/// The tools of the big model (System 2) for driving the PC. All of them are <see cref="RiskLevel.System"/>: they ask even in the
/// mode without confirmations, and only for applications the user listed under <c>[pc] apps</c>. The listing of windows is the one
/// exception (it shows the allowed applications only), so the model can find out what it may touch.
/// </summary>
public static class PcTools
{
    private static int _warmed;

    /// <summary>The tools, or none when PC control is off or no application was listed.</summary>
    public static IReadOnlyList<ITool> For(KairoConfig config, Func<IDecisionModel?> decisionModel, IDesktop? desktop = null)
    {
        if (config.PcEnabled != true || config.PcApps.Count == 0)
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
            new PcGuard(config.PcApps),
            decisionModel,
            new PursueOptions(MaxSteps: config.PcMaxSteps ?? 25, MinConfidence: config.PcMinConfidence ?? 0.7));
        return [new PcWindowsTool(session), new PcLaunchTool(session), new PcObserveTool(session), new PcActTool(session), new PcPursueTool(session), new PcRunPolicyTool(session)];
    }
}

internal sealed class PcSession(IDesktop desktop, PcGuard guard, Func<IDecisionModel?> decisionModel, PursueOptions options)
{
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

    public static ToolAssessment Assess(string tool, string process, string summary) => new(RiskLevel.System, process, $"{tool}({summary})");

    public string Screen(long handle, out Observation observation)
    {
        observation = Desktop.Observe(handle);
        return $"Look {observation.Version}. Use these element ids with PcAct (they are valid until the next look).\n" + observation.ToText();
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
        sb.Append("Allowed programs: ").Append(string.Join(", ", session.Guard.Allowed.Order())).Append(". Windows of other programs are not shown and cannot be controlled.");
        return Task.FromResult(new ToolResult(sb.ToString()));
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
        return PcSession.Assess("PcLaunch", name, name);
    }

    public Task<ToolResult> InvokeAsync(JsonElement input, ToolContext ctx, CancellationToken ct)
    {
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
        "only: use them with PcAct before looking again. An app that shows no elements (a game, a custom-drawn window) cannot be driven this way.",
        ToolInput.Schema(("window", "string", "Window id from PcWindows, or the program name. Default: the allowed window in front.", false)));

    public ToolAssessment Assess(JsonElement input, ToolContext ctx)
    {
        var window = session.Resolve(ToolInput.OptionalString(input, "window"));
        return PcSession.Assess("PcObserve", window.Process, $"{window.Process}: {window.Title}");
    }

    public Task<ToolResult> InvokeAsync(JsonElement input, ToolContext ctx, CancellationToken ct)
    {
        var window = session.Resolve(ToolInput.OptionalString(input, "window"));
        return Task.FromResult(new ToolResult(session.Screen(window.Handle, out _)));
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
    };

    public ToolSpec Spec { get; } = new(
        "PcAct",
        "Does one thing in a window, then returns the new look of the window. action: invoke (click a button or menu item), set_value " +
        "(replace the text of an edit), type_text (type into an element), toggle, expand, collapse, select, scroll_down, scroll_up, focus, " +
        "or hotkey (ctrl+s, alt+tab…). element is an id from the latest PcObserve/PcAct result and look is that result's number; ids from an " +
        "older look are refused. Password fields cannot be filled.",
        ToolInput.Schema(
            ("action", "string", "invoke, set_value, type_text, toggle, expand, collapse, select, scroll_down, scroll_up, focus or hotkey.", true),
            ("element", "string", "Element id such as e12 (not for hotkey).", false),
            ("look", "integer", "The look number the element id comes from.", false),
            ("text", "string", "The text for set_value and type_text, or the key combination for hotkey.", false),
            ("window", "string", "Window id or program name. Default: the allowed window in front.", false)));

    public ToolAssessment Assess(JsonElement input, ToolContext ctx)
    {
        var (window, action, _) = Parse(input);
        var what = action.Kind == DesktopActionKind.Hotkey ? $"hotkey {action.Text}" : $"{action.Kind} {action.ElementId}";
        return PcSession.Assess("PcAct", window.Process, $"{window.Process}: {what}");
    }

    public Task<ToolResult> InvokeAsync(JsonElement input, ToolContext ctx, CancellationToken ct)
    {
        var (window, action, _) = Parse(input);
        if (session.Desktop.StopRequested())
            return Task.FromResult(ToolResult.Error("The user pressed the stop key (Pause). Nothing was done."));

        var outcome = session.Desktop.Perform(window.Handle, action);
        if (!outcome.Ok)
            return Task.FromResult(ToolResult.Error($"Not done: {outcome.Message}. Look again with PcObserve."));
        return Task.FromResult(new ToolResult($"{action}: {outcome.Message}.\n\n{session.Screen(window.Handle, out _)}"));
    }

    private (WindowInfo Window, DesktopAction Action, string Verb) Parse(JsonElement input)
    {
        var verb = ToolInput.RequiredString(input, "action").Trim();
        if (!Kinds.TryGetValue(verb, out var kind))
            throw new ToolInputException($"Unknown action '{verb}'. Use {string.Join(", ", Kinds.Keys)}.");
        var window = session.Resolve(ToolInput.OptionalString(input, "window"));
        var text = ToolInput.OptionalString(input, "text");
        var element = ToolInput.OptionalString(input, "element")?.Trim();

        if (kind == DesktopActionKind.Hotkey)
        {
            if (text is null || !PcGuard.IsValidHotkey(text))
                throw new ToolInputException("hotkey needs text such as ctrl+s: modifiers (ctrl, alt, shift) and one key. The Windows key is not allowed.");
            return (window, new DesktopAction(kind, ToolInput.OptionalInt(input, "look") ?? 0, null, text.Trim().ToLowerInvariant()), verb);
        }

        if (element is null or "")
            throw new ToolInputException($"{verb} needs an element id from the latest look.");
        if (kind is DesktopActionKind.SetValue or DesktopActionKind.TypeText && text is null)
            throw new ToolInputException($"{verb} needs text.");
        if (text is { Length: > 5000 })
            throw new ToolInputException("The text is longer than 5000 characters; set it in parts.");
        var look = ToolInput.OptionalInt(input, "look") ?? throw new ToolInputException("look is required: the number from the PcObserve/PcAct result the element id comes from.");
        return (window, new DesktopAction(kind, look, element, text), verb);
    }
}

internal sealed class PcPursueTool(PcSession session) : ITool
{
    public ToolSpec Spec { get; } = new(
        "PcPursue",
        "Hands one sub-goal to the fast decision model, which works through it step by step at about a third of a second a step, without " +
        "asking you in between: it reads the window, picks the next click or typing from what the screen offers, does it and checks the " +
        "result. Use it for the routine parts of a task (open a menu, fill a form from prepared texts, save). Give a clear success_criterion " +
        "that can be seen on the screen. The fast model only types texts you prepare in `texts` and presses shortcuts you list in `hotkeys`. " +
        "It stops and gives the screen back to you when it is unsure, when a step looks hard to undo (delete, send, pay, sign out, close " +
        "without saving) or when nothing changes: then decide that step yourself with PcAct. Needs a Jev key; without it use PcObserve and PcAct.",
        ToolInput.Schema(
            ("subgoal", "string", "What to achieve, in one sentence, e.g. 'Save the document as report.txt on the desktop'.", true),
            ("success_criterion", "string", "How the screen looks when it is done, e.g. 'The title bar shows report.txt'.", true),
            ("texts", "object", "Named texts the fast model may type, e.g. {\"filename\": \"report.txt\"}.", false),
            ("hotkeys", "string", "Shortcuts it may press, comma separated, e.g. 'ctrl+s, enter'.", false),
            ("max_steps", "integer", "Most steps to take (default 25, at most 60).", false),
            ("window", "string", "Window id or program name. Default: the allowed window in front.", false)));

    public ToolAssessment Assess(JsonElement input, ToolContext ctx)
    {
        var window = session.Resolve(ToolInput.OptionalString(input, "window"));
        var goal = ToolInput.RequiredString(input, "subgoal").Trim().ReplaceLineEndings(" ");
        return PcSession.Assess("PcPursue", window.Process, $"{window.Process}: {(goal.Length > 100 ? goal[..100] + "…" : goal)}");
    }

    public async Task<ToolResult> InvokeAsync(JsonElement input, ToolContext ctx, CancellationToken ct)
    {
        var window = session.Resolve(ToolInput.OptionalString(input, "window"));
        if (session.DecisionModel() is not { } model)
            return ToolResult.Error("The fast decision model is not set up (no Jev key). Use PcObserve and PcAct to do it step by step.");

        var plan = new PursuePlan(
            ToolInput.RequiredString(input, "subgoal").Trim(),
            ToolInput.RequiredString(input, "success_criterion").Trim(),
            ReadTexts(input),
            (ToolInput.OptionalString(input, "hotkeys") ?? "").Split([',', ';'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
        var options = session.Options with { MaxSteps = Math.Clamp(ToolInput.OptionalInt(input, "max_steps") ?? session.Options.MaxSteps, 1, 60) };

        var report = await new PursueLoop(session.Desktop, session.Guard, model, options).RunAsync(window.Handle, plan, ct).ConfigureAwait(false);
        return new ToolResult(Describe(report), IsError: report.Status is PursueStatus.Failed);
    }

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

    internal static string Describe(PursueReport report)
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
            sb.Append("\nThe screen as it is now (look ").Append(last.Version).Append("; ids are valid for PcAct):\n").Append(last.ToText());
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
            ("window", "string", "Window id or program name. Default: the policy's program.", false)));

    public ToolAssessment Assess(JsonElement input, ToolContext ctx)
    {
        var policy = Parse(input);
        var window = session.Resolve(ToolInput.OptionalString(input, "window") ?? policy.App);
        var goal = policy.Goal.ReplaceLineEndings(" ");
        return new ToolAssessment(RiskLevel.System, window.Process, $"PcRunPolicy({window.Process}: {(goal.Length > 100 ? goal[..100] + "…" : goal)})", policy.Describe());
    }

    public async Task<ToolResult> InvokeAsync(JsonElement input, ToolContext ctx, CancellationToken ct)
    {
        var policy = Parse(input);
        var window = session.Resolve(ToolInput.OptionalString(input, "window") ?? policy.App);
        if (session.DecisionModel() is not { } model)
            return ToolResult.Error("The fast decision model is not set up (no Jev key). Use PcObserve and PcAct to do it step by step.");

        var report = await new PolicyRunner(session.Desktop, session.Guard, model, session.Options).RunAsync(policy, window.Handle, ct).ConfigureAwait(false);
        return new ToolResult(Describe(report), IsError: report.Status == PursueStatus.Failed);
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

    internal static string Describe(PolicyReport report)
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
            sb.Append("\nThe screen as it is now (look ").Append(last.Version).Append("; ids are valid for PcAct):\n").Append(last.ToText());
        return sb.ToString();
    }
}
