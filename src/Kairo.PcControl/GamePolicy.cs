using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Kairo.PcControl;

/// <summary>A part of the window picture, as fractions of its width and height: the game board instead of the whole screen.</summary>
public sealed record GameRegion(double Left, double Top, double Right, double Bottom)
{
    public static readonly GameRegion Whole = new(0, 0, 1, 1);
}

/// <param name="Presence">Also ask whether the thing is in the picture at all (a box is always returned, even for what is not there).</param>
public sealed record GameFind(string Name, string Description, bool Presence = true);

public sealed record GameCheck(string Name, string Statement);

public sealed record GameChoose(string Name, string Question, IReadOnlyList<KeyValuePair<string, string>> Options);

public abstract record GameAction;

/// <param name="Target">A thing from see.find; or null with <paramref name="X"/> and <paramref name="Y"/> as expressions in the pixels of the window picture.</param>
public sealed record ClickAction(string? Target, GameExpr? X, GameExpr? Y, string Button, double OffsetX, double OffsetY) : GameAction;

public sealed record DragAction(string From, string To) : GameAction;

public sealed record KeyAction(string Keys) : GameAction;

/// <summary>Stores a value under a key in a map (a card index and the symbol seen on it, say). Maps are declared under vars as {}.</summary>
public sealed record PutAction(string Map, GameExpr Key, GameExpr Value) : GameAction;

public sealed record DelAction(string Map, GameExpr Key) : GameAction;

public sealed record ClearAction(string Map) : GameAction;

public sealed record SetAction(string Variable, GameExpr Value) : GameAction;

public sealed record WaitAction(int Milliseconds) : GameAction;

/// <param name="Handover">Ends the run so that the big model takes over (it gets what Jev saw), instead of a finished game.</param>
public sealed record StopAction(string Reason, bool Handover) : GameAction;

/// <param name="Once">The rule fires at most once in a run.</param>
/// <param name="Cooldown">Ticks that must pass before it may fire again.</param>
public sealed record GameRule(string Name, GameExpr When, IReadOnlyList<GameAction> Do, bool Once, int Cooldown);

/// <param name="Pipeline">Two looks are in flight at once, the second started half a look after the first, so decisions come about twice as often (each from a picture that is up to one look old). A click close to one made since the picture was taken is skipped. Only for policies that keep no notes.</param>
public sealed record GameLimits(int MaxTicks = 600, int MaxSeconds = 300, int StallTicks = 8, int TickMs = 0, bool Pipeline = false);

