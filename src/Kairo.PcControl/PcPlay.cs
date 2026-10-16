using System.Text.Json;
using Kairo.Core.Decisions;
using Kairo.Core.Models;
using Kairo.Core.Tools;

namespace Kairo.PcControl;

/// <summary>
/// Plays a game, or reacts to a window the element list cannot read, without the big model in the loop. It writes a policy once; Kairo
/// looks at the window every tick (one request to Jev), the rules decide, and the clicks or keys follow, one or two ticks a second.
/// </summary>
internal sealed class PcPlayTool(PcSession session) : ITool
{
    public ToolSpec Spec { get; } = new(
        "PcPlay",
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
        "{handover: 'reason'} to get the screen back. A click is skipped when Jev is not sure the thing is there or not sure of its box. " +
        "Example: {goal:'click the green circles', app:'msedge.exe', see:{find:{green:'the green circle'}, check:{over:'a Game over message is shown'}}, " +
        "rules:[{when:'check.over > 0.8', do:[{stop:'game over'}]}, {when:'find.green.seen > 0.7', do:[{click:'green'}]}]}. " +
        "dry_run: true looks once and shows what the rules would do, without doing it: use it first. The screenshots go to Jev (TypeSafe) as well, so this exists only when the user allowed that.",
        Schema());

    private static JsonElement Schema() => JsonDocument.Parse(
        """
        {"type":"object","properties":{
          "policy":{"type":"object","description":"The game policy, as described above."},
          "dry_run":{"type":"boolean","description":"Look once and show what the rules would do, without doing it."},
          "window":{"type":"string","description":"Window id or program name. Default: the policy's app."}},
         "required":["policy"],"additionalProperties":false}
        """).RootElement.Clone();

    private GamePolicy ParsePolicy(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object || !input.TryGetProperty("policy", out var raw))
            throw new ToolInputException("Missing required object 'policy'.");
        // Some models send the policy as a string of JSON.
        JsonDocument? owned = null;
        try
        {
            var json = raw;
            if (raw.ValueKind == JsonValueKind.String)
            {
                try
                {
                    owned = JsonDocument.Parse(raw.GetString()!);
                    json = owned.RootElement;
                }
                catch (JsonException)
                {
                    throw new ToolInputException("policy is not valid JSON.");
                }
            }
            var parsed = GamePolicyParser.Parse(json, session.Guard);
            if (parsed.Policy is not { } policy)
                throw new ToolInputException("The policy has problems; fix all of them:\n- " + string.Join("\n- ", parsed.Errors));
            return policy;
        }
        finally
        {
            owned?.Dispose();
        }
    }

    private WindowInfo Window(JsonElement input, GamePolicy policy) => session.Resolve(ToolInput.OptionalString(input, "window") ?? policy.App);

    public ToolAssessment Assess(JsonElement input, ToolContext ctx)
    {
        var policy = ParsePolicy(input);
        var window = Window(input, policy);
        session.EnsureIdle("PcPlay");
        var assessment = session.Assess("PcPlay", window.Process, $"{window.Process}: {(policy.Goal.Length > 100 ? policy.Goal[..100] + "…" : policy.Goal)} (a screenshot goes to Jev every tick; up to {policy.Limits.MaxSeconds} s)");
        return assessment with { Detail = policy.Describe() };
    }

    public async Task<ToolResult> InvokeAsync(JsonElement input, ToolContext ctx, CancellationToken ct)
    {
        var policy = ParsePolicy(input);
        var window = Window(input, policy);
        if (session.DecisionModel() is not { } model)
            return ToolResult.Error("The fast vision model is not set up (no Jev key). Play move by move with PcScreenshot and click_at.");
        using var banner = session.Begin();
        GameResult result;
        try
        {
            result = await GameRunner.RunAsync(session.Desktop, session.Guard, model, policy, window.Handle, new GameRunOptions(ToolInput.OptionalBool(input, "dry_run")), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        // The last picture stays valid for click_at, so a handover can go on from it.
        if (result.Last is { } last)
            session.NoteShot(window.Handle, last.Shot);
        var title = session.Desktop.Window(window.Handle)?.Title;
        var text = GameRunner.Describe(result) + (title is null ? "" : $"\nWindow title now: {title}");
        return new ToolResult(text, IsError: result.Status == GameStatus.Failed);
    }
}
