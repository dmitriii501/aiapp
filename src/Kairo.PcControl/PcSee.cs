using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Kairo.Core.Decisions;
using Kairo.Core.Models;
using Kairo.Core.Tools;

namespace Kairo.PcControl;

/// <summary>Where one thing was found in a screenshot, in the pixels of that screenshot.</summary>
/// <param name="Visible">Probability that the thing is in the picture at all (a box is always returned, even for what is not there).</param>
/// <param name="Confidence">How sure the model is of the box; null when it gives none.</param>
/// <param name="Refined">The box comes from a second look at an enlarged part of the picture (small targets).</param>
internal sealed record Seen(string What, double Visible, double? Confidence, int Left, int Top, int Right, int Bottom, bool Refined)
{
    public int CentreX => (Left + Right) / 2;

    public int CentreY => (Top + Bottom) / 2;

    public int Width => Right - Left;

    public int Height => Bottom - Top;
}

internal sealed record SeeResult(Snapshot Shot, IReadOnlyList<Seen> Found, IReadOnlyList<(string Statement, double Probability)> Checks, long Tokens, long Milliseconds);

/// <summary>
/// Looking at a window through Jev: where is this, is that so. One screenshot, one request for everything asked (the picture is read
/// once), about half a second. A small target gets a second look at an enlarged piece of the picture, because a box around a 20 px
/// button is off by tens of pixels when the whole window is the picture. Pictures go to Jev (a second service besides the main model),
/// so this runs only when the user turned on <c>[pc] jev_vision</c>.
/// </summary>
internal static class Vision
{
    public const int MinSide = 280;
    public const int MaxSide = 1568;
    public const int MinPixels = 78_400;

    /// <summary>A box smaller than this share of the picture is looked at again, enlarged.</summary>
    public const double SmallWidth = 0.08;
    public const double SmallHeight = 0.06;

    public static async Task<SeeResult> SeeAsync(PcSession session, WindowInfo window, IReadOnlyList<string> find, IReadOnlyList<string> check, CancellationToken ct)
    {
        if (session.DecisionModel() is not { } model)
            throw new InvalidOperationException("The fast vision model is not set up (no Jev key). Use PcScreenshot and PcAct click_at, or PcObserve.");
        var shot = session.Desktop.Capture(window.Handle);
        session.NoteShot(window.Handle, shot);
        var p = shot.Picture;
        if (p.Width < MinSide || p.Height < MinSide || (long)p.Width * p.Height < MinPixels || p.Width > MaxSide || p.Height > MaxSide)
            throw new InvalidOperationException($"The window picture is {p.Width}x{p.Height}; Jev takes pictures of {MinSide} to {MaxSide} pixels on each side. Use PcScreenshot and look yourself.");

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var key = "shot-" + Guid.NewGuid().ToString("N")[..12];
        var questions = new Dictionary<string, DecisionQuestion>();
        for (var i = 0; i < find.Count; i++)
        {
            questions[$"f{i}"] = new BoxQuestion($"{find[i]} in `screen`");
            questions[$"v{i}"] = new YesNoQuestion($"Is there {find[i]} in `screen`?");
        }
        for (var i = 0; i < check.Count; i++)
            questions[$"c{i}"] = new YesNoQuestion($"Looking at `screen`: {check[i]}");

        var request = new DecisionRequest(
            DecisionState.Json($"{{\"screen\":\"{key}\"}}"),
            questions,
            new Dictionary<string, DecisionImage> { [key] = new("png", p.Png) });
        var answer = await model.DecideAsync(request, ct).ConfigureAwait(false);
        long tokens = answer.InputTokens;

        var found = new List<Seen>();
        for (var i = 0; i < find.Count; i++)
        {
            var box = answer.Box($"f{i}");
            var visible = answer.YesNo($"v{i}").Probability;
            found.Add(new Seen(find[i], visible, box.Confidence,
                (int)Math.Round(box.Left * p.Width), (int)Math.Round(box.Top * p.Height), (int)Math.Round(box.Right * p.Width), (int)Math.Round(box.Bottom * p.Height), false));
        }

        // Small things that are probably there get a second look, all at once.
        var again = Enumerable.Range(0, found.Count)
            .Where(i => found[i].Visible >= 0.5 && ((double)found[i].Width / p.Width < SmallWidth || (double)found[i].Height / p.Height < SmallHeight))
            .ToList();
        if (again.Count > 0)
        {
            var refined = await Task.WhenAll(again.Select(i => RefineAsync(model, p, found[i], ct))).ConfigureAwait(false);
            for (var k = 0; k < again.Count; k++)
            {
                if (refined[k] is { } better)
                {
                    found[again[k]] = better.Seen;
                    tokens += better.Tokens;
                }
            }
        }

        var checks = Enumerable.Range(0, check.Count).Select(i => (check[i], answer.YesNo($"c{i}").Probability)).ToList();
        return new SeeResult(shot, found, checks, tokens, clock.ElapsedMilliseconds);
    }

