using System.Text.Json;

namespace Kairo.PcControl;

// "The big model compiles, the fast one executes" (ADR-0003) for programs that UI Automation can see. The big model writes a policy once:
// the steps to take, the texts and shortcuts the fast model may use, and reflexes for windows it already knows how to answer. The runtime
// then works through it without the big model. A policy is data, not code: it can only name things from this fixed vocabulary, and it
// is checked, shown to the user and bounded before anything runs. The game-oriented parts of docs/specs/decision-policy.md (frame
// capture, OCR, templates) are not part of v0.

/// <summary>Finds an element by what it is called. All given conditions must hold; names are compared without case.</summary>
public sealed record ElementSelector(string? Role = null, string? NameEquals = null, string? NameContains = null, string? AutomationId = null)
{
    public bool IsEmpty => Role is null && NameEquals is null && NameContains is null && AutomationId is null;

    public bool Matches(UiElement e) =>
        e.Enabled
        && (Role is null || e.Role.Equals(Role, StringComparison.OrdinalIgnoreCase))
        && (NameEquals is null || e.Name.Equals(NameEquals, StringComparison.OrdinalIgnoreCase))
        && (NameContains is null || e.Name.Contains(NameContains, StringComparison.OrdinalIgnoreCase))
        && (AutomationId is null || string.Equals(e.AutomationId, AutomationId, StringComparison.OrdinalIgnoreCase));

    public override string ToString() => string.Join(", ", new[]
    {
        Role is null ? null : $"role {Role}", NameEquals is null ? null : $"name \"{NameEquals}\"",
        NameContains is null ? null : $"name contains \"{NameContains}\"", AutomationId is null ? null : $"#{AutomationId}",
    }.Where(p => p is not null));
}

/// <summary>When a reflex fires: the window title and/or an element is there. At least one condition is needed.</summary>
public sealed record ReflexCondition(string? WindowTitleContains = null, ElementSelector? Element = null);

/// <summary>What a reflex does: invoke an element it finds, or press a shortcut. Exactly one of them.</summary>
public sealed record ReflexAction(ElementSelector? Invoke = null, string? Hotkey = null);

/// <param name="MaxFires">A reflex that fires this often in one run stops firing (it is not helping).</param>
public sealed record Reflex(string Name, ReflexCondition When, ReflexAction Do, int MaxFires = 5);

public sealed record PolicyStep(string Id, string Goal, string Until, IReadOnlyList<string> Texts, IReadOnlyList<string> Hotkeys, int MaxSteps = 15);

public sealed record PolicyLimits(int MaxSteps = 100, int MaxSeconds = 180);

public sealed record PcPolicy(
    string Goal,
    string App,
    IReadOnlyDictionary<string, string> Texts,
    IReadOnlyList<PolicyStep> Steps,
    IReadOnlyList<Reflex> Reflexes,
    PolicyLimits Limits)
{
    public const string Schema = "kairo.policy/v0";
    public const int MaxStepsInPolicy = 30;
    public const int MaxReflexes = 20;

    /// <summary>What the user is shown before the policy runs: the program, every step, every reflex, the limits.</summary>
    public string Describe()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("Goal: ").Append(Goal).Append('\n').Append("Program: ").Append(App).Append('\n');
        sb.Append("Steps (").Append(Steps.Count).Append("):\n");
        for (var i = 0; i < Steps.Count; i++)
        {
            var s = Steps[i];
            sb.Append("  ").Append(i + 1).Append(". ").Append(s.Goal).Append("  [done when: ").Append(s.Until).Append(']');
            if (s.Texts.Count > 0)
                sb.Append("  texts: ").Append(string.Join(", ", s.Texts.Select(t => $"{t} = \"{Clip(Texts[t])}\"")));
            if (s.Hotkeys.Count > 0)
                sb.Append("  keys: ").Append(string.Join(", ", s.Hotkeys));
            sb.Append('\n');
        }
        if (Reflexes.Count > 0)
        {
            sb.Append("Reflexes (act without asking the model):\n");
            foreach (var r in Reflexes)
            {
                var when = string.Join(" and ", new[] { r.When.WindowTitleContains is null ? null : $"window title contains \"{r.When.WindowTitleContains}\"", r.When.Element is null ? null : $"there is an element ({r.When.Element})" }.Where(p => p is not null));
                sb.Append("  ").Append(r.Name).Append(": when ").Append(when).Append(" -> ").Append(r.Do.Hotkey is not null ? $"press {r.Do.Hotkey}" : $"click the element ({r.Do.Invoke})").Append('\n');
            }
        }
        sb.Append("Limits: ").Append(Limits.MaxSteps).Append(" steps, ").Append(Limits.MaxSeconds).Append(" s. The fast model hands any unsure or hard-to-undo step back.");
        return sb.ToString();

        static string Clip(string text) => text.Length <= 40 ? text : text[..40] + "…";
    }
}