/// <summary>
/// A game (or any window the element list cannot read) played by looking and reacting, without the big model in the loop: every tick
/// Jev is asked the same questions about the picture, the rules decide what to do from the answers, and the actions are clicks or keys.
/// The big model writes the policy once; Kairo checks all of it before anything runs, and shows it to the user.
/// </summary>
public sealed record GamePolicy(
    string Goal,
    string App,
    GameRegion Region,
    int Width,
    IReadOnlyList<GameFind> Finds,
    IReadOnlyList<GameCheck> Checks,
    IReadOnlyList<GameChoose> Chooses,
    IReadOnlyDictionary<string, object> Vars,
    IReadOnlyList<GameRule> Rules,
    GameLimits Limits)
{
    public const int DefaultWidth = 1024;
    public const int MaxQuestions = 40;

    public string Describe()
    {
        var sb = new StringBuilder();
        sb.Append("Game policy: ").Append(Goal).Append('\n').Append("Program: ").Append(App).Append(". Every tick a screenshot");
        if (Region != GameRegion.Whole)
            sb.Append(" of the part ").Append(Pct(Region.Left)).Append('-').Append(Pct(Region.Right)).Append(" wide, ").Append(Pct(Region.Top)).Append('-').Append(Pct(Region.Bottom)).Append(" high");
        sb.Append(" goes to Jev (TypeSafe), ").Append(Width).Append(" px wide.\n");
        foreach (var f in Finds)
            sb.Append("  find ").Append(f.Name).Append(": ").Append(f.Description).Append('\n');
        foreach (var c in Checks)
            sb.Append("  check ").Append(c.Name).Append(": ").Append(c.Statement).Append('\n');
        foreach (var c in Chooses)
            sb.Append("  choose ").Append(c.Name).Append(": ").Append(c.Question).Append(" (").Append(string.Join(", ", c.Options.Select(o => o.Key))).Append(")\n");
        foreach (var r in Rules)
            sb.Append("  rule ").Append(r.Name).Append(": when ").Append(r.When.Text).Append(" do ").Append(string.Join("; ", r.Do.Select(Describe))).Append(r.Once ? " (once)" : "").Append(r.Cooldown > 0 ? $" (every {r.Cooldown}+ ticks)" : "").Append('\n');
        sb.Append("Limits: ").Append(Limits.MaxTicks).Append(" ticks, ").Append(Limits.MaxSeconds).Append(" s; the Pause key stops it; stops when ")
          .Append(Limits.StallTicks).Append(" ticks in a row do nothing or change nothing.");
        return sb.ToString();
    }

    private static string Pct(double f) => (f * 100).ToString("0", CultureInfo.InvariantCulture) + "%";

    internal static string Describe(GameAction a) => a switch
    {
        ClickAction { Target: { } t } c => $"{(c.Button == "left" ? "click" : c.Button + " click")} {t}",
        ClickAction c => $"{(c.Button == "left" ? "click" : c.Button + " click")} at ({c.X!.Text}, {c.Y!.Text})",
        DragAction d => $"drag {d.From} to {d.To}",
        KeyAction k => $"press {k.Keys}",
        PutAction p => $"put {p.Map}[{p.Key.Text}] = {p.Value.Text}",
        DelAction d => $"del {d.Map}[{d.Key.Text}]",
        ClearAction c => $"clear {c.Map}",
        SetAction s => $"set {s.Variable} = {s.Value.Text}",
        WaitAction w => $"wait {w.Milliseconds} ms",
        StopAction { Handover: true } s => $"hand over ({s.Reason})",
        StopAction s => $"stop ({s.Reason})",
        _ => a.ToString() ?? "",
    };
}

public sealed record GamePolicyParse(GamePolicy? Policy, IReadOnlyList<string> Errors)
{
    public bool Ok => Policy is not null;
}

public static partial class GamePolicyParser
{
    /// <summary>What a model needs to know to write a policy; the PcPlay tool shows it as its description.</summary>
    public const string ToolDescription =
        "Plays a game, or reacts to a window the element list cannot read, FAST and without you in the loop: you write a policy once; Kairo then " +
        "looks at the window every tick (one request to the fast vision model Jev, 0.5 to 1 s), runs your rules on the answers and clicks or presses " +
        "keys, until a rule stops it, a limit is reached, or nothing happens. Use it for reaction-style work (click what appears, answer what is asked, " +
        "press what is shown, repeat a routine); for moves that need your thinking use PcSee and PcClick one move at a time. " +
        "Policy (JSON): {goal, app (such as msedge.exe), region? {left, top, right, bottom as fractions of the window: look only at the board, it is faster}, " +
        "width? (pixels sent to Jev, 320-1568, default 1024; smaller is faster), " +
        "see: {find: {name: 'short description'} (where is it), check: {name: 'statement'} (probability that it holds), choose: {name: {question, options: {key: 'meaning'}}}}, " +
        "vars? {name: initial value}, rules: [{name?, when: 'expression', do: [actions], once?, cooldown? (ticks)}], limits? {max_ticks, max_seconds, stall_ticks, tick_ms}}. " +
        "Every tick the FIRST rule whose when is true runs. Expressions read find.NAME.seen|conf|x|y|w|h|left|top|right|bottom (x, y: the centre in window pixels; seen and conf 0 to 1), " +
        "check.NAME (0 to 1), choose.NAME (the key chosen) and choose.NAME.conf, tick, elapsed (seconds), idle (ticks in a row nothing was done), and your vars; " +
        "they use + - * / %, == != < <= > >=, && || !, ( ) and min max abs round floor ceil random. Actions: {click: 'findName'} or {click: {x: 'expr', y: 'expr'}} " +
        "(button: left, right or double; dx, dy), {drag: {from: 'findName', to: 'findName'}}, {key: 'space'}, {set: 'var = expr'}, {wait: ms}, {stop: 'reason'} when the goal is reached, " +
        "{put: {map, key, value}}, {del: {map, key}}, {clear: 'map'} (a var declared as {} is a map: notes by key, for what has to be remembered, such as which symbol is on which card; read with get(map, key[, fallback]), has(map, key), count(map), first_free(map, n) (the first number below n that is not a key), find_key(map, value, skipMap, exceptKey) (a key holding that value), pair_a(map, skipMap) and pair_b(map, skipMap) (two keys holding the same value, or -1)), " +
        "{handover: 'reason'} to get the screen back. A click is skipped when Jev is not sure the thing is there or not sure of its box. " +
        "Example: {goal:'click the green circles', app:'msedge.exe', see:{find:{green:'the green circle'}, check:{over:'a Game over message is shown'}}, " +
        "rules:[{when:'check.over > 0.8', do:[{stop:'game over'}]}, {when:'find.green.seen > 0.7', do:[{click:'green'}]}]}. " +
        "dry_run: true looks once and shows what the rules would do, without doing it: use it first. The screenshots go to Jev (TypeSafe) as well, so this exists only when the user allowed that.";