    /// <summary>Asks again about a piece of the picture around the first answer, shown at twice the size, and maps the new box back.</summary>
    private static async Task<(Seen Seen, long Tokens)?> RefineAsync(IDecisionModel model, Screenshot p, Seen first, CancellationToken ct)
    {
        try
        {
            var wPx = Math.Min(p.Width, Math.Max(360.0, p.Width * 0.22));
            var hPx = Math.Min(p.Height, wPx * p.Height / p.Width);
            var left = Math.Clamp(first.CentreX - wPx / 2, 0, p.Width - wPx);
            var top = Math.Clamp(first.CentreY - hPx / 2, 0, p.Height - hPx);
            const double scale = 2.0;
            byte[] png;
            using (var source = new Bitmap(new MemoryStream(p.Png)))
            using (var crop = new Bitmap((int)(wPx * scale), (int)(hPx * scale)))
            {
                using (var g = Graphics.FromImage(crop))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(source, new Rectangle(0, 0, crop.Width, crop.Height), new RectangleF((float)left, (float)top, (float)wPx, (float)hPx), GraphicsUnit.Pixel);
                }
                using var stream = new MemoryStream();
                crop.Save(stream, ImageFormat.Png);
                png = stream.ToArray();
            }
            if (png.Length == 0 || (int)(wPx * scale) < MinSide || (int)(hPx * scale) < MinSide)
                return null;

            var key = "crop-" + Guid.NewGuid().ToString("N")[..12];
            var request = new DecisionRequest(
                DecisionState.Json($"{{\"screen\":\"{key}\"}}"),
                new Dictionary<string, DecisionQuestion> { ["f"] = new BoxQuestion($"{first.What} in `screen`") },
                new Dictionary<string, DecisionImage> { [key] = new("png", png) });
            var answer = await model.DecideAsync(request, ct).ConfigureAwait(false);
            var box = answer.Box("f");
            // Trust the second look when it is at least as sure as the first, or sure on its own.
            if (box.Confidence is { } c && first.Confidence is { } f && c < f && c < 0.6)
                return null;
            return (new Seen(first.What, first.Visible, box.Confidence ?? first.Confidence,
                (int)Math.Round(left + box.Left * wPx), (int)Math.Round(top + box.Top * hPx), (int)Math.Round(left + box.Right * wPx), (int)Math.Round(top + box.Bottom * hPx), true), answer.InputTokens);
        }
        catch (DecisionException)
        {
            return null; // the first answer still stands
        }
        catch (ArgumentException)
        {
            return null; // the picture could not be cut
        }
    }

    /// <summary>One line per thing: where it is (the centre is what to click), how sure Jev is.</summary>
    public static string Describe(SeeResult result, int lookNumber)
    {
        var sb = new StringBuilder();
        var p = result.Shot.Picture;
        sb.Append("Screenshot ").Append(lookNumber).Append(": ").Append(p.Width).Append('x').Append(p.Height).Append(" px (use look=").Append(lookNumber)
          .Append(" with click_at, double_click_at, right_click_at, drag_at, drag_path). Jev answered in ").Append(result.Milliseconds).Append(" ms.\n");
        foreach (var s in result.Found)
        {
            var visible = s.Visible >= 0.5;
            sb.Append(visible ? "  FOUND " : "  NOT SEEN ").Append('"').Append(s.What).Append("\": ");
            if (visible)
                sb.Append("click at (").Append(s.CentreX).Append(", ").Append(s.CentreY).Append("), box (").Append(s.Left).Append(", ").Append(s.Top).Append(")-(").Append(s.Right).Append(", ").Append(s.Bottom).Append(')');
            else
                sb.Append("probably not on the screen (").Append(Pct(s.Visible)).Append(" it is); the box is a guess, do not click it");
            sb.Append("; sure it is there ").Append(Pct(s.Visible));
            if (s.Confidence is { } c)
                sb.Append(", sure of the box ").Append(Pct(c));
            if (s.Refined)
                sb.Append(" (second look at an enlarged part)");
            sb.Append('\n');
        }
        foreach (var (statement, probability) in result.Checks)
            sb.Append("  CHECK \"").Append(statement).Append("\": ").Append(probability >= 0.9 ? "yes" : probability <= 0.1 ? "no" : "not clear").Append(" (").Append(Pct(probability)).Append(")\n");
        sb.Append("A box with low confidence (under 60%) or a small target can be off by tens of pixels: look at the result after the click.");
        return sb.ToString();
    }

    private static string Pct(double p) => (p * 100).ToString("0", CultureInfo.InvariantCulture) + "%";
}