public sealed record PolicyParse(PcPolicy? Policy, IReadOnlyList<string> Errors)
{
    public bool Ok => Policy is not null;
}

public static class PolicyParser
{
    /// <summary>Reads and checks a policy. Every problem is listed, so the model that wrote it can fix them all at once.</summary>
    public static PolicyParse Parse(JsonElement json, PcGuard guard)
    {
        var errors = new List<string>();
        if (json.ValueKind != JsonValueKind.Object)
            return new(null, ["The policy must be a JSON object."]);

        if (Str(json, "$schema") is { } schema && schema != PcPolicy.Schema)
            errors.Add($"$schema must be \"{PcPolicy.Schema}\" (or left out), not \"{schema}\".");

        var goal = Str(json, "goal")?.Trim();
        if (string.IsNullOrEmpty(goal))
            errors.Add("goal is required.");

        string? app = null;
        if (json.TryGetProperty("target", out var target) && target.ValueKind == JsonValueKind.Object && Str(target, "app") is { Length: > 0 } rawApp)
        {
            app = PcGuard.Normalize(rawApp);
            if (guard.IsDenied(app))
                errors.Add($"target.app: {app} cannot be controlled.");
            else if (!guard.IsAllowed(app))
                errors.Add($"target.app: {app} is not in the list of applications Kairo may control.");
        }
        else
            errors.Add("target.app is required, e.g. {\"target\": {\"app\": \"notepad.exe\"}}.");

        var texts = new Dictionary<string, string>();
        if (json.TryGetProperty("texts", out var textsJson))
        {
            if (textsJson.ValueKind != JsonValueKind.Object)
                errors.Add("texts must be an object of name → text.");
            else
            {
                foreach (var p in textsJson.EnumerateObject())
                {
                    if (p.Value.ValueKind != JsonValueKind.String || p.Value.GetString() is not { Length: > 0 and <= 2000 } value)
                        errors.Add($"texts.{p.Name} must be a string of 1 to 2000 characters.");
                    else
                        texts[p.Name] = value;
                }
                if (texts.Count > 30)
                    errors.Add("At most 30 texts.");
            }
        }

        var steps = new List<PolicyStep>();
        if (!json.TryGetProperty("steps", out var stepsJson) || stepsJson.ValueKind != JsonValueKind.Array || stepsJson.GetArrayLength() == 0)
            errors.Add("steps is required: a non-empty array.");
        else if (stepsJson.GetArrayLength() > PcPolicy.MaxStepsInPolicy)
            errors.Add($"At most {PcPolicy.MaxStepsInPolicy} steps; split a longer job into several policies.");
        else
        {
            var index = 0;
            foreach (var s in stepsJson.EnumerateArray())
            {
                index++;
                var where = $"steps[{index}]";
                if (s.ValueKind != JsonValueKind.Object)
                {
                    errors.Add($"{where} must be an object.");
                    continue;
                }
                var stepGoal = Str(s, "goal")?.Trim();
                var until = Str(s, "until")?.Trim();
                if (string.IsNullOrEmpty(stepGoal)) errors.Add($"{where}.goal is required.");
                if (string.IsNullOrEmpty(until)) errors.Add($"{where}.until is required: how the screen looks when the step is done.");

                var stepTexts = new List<string>();
                foreach (var name in Strings(s, "texts", errors, where))
                {
                    if (!texts.ContainsKey(name))
                        errors.Add($"{where}.texts names \"{name}\", which is not defined under texts.");
                    else
                        stepTexts.Add(name);
                }
                var hotkeys = new List<string>();
                foreach (var key in Strings(s, "hotkeys", errors, where))
                {
                    if (!PcGuard.IsValidHotkey(key))
                        errors.Add($"{where}.hotkeys: \"{key}\" is not a shortcut Kairo will press (modifiers ctrl/alt/shift and one key; no Windows key).");
                    else
                        hotkeys.Add(key.Trim().ToLowerInvariant());
                }
                var max = s.TryGetProperty("max_steps", out var m) && m.TryGetInt32(out var mv) ? Math.Clamp(mv, 1, 40) : 15;
                steps.Add(new PolicyStep(Str(s, "id")?.Trim() is { Length: > 0 } id ? id : $"step{index}", stepGoal ?? "", until ?? "", stepTexts, hotkeys, max));
            }
            if (steps.Select(x => x.Id).Distinct().Count() != steps.Count)
                errors.Add("Step ids must be unique.");
        }

        var reflexes = new List<Reflex>();
        if (json.TryGetProperty("reflexes", out var reflexesJson))
        {
            if (reflexesJson.ValueKind != JsonValueKind.Array)
                errors.Add("reflexes must be an array.");
            else if (reflexesJson.GetArrayLength() > PcPolicy.MaxReflexes)
                errors.Add($"At most {PcPolicy.MaxReflexes} reflexes.");
            else
            {
                var index = 0;
                foreach (var r in reflexesJson.EnumerateArray())
                {
                    index++;
                    if (ParseReflex(r, $"reflexes[{index}]", index, errors) is { } reflex)
                        reflexes.Add(reflex);
                }
            }
        }

        var limits = new PolicyLimits();
        if (json.TryGetProperty("limits", out var limitsJson) && limitsJson.ValueKind == JsonValueKind.Object)
        {
            limits = new PolicyLimits(
                limitsJson.TryGetProperty("max_steps", out var ms) && ms.TryGetInt32(out var msv) ? Math.Clamp(msv, 1, 300) : limits.MaxSteps,
                limitsJson.TryGetProperty("max_seconds", out var sec) && sec.TryGetInt32(out var secv) ? Math.Clamp(secv, 5, 900) : limits.MaxSeconds);
        }

        return errors.Count > 0 || app is null
            ? new(null, errors)
            : new(new PcPolicy(goal!, app, texts, steps, reflexes, limits), []);
    }

