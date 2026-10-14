using System.Text.Json;
using Kairo.Core.Config;
using Kairo.Core.Tests.Fakes;
using Kairo.Core.Tools;
using Kairo.PcControl;
using static Kairo.Core.Tests.Fakes.FakeDesktop;

namespace Kairo.Core.Tests;

public sealed class StrokesTests
{
    [Fact]
    public void An_ellipse_is_closed_and_stays_inside_its_box()
    {
        var points = Strokes.Ellipse(100, 200, 300, 260);

        Assert.Equal(points[0], points[^1]);
        Assert.All(points, p => Assert.True(p.X is >= 100 and <= 300 && p.Y is >= 200 and <= 260));
        Assert.Contains((200, 200), points); // the top
        Assert.Contains((300, 230), points); // the right side
    }

    [Fact]
    public void A_rectangle_returns_to_its_first_corner()
    {
        Assert.Equal([(1, 2), (9, 2), (9, 8), (1, 8), (1, 2)], Strokes.Rectangle(1, 2, 9, 8));
    }

    [Fact]
    public void A_smooth_curve_passes_through_the_given_points()
    {
        (int X, int Y)[] given = [(0, 0), (100, 80), (200, 0), (300, 80)];

        var curve = Strokes.Smooth(given);

        Assert.All(given, g => Assert.Contains(g, curve));
        Assert.Equal(given[0], curve[0]);
        Assert.Equal(given[^1], curve[^1]);
        Assert.True(curve.Count > given.Length * 4);
    }

    [Fact]
    public void A_closed_curve_has_no_corner_where_it_closes()
    {
        (int X, int Y)[] square = [(0, 0), (100, 0), (100, 100), (0, 100), (0, 0)];

        var curve = Strokes.Smooth(square);

        Assert.Equal(curve[0], curve[^1]);
        // Rounded: the curve does not pass through the corner (100, 100) as a corner would; it is a loop through the four points.
        Assert.Contains((100, 100), curve);
    }
}

public sealed class LiveClipboardTests
{
    /// <summary>Against the real Windows clipboard; it overwrites what is on it, so it runs only when KAIRO_LIVE_CLIPBOARD=1.</summary>
    [Fact]
    public void Text_and_a_picture_make_the_round_trip_through_the_real_clipboard()
    {
        if (Environment.GetEnvironmentVariable("KAIRO_LIVE_CLIPBOARD") != "1")
            return;
        var before = WinClipboard.ReadText();
        try
        {
            WinClipboard.WriteText("kairo-клип-тест");
            Assert.Equal("kairo-клип-тест", WinClipboard.ReadText());
            Assert.True(WinClipboard.HasText);

            var logo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "assets", "logo", "kairo.png"));
            var (w, h) = WinClipboard.WriteImage(logo);
            Assert.Equal((512, 512), (w, h));
            Assert.True(WinClipboard.HasImage);
        }
        finally
        {
            if (before is not null)
                WinClipboard.WriteText(before);
        }
    }
}

public sealed class PcExtraToolsTests : IDisposable
{
    private sealed class FakeClipboard : IClipboard
    {
        public string? Text { get; set; }
        public string? ImagePath { get; private set; }
        public bool HasText => Text is not null;
        public bool HasImage => ImagePath is not null;
        public string? ReadText() => Text;
        public void WriteText(string text) => Text = text;

        public (int Width, int Height) WriteImage(string path)
        {
            ImagePath = path;
            return (640, 480);
        }
    }

    private readonly FakeDesktop _desktop = new();
    private readonly FakeClipboard _clipboard = new();
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private IReadOnlyList<ITool> Tools(bool vision = true, bool autonomous = false)
    {
        var config = Path.Combine(_dir.Path, "cfg");
        Directory.CreateDirectory(config);
        File.WriteAllText(Path.Combine(config, "config.toml"), $"[pc]\nenabled = true\napps = [\"notepad.exe\"]\nvision = {(vision ? "true" : "false")}\nautonomous = {(autonomous ? "true" : "false")}");
        return PcTools.For(KairoConfig.Load(config, config, includeProject: false), () => null, _desktop, clipboard: _clipboard);
    }