    private static readonly string[] FindFields = ["seen", "conf", "x", "y", "w", "h", "left", "top", "right", "bottom"];
    private static readonly HashSet<string> Reserved = new(["find", "check", "choose", "tick", "elapsed", "idle", "true", "false"], StringComparer.Ordinal);

    [GeneratedRegex("^[a-z][a-z0-9_]{0,30}$")]
    private static partial Regex NameSyntax();

    public static GamePolicyParse Parse(JsonElement json, PcGuard guard)
    {
        var errors = new List<string>();
        void Fail(string message) => errors.Add(message);

        if (json.ValueKind != JsonValueKind.Object)
            return new(null, ["The policy must be a JSON object."]);

        var goal = Str(json, "goal")?.Trim();
        if (string.IsNullOrEmpty(goal))
            Fail("goal: say in one sentence what the policy is for.");
        else if (goal.Length > 300)
            Fail("goal is longer than 300 characters.");

        var app = Str(json, "app")?.Trim();
        if (string.IsNullOrEmpty(app))
            Fail("app: the program of the window, such as msedge.exe.");
        else
        {
            app = PcGuard.Normalize(app);
            if (guard.IsDenied(app))
                Fail($"app: {app} cannot be controlled (terminals, password managers, system tools).");
        }

        var region = GameRegion.Whole;
        if (json.TryGetProperty("region", out var r) && r.ValueKind == JsonValueKind.Object)
        {
            double Edge(string n, double fallback) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : fallback;
            region = new GameRegion(Edge("left", 0), Edge("top", 0), Edge("right", 1), Edge("bottom", 1));
            if (region.Left < 0 || region.Top < 0 || region.Right > 1 || region.Bottom > 1 || region.Right - region.Left < 0.1 || region.Bottom - region.Top < 0.1)
                Fail("region: left, top, right, bottom are fractions of the window (0 to 1) and the part must be at least a tenth wide and high.");
        }

        var width = GamePolicy.DefaultWidth;
        if (json.TryGetProperty("width", out var w) && w.ValueKind == JsonValueKind.Number)
        {
            width = w.TryGetInt32(out var wi) ? wi : 0;
            if (width is < 320 or > 1568)
                Fail("width: the picture sent to Jev is 320 to 1568 pixels wide (smaller is faster, bigger sees small things).");
        }

        var finds = new List<GameFind>();
        var checks = new List<GameCheck>();
        var chooses = new List<GameChoose>();
        if (json.TryGetProperty("see", out var see) && see.ValueKind == JsonValueKind.Object)
        {
            if (see.TryGetProperty("find", out var f) && f.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in f.EnumerateObject())
                {
                    var description = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : p.Value.ValueKind == JsonValueKind.Object ? Str(p.Value, "look") : null;
                    var presence = !(p.Value.ValueKind == JsonValueKind.Object && p.Value.TryGetProperty("presence", out var pv) && pv.ValueKind == JsonValueKind.False);
                    if (!CheckName(p.Name, "find", Fail) || string.IsNullOrWhiteSpace(description))
                    {
                        if (string.IsNullOrWhiteSpace(description))
                            Fail($"find {p.Name}: describe what to find, such as \"the green circle\".");
                        continue;
                    }
                    if (description.Length > 300)
                        Fail($"find {p.Name}: the description is longer than 300 characters.");
                    if (PcGuard.IsRiskyText(description))
                        Fail($"find {p.Name}: \"{description}\" sounds like something that cannot be undone (delete, pay, send, close without saving…). Do that step yourself with PcClick, not from a policy that runs by itself.");
                    finds.Add(new GameFind(p.Name, description.Trim(), presence));
                }
            }
            if (see.TryGetProperty("check", out var c) && c.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in c.EnumerateObject())
                {
                    if (!CheckName(p.Name, "check", Fail))
                        continue;
                    if (p.Value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(p.Value.GetString()))
                        Fail($"check {p.Name}: write the statement to judge, such as \"the level is over\".");
                    else
                        checks.Add(new GameCheck(p.Name, p.Value.GetString()!.Trim()));
                }
            }
            if (see.TryGetProperty("choose", out var ch) && ch.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in ch.EnumerateObject())
                {
                    if (!CheckName(p.Name, "choose", Fail))
                        continue;
                    var question = p.Value.ValueKind == JsonValueKind.Object ? Str(p.Value, "question") : null;
                    if (string.IsNullOrWhiteSpace(question) || !p.Value.TryGetProperty("options", out var o) || o.ValueKind != JsonValueKind.Object)
                    {
                        Fail($"choose {p.Name}: needs a question and options ({{\"key\": \"what it means\"}}).");
                        continue;
                    }
                    var options = o.EnumerateObject().Select(e => new KeyValuePair<string, string>(e.Name, e.Value.ValueKind == JsonValueKind.String ? e.Value.GetString()! : "")).ToList();
                    if (options.Count is < 2 or > 20 || options.Any(x => !NameSyntax().IsMatch(x.Key.ToLowerInvariant().Replace('-', '_'))))
                        Fail($"choose {p.Name}: 2 to 20 options with short keys (letters, digits, _).");
                    else
                        chooses.Add(new GameChoose(p.Name, question.Trim(), options));
                }
            }
        }
        if (finds.Count > 16)
            Fail("At most 16 things to find.");
        if (checks.Count > 12)
            Fail("At most 12 checks.");
        if (chooses.Count > 6)
            Fail("At most 6 choices.");
        if (finds.Count * 2 + checks.Count + chooses.Count > GamePolicy.MaxQuestions)
            Fail($"Too many questions for one look (finds count twice, with the question whether they are there): at most {GamePolicy.MaxQuestions}.");
        if (finds.Count + checks.Count + chooses.Count == 0)
            Fail("see: ask something (find, check or choose), or there is nothing to react to.");

        var vars = new Dictionary<string, object>(StringComparer.Ordinal);
        if (json.TryGetProperty("vars", out var v) && v.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in v.EnumerateObject())
            {
                if (!NameSyntax().IsMatch(p.Name) || Reserved.Contains(p.Name))
                    Fail($"vars.{p.Name}: a name of letters, digits and _, not one of {string.Join(", ", Reserved)}.");
                else if (p.Value.ValueKind == JsonValueKind.Object)
                {
                    // A map: {} or with a few starting entries.
                    var map = new Dictionary<string, object>(StringComparer.Ordinal);
                    foreach (var e in p.Value.EnumerateObject())
                    {
                        if (e.Value.ValueKind == JsonValueKind.Number)
                            map[e.Name] = e.Value.GetDouble();
                        else if (e.Value.ValueKind == JsonValueKind.String)
                            map[e.Name] = e.Value.GetString()!;
                        else
                            Fail($"vars.{p.Name}.{e.Name}: a number or a text.");
                    }
                    vars[p.Name] = map;
                }
                else if (p.Value.ValueKind == JsonValueKind.Number)
                    vars[p.Name] = p.Value.GetDouble();
                else if (p.Value.ValueKind == JsonValueKind.String)
                    vars[p.Name] = p.Value.GetString()!;
                else if (p.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    vars[p.Name] = p.Value.GetBoolean();
                else
                    Fail($"vars.{p.Name}: a number, a text, true/false, or {{}} for a map.");
            }
        }
        if (vars.Count > 30)
            Fail("At most 30 variables.");

        bool KnownName(string name)
        {
            if (name is "tick" or "elapsed" or "idle" || vars.ContainsKey(name))
                return true;
            var parts = name.Split('.');
            return parts switch
            {
                ["find", var n, var field] => finds.Any(x => x.Name == n) && FindFields.Contains(field),
                ["check", var n] => checks.Any(x => x.Name == n),
                ["choose", var n] => chooses.Any(x => x.Name == n),
                ["choose", var n, "conf"] => chooses.Any(x => x.Name == n),
                _ => false,
            };
        }

        GameExpr? Expr(string text, string where)
        {
            try
            {
                var expr = GameExpr.Parse(text);
                foreach (var name in expr.Names.Where(n => !KnownName(n)))
                    Fail($"{where}: '{name}' is not something the policy knows. Known: find.NAME.{{{string.Join(",", FindFields)}}}, check.NAME, choose.NAME, choose.NAME.conf, tick, elapsed, idle, and your vars.");
                return expr;
            }
            catch (GameExprException ex)
            {
                Fail($"{where}: {ex.Message}");
                return null;
            }
        }

        var rules = new List<GameRule>();
        if (json.TryGetProperty("rules", out var rs) && rs.ValueKind == JsonValueKind.Array)
        {
            var number = 0;
            foreach (var rule in rs.EnumerateArray())
            {
                number++;
                var name = rule.ValueKind == JsonValueKind.Object ? Str(rule, "name") ?? $"r{number}" : $"r{number}";
                var where = $"rule {name}";
                if (rule.ValueKind != JsonValueKind.Object || Str(rule, "when") is not { } when)
                {
                    Fail($"{where}: needs \"when\" (an expression) and \"do\" (a list of actions).");
                    continue;
                }
                var cond = Expr(when, $"{where} when");
                var actions = new List<GameAction>();
                if (!rule.TryGetProperty("do", out var doList) || doList.ValueKind != JsonValueKind.Array || doList.GetArrayLength() is 0 or > 8)
                {
                    Fail($"{where}: \"do\" is a list of 1 to 8 actions.");
                    continue;
                }
                foreach (var a in doList.EnumerateArray())
                {
                    if (Action(a, where, finds, vars, Expr, Fail) is { } action)
                        actions.Add(action);
                }
                var cooldown = rule.TryGetProperty("cooldown", out var cd) && cd.ValueKind == JsonValueKind.Number && cd.TryGetInt32(out var cdi) ? Math.Clamp(cdi, 0, 1000) : 0;
                var once = rule.TryGetProperty("once", out var oc) && oc.ValueKind == JsonValueKind.True;
                if (cond is not null)
                    rules.Add(new GameRule(name, cond, actions, once, cooldown));
            }
        }
        if (rules.Count == 0)
            Fail("rules: at least one rule, {\"when\": \"find.x.seen > 0.7\", \"do\": [{\"click\": \"x\"}]}.");
        if (rules.Count > 30)
            Fail("At most 30 rules.");

        var limits = new GameLimits();
        if (json.TryGetProperty("limits", out var lim) && lim.ValueKind == JsonValueKind.Object)
        {
            int Int(string n, int fallback, int min, int max)
            {
                if (!lim.TryGetProperty(n, out var x) || x.ValueKind != JsonValueKind.Number || !x.TryGetInt32(out var i))
                    return fallback;
                if (i < min || i > max)
                    Fail($"limits.{n}: {min} to {max}.");
                return Math.Clamp(i, min, max);
            }
            var pipeline = lim.TryGetProperty("pipeline", out var pl) && pl.ValueKind == JsonValueKind.True;
            if (pipeline && vars.Count > 0)
                Fail("limits.pipeline: not with vars (a decision that depends on notes cannot be made from a picture that is one look old).");
            limits = new GameLimits(Int("max_ticks", 600, 1, 5000), Int("max_seconds", 300, 5, 1800), Int("stall_ticks", 8, 2, 100), Int("tick_ms", 0, 0, 5000), pipeline);
        }

        return errors.Count > 0 || goal is null || app is null
            ? new(null, errors)
            : new(new GamePolicy(goal, app, region, width, finds, checks, chooses, vars, rules, limits), []);
    }

    private static GameAction? Action(JsonElement a, string where, List<GameFind> finds, Dictionary<string, object> vars,
        Func<string, string, GameExpr?> expr, Action<string> fail)
    {
        if (a.ValueKind != JsonValueKind.Object)
        {
            fail($"{where}: an action is an object such as {{\"click\": \"x\"}}.");
            return null;
        }

        if (a.TryGetProperty("click", out var click))
        {
            var button = (Str(a, "button") ?? "left").ToLowerInvariant();
            if (button is not ("left" or "right" or "double"))
                fail($"{where}: button is left, right or double.");
            double Off(string n) => a.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? Math.Clamp(v.GetDouble(), -500, 500) : 0;
            if (click.ValueKind == JsonValueKind.String)
            {
                var target = click.GetString()!;
                if (!finds.Any(f => f.Name == target))
                    fail($"{where}: click \"{target}\": there is no find with that name.");
                return new ClickAction(target, null, null, button, Off("dx"), Off("dy"));
            }
            if (click.ValueKind == JsonValueKind.Object && Str(click, "x") is { } xs && Str(click, "y") is { } ys)
            {
                var x = expr(xs, $"{where} click x");
                var y = expr(ys, $"{where} click y");
                return x is null || y is null ? null : new ClickAction(null, x, y, button, Off("dx"), Off("dy"));
            }
            if (click.ValueKind == JsonValueKind.Object && click.TryGetProperty("x", out var xn) && click.TryGetProperty("y", out var yn))
            {
                var x = expr(xn.GetRawText().Trim('"'), $"{where} click x");
                var y = expr(yn.GetRawText().Trim('"'), $"{where} click y");
                return x is null || y is null ? null : new ClickAction(null, x, y, button, Off("dx"), Off("dy"));
            }
            fail($"{where}: click is the name of a find, or {{\"x\": \"expr\", \"y\": \"expr\"}} in window pixels.");
            return null;
        }
        if (a.TryGetProperty("drag", out var drag))
        {
            var from = drag.ValueKind == JsonValueKind.Object ? Str(drag, "from") : null;
            var to = drag.ValueKind == JsonValueKind.Object ? Str(drag, "to") : null;
            if (from is null || to is null || !finds.Any(f => f.Name == from) || !finds.Any(f => f.Name == to))
            {
                fail($"{where}: drag is {{\"from\": \"find\", \"to\": \"find\"}} with names of finds.");
                return null;
            }
            return new DragAction(from, to);
        }
        if (a.TryGetProperty("key", out var key))
        {
            var keys = key.ValueKind == JsonValueKind.String ? key.GetString()!.Trim().ToLowerInvariant() : "";
            if (PcGuard.IsValidHotkey(keys) && PcGuard.Risk(null, new DesktopAction(DesktopActionKind.Hotkey, 0, null, keys)) is { } risk)
            {
                fail($"{where}: key \"{keys}\": {risk}. Press it yourself with PcAct, not from a policy that runs by itself.");
                return null;
            }
            if (!PcGuard.IsValidHotkey(keys))
            {
                fail($"{where}: key \"{keys}\" is not a key Kairo presses (a letter, a digit, f1-f12, enter, tab, esc, space, backspace, delete, home, end, pageup, pagedown, up, down, left, right, with ctrl/alt/shift).");
                return null;
            }
            return new KeyAction(keys);
        }
        if (a.TryGetProperty("set", out var set))
        {
            var text = set.ValueKind == JsonValueKind.String ? set.GetString()! : "";
            var eq = text.IndexOf('=');
            var name = eq > 0 ? text[..eq].Trim() : "";
            if (eq <= 0 || eq + 1 < text.Length && text[eq + 1] == '=' || !vars.ContainsKey(name) || vars[name] is Dictionary<string, object>)
            {
                fail($"{where}: set is \"name = expression\" with a name declared under vars.");
                return null;
            }
            return expr(text[(eq + 1)..], $"{where} set {name}") is { } value ? new SetAction(name, value) : null;
        }
        if (a.TryGetProperty("put", out var put))
        {
            var map = put.ValueKind == JsonValueKind.Object ? Str(put, "map") : null;
            var keyText = put.ValueKind == JsonValueKind.Object ? Str(put, "key") : null;
            var value = put.ValueKind == JsonValueKind.Object ? Str(put, "value") : null;
            if (map is null || keyText is null || value is null || !vars.TryGetValue(map, out var m) || m is not Dictionary<string, object>)
            {
                fail($"{where}: put is {{\"map\": \"name\", \"key\": \"expr\", \"value\": \"expr\"}} with a map declared under vars as {{}}.");
                return null;
            }
            var k = expr(keyText, $"{where} put key");
            var v = expr(value, $"{where} put value");
            return k is null || v is null ? null : new PutAction(map, k, v);
        }
        if (a.TryGetProperty("del", out var del))
        {
            var map = del.ValueKind == JsonValueKind.Object ? Str(del, "map") : null;
            var keyText = del.ValueKind == JsonValueKind.Object ? Str(del, "key") : null;
            if (map is null || keyText is null || !vars.TryGetValue(map, out var m) || m is not Dictionary<string, object>)
            {
                fail($"{where}: del is {{\"map\": \"name\", \"key\": \"expr\"}} with a map declared under vars as {{}}.");
                return null;
            }
            return expr(keyText, $"{where} del key") is { } kd ? new DelAction(map, kd) : null;
        }
        if (a.TryGetProperty("clear", out var clear))
        {
            var map = clear.ValueKind == JsonValueKind.String ? clear.GetString()! : "";
            if (!vars.TryGetValue(map, out var m) || m is not Dictionary<string, object>)
            {
                fail($"{where}: clear is the name of a map declared under vars as {{}}.");
                return null;
            }
            return new ClearAction(map);
        }
        if (a.TryGetProperty("wait", out var wait))
        {
            if (wait.ValueKind != JsonValueKind.Number || !wait.TryGetInt32(out var ms) || ms is < 0 or > 5000)
            {
                fail($"{where}: wait is milliseconds, 0 to 5000.");
                return null;
            }
            return new WaitAction(ms);
        }
        if (a.TryGetProperty("stop", out var stop))
            return new StopAction(stop.ValueKind == JsonValueKind.String ? stop.GetString()! : "done", false);
        if (a.TryGetProperty("handover", out var handover))
            return new StopAction(handover.ValueKind == JsonValueKind.String ? handover.GetString()! : "needs the big model", true);

        fail($"{where}: an action is one of click, drag, key, set, put, del, clear, wait, stop, handover.");
        return null;
    }

    private static bool CheckName(string name, string kind, Action<string> fail)
    {
        if (NameSyntax().IsMatch(name))
            return true;
        fail($"{kind} {name}: a name is lowercase letters, digits and _, starting with a letter (at most 31 characters).");
        return false;
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