    private static Reflex? ParseReflex(JsonElement r, string where, int index, List<string> errors)
    {
        if (r.ValueKind != JsonValueKind.Object)
        {
            errors.Add($"{where} must be an object.");
            return null;
        }
        var before = errors.Count;
        var name = Str(r, "name")?.Trim() is { Length: > 0 } n ? n : $"reflex{index}";

        ReflexCondition? when = null;
        if (!r.TryGetProperty("when", out var w) || w.ValueKind != JsonValueKind.Object)
            errors.Add($"{where}.when is required: {{\"window_title_contains\": …}} and/or {{\"element\": {{…}}}}.");
        else
        {
            var title = Str(w, "window_title_contains");
            var element = w.TryGetProperty("element", out var e) ? ParseSelector(e, $"{where}.when.element", errors) : null;
            if (title is null && element is null)
                errors.Add($"{where}.when needs window_title_contains or element.");
            when = new ReflexCondition(title, element);
        }

        ReflexAction? action = null;
        if (!r.TryGetProperty("do", out var d) || d.ValueKind != JsonValueKind.Object)
            errors.Add($"{where}.do is required: {{\"invoke\": {{…}}}} or {{\"hotkey\": \"enter\"}}.");
        else
        {
            var invoke = d.TryGetProperty("invoke", out var i) ? ParseSelector(i, $"{where}.do.invoke", errors) : null;
            var hotkey = Str(d, "hotkey");
            if ((invoke is null) == (hotkey is null))
                errors.Add($"{where}.do needs exactly one of invoke or hotkey.");
            else if (hotkey is not null && !PcGuard.IsValidHotkey(hotkey))
                errors.Add($"{where}.do.hotkey \"{hotkey}\" is not a shortcut Kairo will press.");
            action = new ReflexAction(invoke, hotkey?.Trim().ToLowerInvariant());
        }

        var fires = r.TryGetProperty("max_fires", out var f) && f.TryGetInt32(out var fv) ? Math.Clamp(fv, 1, 50) : 5;
        return errors.Count > before || when is null || action is null ? null : new Reflex(name, when, action, fires);
    }