    private static ITool Tool(IReadOnlyList<ITool> tools, string name) => tools.Single(t => t.Spec.Name == name);

    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);

    private ToolContext Ctx => new(_dir.Path);

    [Fact]
    public async Task A_right_click_names_the_screenshot_and_returns_the_new_picture()
    {
        var tools = Tools();
        await Tool(tools, "PcScreenshot").InvokeAsync(Json(new { }), Ctx, default);

        var result = await Tool(tools, "PcAct").InvokeAsync(Json(new { action = "right_click_at", x = 40, y = 30, look = 1 }), Ctx, default);

        Assert.False(result.IsError, result.Content);
        var action = Assert.Single(_desktop.Performed);
        Assert.Equal((DesktopActionKind.RightClickAt, 1, 40, 30), (action.Kind, action.ObservationVersion, action.X, action.Y));
        Assert.Contains("Screenshot 2", result.Content);
    }

    [Fact]
    public async Task A_shape_becomes_a_closed_stroke_inside_its_box()
    {
        var tools = Tools();
        await Tool(tools, "PcScreenshot").InvokeAsync(Json(new { }), Ctx, default);

        var result = await Tool(tools, "PcAct").InvokeAsync(Json(new { action = "drag_path", look = 1, shape = "ellipse", x = 20, y = 20, x2 = 80, y2 = 50 }), Ctx, default);

        Assert.False(result.IsError, result.Content);
        var path = Assert.Single(_desktop.Performed).Path!;
        Assert.Equal(49, path.Count);
        Assert.Equal(path[0], path[^1]);
    }

    [Fact]
    public async Task Smooth_points_become_a_longer_curve_and_an_unknown_shape_is_explained()
    {
        var tools = Tools();
        await Tool(tools, "PcScreenshot").InvokeAsync(Json(new { }), Ctx, default);

        await Tool(tools, "PcAct").InvokeAsync(Json(new { action = "drag_path", look = 1, points = "10,10 40,60 70,10 100,60", smooth = true }), Ctx, default);

        Assert.True(_desktop.Performed.Single().Path!.Count > 20);
        var ex = Assert.Throws<ToolInputException>(() => Tool(tools, "PcAct").Assess(Json(new { action = "drag_path", look = 1, shape = "star", x = 1, y = 1, x2 = 5, y2 = 5 }), Ctx));
        Assert.Contains("Unknown shape", ex.Message);
    }

    [Fact]
    public async Task A_batch_does_its_steps_in_order_and_returns_one_look()
    {
        _desktop.Elements = [El("e1", "button", "1", UiCapability.Invoke), El("e2", "button", "plus", UiCapability.Invoke), El("e3", "button", "equals", UiCapability.Invoke)];
        var tools = Tools(vision: false);

        var result = await Tool(tools, "PcBatch").InvokeAsync(Json(new { steps = new object[] { new { action = "invoke", element = "e1" }, new { action = "invoke", element = "e2" }, new { action = "hotkey", text = "enter" } } }), Ctx, default);

        Assert.False(result.IsError, result.Content);
        Assert.Equal([DesktopActionKind.Invoke, DesktopActionKind.Invoke, DesktopActionKind.Hotkey], _desktop.Performed.Select(a => a.Kind));
        Assert.Contains("3 steps done", result.Content);
        Assert.Contains("1. Invoke e1", result.Content);
        Assert.Contains("Look ", result.Content);
    }

    [Fact]
    public async Task A_batch_stops_at_the_first_failure_and_says_what_was_not_done()
    {
        _desktop.Elements = [El("e1", "button", "1", UiCapability.Invoke)];
        var tools = Tools(vision: false);

        var result = await Tool(tools, "PcBatch").InvokeAsync(Json(new { steps = new object[] { new { action = "invoke", element = "e1" }, new { action = "invoke", element = "e9" }, new { action = "invoke", element = "e1" } } }), Ctx, default);

        Assert.True(result.IsError);
        Assert.Contains("Step 2", result.Content);
        Assert.Contains("Steps 2-3 were not done", result.Content);
        Assert.Equal(2, _desktop.Performed.Count); // the third was never tried
    }

    [Fact]
    public void A_batch_with_a_bad_step_is_refused_before_anything_is_done()
    {
        _desktop.Elements = [El("e1", "button", "1", UiCapability.Invoke)];
        var tools = Tools(vision: false);

        var ex = Assert.Throws<ToolInputException>(() => Tool(tools, "PcBatch").Assess(Json(new { steps = new object[] { new { action = "invoke", element = "e1" }, new { action = "click_at", x = 1, y = 1, look = 1 } } }), Ctx));

        Assert.Contains("Step 2", ex.Message);
        Assert.Empty(_desktop.Performed);
    }

    [Fact]
    public void A_batch_asks_like_PcAct_and_names_its_steps()
    {
        _desktop.Elements = [El("e1", "button", "1", UiCapability.Invoke)];

        var asking = Tool(Tools(vision: false), "PcBatch").Assess(Json(new { steps = new object[] { new { action = "invoke", element = "e1" } } }), Ctx);
        var autonomous = Tool(Tools(vision: false, autonomous: true), "PcBatch").Assess(Json(new { steps = new object[] { new { action = "invoke", element = "e1" } } }), Ctx);

        Assert.Equal(RiskLevel.System, asking.Risk);
        Assert.Equal(RiskLevel.Exec, autonomous.Risk);
        Assert.Contains("1 steps", asking.Summary);
        Assert.Contains("Invoke e1", asking.Summary);
    }

    [Fact]
    public async Task Reading_the_clipboard_always_asks_even_when_pc_control_is_autonomous()
    {
        _clipboard.Text = "hunter2";
        var tool = Tool(Tools(autonomous: true), "PcClipboard");

        var read = tool.Assess(Json(new { action = "read_text" }), Ctx);
        var write = tool.Assess(Json(new { action = "write_text", text = "hi" }), Ctx);
        var result = await tool.InvokeAsync(Json(new { action = "read_text" }), Ctx, default);

        Assert.Equal(RiskLevel.System, read.Risk);
        Assert.Equal(RiskLevel.Exec, write.Risk);
        Assert.Contains("hunter2", result.Content);
        Assert.Contains("untrusted", result.Content);
    }

    [Fact]
    public async Task Text_is_put_on_the_clipboard()
    {
        var result = await Tool(Tools(), "PcClipboard").InvokeAsync(Json(new { action = "write_text", text = "hello" }), Ctx, default);

        Assert.False(result.IsError, result.Content);
        Assert.Equal("hello", _clipboard.Text);
        Assert.Contains("ctrl+v", result.Content);
    }

    [Fact]
    public async Task A_picture_from_the_working_directory_goes_on_the_clipboard_and_one_from_elsewhere_does_not()
    {
        var inside = _dir.File("kairo-downloads\\pic.png", "x");
        var tool = Tool(Tools(), "PcClipboard");

        var ok = await tool.InvokeAsync(Json(new { action = "set_image", path = "kairo-downloads/pic.png" }), Ctx, default);

        Assert.False(ok.IsError, ok.Content);
        Assert.Equal(inside, _clipboard.ImagePath, ignoreCase: true);
        Assert.Contains("640x480", ok.Content);

        var elsewhere = Path.Combine(Path.GetTempPath(), "kairo-outside-" + Guid.NewGuid().ToString("N")[..6] + ".png");
        File.WriteAllText(elsewhere, "x");
        try
        {
            var ex = Assert.Throws<ToolInputException>(() => tool.Assess(Json(new { action = "set_image", path = elsewhere }), Ctx));
            Assert.Contains("not one the clipboard takes pictures from", ex.Message);
        }
        finally
        {
            File.Delete(elsewhere);
        }
    }

    [Fact]
    public void Only_pictures_can_be_put_on_the_clipboard()
    {
        _dir.File("secret.txt", "x");

        var ex = Assert.Throws<ToolInputException>(() => Tool(Tools(), "PcClipboard").Assess(Json(new { action = "set_image", path = "secret.txt" }), Ctx));

        Assert.Contains("pictures", ex.Message);
    }
}