/// <summary>
/// Asks the fast vision model (Jev) where things are on a window and whether statements about it are true. The main model does not need
/// to see a picture at all: it gets pixel coordinates it can pass to PcAct click_at / drag_path, or to PcClick.
/// </summary>
internal sealed class PcSeeTool(PcSession session) : ITool
{
    public ToolSpec Spec { get; } = new(
        "PcSee",
        "Looks at a window with the fast vision model (Jev) and answers in about half a second, without sending you a picture: where things are " +
        "(find: short descriptions such as 'the Save button' or 'the red circle on the canvas'), and whether statements are true (check: " +
        "'a dialog is open', 'the score is 3'). Use it where the element list shows nothing (a game, a canvas, a custom-drawn window). " +
        "Returns the pixel centre of each thing to click and how sure it is; pass the numbers to PcAct click_at / drag_path with look = the screenshot " +
        "number, or use PcClick. Ask for everything you need in one call. Small targets are less exact: look at the result of the click. " +
        "The screenshot goes to Jev (TypeSafe) as well, so it is only available when the user turned that on.",
        Schema());

    private static JsonElement Schema() => JsonDocument.Parse(
        """
        {"type":"object","properties":{
          "find":{"type":"array","description":"Things to locate, each a short description in English or the program's language (up to 12).","items":{"type":"string"}},
          "check":{"type":"array","description":"Statements to judge from the picture, answered as a probability (up to 12).","items":{"type":"string"}},
          "window":{"type":"string","description":"Window id or program name. Default: the allowed window in front."}},
         "additionalProperties":false}
        """).RootElement.Clone();

    internal static (IReadOnlyList<string> Find, IReadOnlyList<string> Check) Parse(JsonElement input)
    {
        List<string> Read(string name)
        {
            var list = new List<string>();
            if (input.ValueKind == JsonValueKind.Object && input.TryGetProperty(name, out var v))
            {
                if (v.ValueKind == JsonValueKind.Array)
                    list.AddRange(v.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!.Trim()).Where(s => s.Length > 0));
                else if (v.ValueKind == JsonValueKind.String && v.GetString()!.Trim() is { Length: > 0 } one)
                    list.Add(one);
            }
            if (list.Count > 12)
                throw new ToolInputException($"At most 12 entries in {name}; ask in several calls.");
            if (list.Any(s => s.Length > 300))
                throw new ToolInputException($"An entry of {name} is longer than 300 characters.");
            return list;
        }
        var find = Read("find");
        var check = Read("check");
        if (find.Count + check.Count == 0)
            throw new ToolInputException("Give something to find or to check.");
        return (find, check);
    }

    public ToolAssessment Assess(JsonElement input, ToolContext ctx)
    {
        var (find, check) = Parse(input);
        var window = session.Resolve(ToolInput.OptionalString(input, "window"));
        session.EnsureIdle("PcSee");
        return session.Assess("PcSee", window.Process, $"{window.Process}: a screenshot goes to Jev; find {find.Count}, check {check.Count}");
    }

    public async Task<ToolResult> InvokeAsync(JsonElement input, ToolContext ctx, CancellationToken ct)
    {
        var (find, check) = Parse(input);
        var window = session.Resolve(ToolInput.OptionalString(input, "window"));
        using var banner = session.Begin();
        try
        {
            var result = await Vision.SeeAsync(session, window, find, check, ct).ConfigureAwait(false);
            return new ToolResult(Vision.Describe(result, result.Shot.Id));
        }
        catch (InvalidOperationException ex)
        {
            return ToolResult.Error(ex.Message);
        }
        catch (DecisionException ex)
        {
            return ToolResult.Error($"The fast vision model could not answer ({ex.Message}). Use PcScreenshot and look yourself.");
        }
    }
}