    private static ElementSelector? ParseSelector(JsonElement e, string where, List<string> errors)
    {
        if (e.ValueKind != JsonValueKind.Object)
        {
            errors.Add($"{where} must be an object like {{\"role\": \"button\", \"name_contains\": \"OK\"}}.");
            return null;
        }
        var selector = new ElementSelector(Str(e, "role"), Str(e, "name_equals"), Str(e, "name_contains"), Str(e, "automation_id"));
        if (selector.IsEmpty)
        {
            errors.Add($"{where} is empty: give role, name_equals, name_contains or automation_id.");
            return null;
        }
        return selector;
    }

    private static string? Str(JsonElement o, string name) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static IEnumerable<string> Strings(JsonElement o, string name, List<string> errors, string where)
    {
        if (!o.TryGetProperty(name, out var v))
            yield break;
        if (v.ValueKind != JsonValueKind.Array)
        {
            errors.Add($"{where}.{name} must be an array of strings.");
            yield break;
        }
        foreach (var item in v.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } text)
                yield return text;
            else
                errors.Add($"{where}.{name} must contain only non-empty strings.");
        }
    }
}

/// <summary>Reflexes of a policy at run time: finds the first one whose condition holds on a screen, and counts how often each fired.</summary>
public sealed class ReflexSet(IReadOnlyList<Reflex> reflexes)
{
    private readonly Dictionary<string, int> _fired = new();

    public int Count => reflexes.Count;

    public int Fired(string name) => _fired.GetValueOrDefault(name);

    public int TotalFired => _fired.Values.Sum();

    /// <summary>The action to take on this screen, or null. Nothing here calls a model: it is a handful of string comparisons.</summary>
    public (Reflex Reflex, DesktopAction Action)? Match(Observation screen)
    {
        foreach (var reflex in reflexes)
        {
            if (_fired.GetValueOrDefault(reflex.Name) >= reflex.MaxFires)
                continue;
            if (reflex.When.WindowTitleContains is { } title && !screen.Window.Title.Contains(title, StringComparison.OrdinalIgnoreCase)
                && !screen.Elements.Any(e => e.Role is "dialog" or "popup menu" && e.Name.Contains(title, StringComparison.OrdinalIgnoreCase)))
                continue;

            UiElement? element = null;
            if (reflex.When.Element is { } condition)
            {
                element = screen.Elements.FirstOrDefault(condition.Matches);
                if (element is null)
                    continue;
            }

            DesktopAction? action = null;
            if (reflex.Do.Hotkey is { } keys)
                action = new DesktopAction(DesktopActionKind.Hotkey, screen.Version, null, keys);
            else if (reflex.Do.Invoke is { } target && screen.Elements.FirstOrDefault(target.Matches) is { } found)
                action = new DesktopAction(DesktopActionKind.Invoke, screen.Version, found.Id);
            if (action is null)
                continue;

            _fired[reflex.Name] = _fired.GetValueOrDefault(reflex.Name) + 1;
            return (reflex, action);
        }
        return null;
    }
}