/// <summary>Clicks what the model describes in words: Jev finds it on the screenshot, Kairo clicks the centre of the box.</summary>
internal sealed class PcClickTool(PcSession session) : ITool
{
    /// <summary>Below this the box is not clicked: it is more likely wrong than right (measured: wrong answers sat at 0.3 to 0.4, the best at 0.9).</summary>
    public const double MinConfidence = 0.5;

    public ToolSpec Spec { get; } = new(
        "PcClick",
        "Clicks a thing you describe in words, where the element list does not show it (a game, a canvas, a custom-drawn window): the fast vision " +
        "model (Jev) finds it on the screenshot in about half a second and Kairo clicks its centre, then returns the new picture or look. " +
        "target: a short description such as 'the OK button' or 'the second card in the top row'. It refuses to click when the thing is probably " +
        "not on the screen or the box is doubtful, and says so. The screenshot goes to Jev (TypeSafe) as well, so it needs the user's setting.",
        ToolInput.Schema(
            ("target", "string", "What to click, in a few words.", true),
            ("button", "string", "left (default), right or double.", false),
            ("wait", "number", "Seconds to wait for the window to change after the click (0-10). Default 1.", false),
            ("window", "string", "Window id or program name. Default: the allowed window in front.", false)));

    public ToolAssessment Assess(JsonElement input, ToolContext ctx)
    {
        var target = ToolInput.RequiredString(input, "target").Trim();
        Button(input);
        var window = session.Resolve(ToolInput.OptionalString(input, "window"));
        session.EnsureIdle("PcClick");
        return session.Assess("PcClick", window.Process, $"{window.Process}: click \"{(target.Length > 80 ? target[..80] + "…" : target)}\" (a screenshot goes to Jev)");
    }

    private static DesktopActionKind Button(JsonElement input) => (ToolInput.OptionalString(input, "button") ?? "left").Trim().ToLowerInvariant() switch
    {
        "left" or "" => DesktopActionKind.ClickAt,
        "right" => DesktopActionKind.RightClickAt,
        "double" => DesktopActionKind.DoubleClickAt,
        var other => throw new ToolInputException($"Unknown button '{other}': left, right or double."),
    };

    public async Task<ToolResult> InvokeAsync(JsonElement input, ToolContext ctx, CancellationToken ct)
    {
        var target = ToolInput.RequiredString(input, "target").Trim();
        var kind = Button(input);
        var window = session.Resolve(ToolInput.OptionalString(input, "window"));
        using var banner = session.Begin();
        if (session.Desktop.StopRequested())
            return ToolResult.Error("The user pressed the stop key (Pause). Nothing was done.");

        SeeResult seen;
        try
        {
            seen = await Vision.SeeAsync(session, window, [target], [], ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            return ToolResult.Error(ex.Message);
        }
        catch (DecisionException ex)
        {
            return ToolResult.Error($"The fast vision model could not answer ({ex.Message}). Use PcScreenshot and PcAct click_at yourself.");
        }

        var found = seen.Found[0];
        if (found.Visible < 0.5)
            return ToolResult.Error($"Not clicked: \"{target}\" is probably not on the screen ({(found.Visible * 100):0}% sure it is). Look with PcScreenshot or PcSee.");
        if (found.Confidence is { } c && c < MinConfidence)
            return ToolResult.Error($"Not clicked: the box for \"{target}\" is doubtful ({c * 100:0}% sure, at ({found.CentreX}, {found.CentreY})). Describe it more exactly, or click_at yourself after PcScreenshot.");

        var action = new DesktopAction(kind, seen.Shot.Id, null, null, found.CentreX, found.CentreY);
        var outcome = session.Desktop.Perform(window.Handle, action);
        if (!outcome.Ok)
            return ToolResult.Error($"Not done: {outcome.Message}. Look again with PcObserve.");
        var how = found.Confidence is { } conf ? $"{conf * 100:0}% sure of the box{(found.Refined ? ", second look" : "")}" : "no confidence given";
        var head = $"Clicked \"{target}\" at ({found.CentreX}, {found.CentreY}) ({how}; Jev took {seen.Milliseconds} ms): {outcome.Message}.\n\n";
        return new PcActTool(session).Finish(window, action, head, ToolInput.OptionalNumber(input, "wait"));
    }
}
