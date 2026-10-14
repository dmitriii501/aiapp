using System.Text.Json;
using Kairo.Core.Config;
using Kairo.Core.Models;
using Kairo.Core.Tests.Fakes;
using Kairo.Core.Tools;
using Kairo.PcControl;
using static Kairo.Core.Tests.Fakes.FakeDesktop;

namespace Kairo.Core.Tests;

public sealed class PcVisionTests
{
    private readonly FakeDesktop _desktop = new();

    private static KairoConfig Config(string pc)
    {
        var dir = Directory.CreateTempSubdirectory("kairo-vis-").FullName;
        File.WriteAllText(Path.Combine(dir, "config.toml"), "[pc]\nenabled = true\napps = [\"notepad.exe\"]\n" + pc);
        return KairoConfig.Load(dir, dir, includeProject: false);
    }

    private IReadOnlyList<ITool> Tools(bool vision) => PcTools.For(Config(vision ? "vision = true" : ""), () => null, _desktop);

    private static ITool Tool(IReadOnlyList<ITool> tools, string name) => tools.Single(t => t.Spec.Name == name);

    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);

    private ToolContext Ctx => new(Path.GetTempPath());

    [Fact]
    public void The_screenshot_tool_and_the_pixel_actions_exist_only_with_vision_on()
    {
        Assert.DoesNotContain(Tools(false), t => t.Spec.Name == "PcScreenshot");
        Assert.Contains(Tools(true), t => t.Spec.Name == "PcScreenshot");

        var ex = Assert.Throws<ToolInputException>(() => Tool(Tools(false), "PcAct").Assess(Json(new { action = "click_at", x = 5, y = 5, look = 1 }), Ctx));
        Assert.Contains("Unknown action", ex.Message);
        Assert.DoesNotContain("click_at", ex.Message[ex.Message.IndexOf("Use ", StringComparison.Ordinal)..]); // not offered as a choice
        Assert.NotNull(Tool(Tools(true), "PcAct").Assess(Json(new { action = "click_at", x = 5, y = 5, look = 1 }), Ctx));
    }

    [Fact]
    public void A_project_cannot_turn_vision_on()
    {
        var user = Directory.CreateTempSubdirectory("kairo-vis-user-").FullName;
        var project = Directory.CreateTempSubdirectory("kairo-vis-proj-").FullName;
        File.WriteAllText(Path.Combine(user, "config.toml"), "[pc]\nenabled = true\napps = [\"notepad.exe\"]");
        Directory.CreateDirectory(Path.Combine(project, ".kairo"));
        File.WriteAllText(Path.Combine(project, ".kairo", "config.toml"), "[pc]\nvision = true\noverlay = false");

        var config = KairoConfig.Load(project, user);

        Assert.Null(config.PcVision);
        Assert.Null(config.PcOverlay);
        Assert.Null(config.PcAutonomous);
    }

    [Fact]
    public async Task A_screenshot_comes_back_as_a_picture_with_its_number_and_how_to_use_it()
    {
        var result = await Tool(Tools(true), "PcScreenshot").InvokeAsync(Json(new { }), Ctx, default);

        Assert.False(result.IsError, result.Content);
        var image = Assert.Single(result.Images!);
        Assert.Equal("image/png", image.MediaType);
        Assert.Equal(Convert.ToBase64String([137, 80, 78, 71, 13, 10, 26, 10]), image.Base64);
        Assert.Contains("Screenshot 1 of w10", result.Content);
        Assert.Contains("200x100 px", result.Content);
        Assert.Contains("shrunk to 0.50", result.Content);
        Assert.Contains("look=1", result.Content);
    }

    [Fact]
    public async Task A_click_at_a_point_names_the_screenshot_and_returns_the_new_picture_and_the_tree()
    {
        _desktop.Elements = [El("e1", "pane", "Canvas")];
        var tools = Tools(true);
        await Tool(tools, "PcScreenshot").InvokeAsync(Json(new { }), Ctx, default);

        var result = await Tool(tools, "PcAct").InvokeAsync(Json(new { action = "click_at", x = 40, y = 30, look = 1 }), Ctx, default);

        Assert.False(result.IsError, result.Content);
        var action = Assert.Single(_desktop.Performed);
        Assert.Equal((DesktopActionKind.ClickAt, 1, 40, 30), (action.Kind, action.ObservationVersion, action.X, action.Y));
        Assert.Single(result.Images!);
        Assert.Contains("Screenshot 2", result.Content); // the effect of the click is a new picture
        Assert.Contains("Look ", result.Content); // and the tree, when there is one
        Assert.Equal(2, _desktop.Captures);
    }

    [Fact]
    public async Task A_click_on_an_old_screenshot_is_refused_with_advice()
    {
        var tools = Tools(true);
        await Tool(tools, "PcScreenshot").InvokeAsync(Json(new { }), Ctx, default);
        await Tool(tools, "PcScreenshot").InvokeAsync(Json(new { }), Ctx, default);

        var result = await Tool(tools, "PcAct").InvokeAsync(Json(new { action = "double_click_at", x = 1, y = 1, look = 1 }), Ctx, default);

        Assert.True(result.IsError);
        Assert.Contains("take a new one", result.Content);
        Assert.Null(result.Images);
    }

    [Theory]
    [InlineData("{\"action\":\"click_at\",\"x\":1,\"y\":1}", "look is required")]
    [InlineData("{\"action\":\"click_at\",\"look\":1,\"y\":1}", "needs x and y")]
    [InlineData("{\"action\":\"type_focused\"}", "needs text")]
    public void Bad_pixel_actions_are_explained(string json, string message)
    {
        var ex = Assert.Throws<ToolInputException>(() => Tool(Tools(true), "PcAct").Assess(JsonDocument.Parse(json).RootElement, Ctx));

        Assert.Contains(message, ex.Message);
    }

    [Fact]
    public async Task Typing_into_the_focused_element_needs_no_look_and_shows_the_result()
    {
        var result = await Tool(Tools(true), "PcAct").InvokeAsync(Json(new { action = "type_focused", text = "hello" }), Ctx, default);

        Assert.False(result.IsError, result.Content);
        Assert.Equal((DesktopActionKind.TypeFocused, "hello"), (_desktop.Performed[0].Kind, _desktop.Performed[0].Text));
        Assert.Single(result.Images!);
    }

    [Fact]
    public async Task A_window_with_no_tree_is_told_to_use_pictures_only_when_they_are_on()
    {
        _desktop.Elements = [];

        var withVision = await Tool(Tools(true), "PcObserve").InvokeAsync(Json(new { }), Ctx, default);
        var without = await Tool(Tools(false), "PcObserve").InvokeAsync(Json(new { }), Ctx, default);

        Assert.Contains("use PcScreenshot", withVision.Content);
        Assert.DoesNotContain("PcScreenshot", without.Content);
        Assert.Contains("cannot be driven this way", without.Content);
    }

    [Fact]
    public void Every_pixel_step_asks_the_user_whatever_the_mode()
    {
        var tools = Tools(true);
        var engine = new Kairo.Core.Permissions.PermissionEngine(new Kairo.Core.Permissions.PermissionRules(), Kairo.Core.Permissions.PermissionMode.Bypass);

        foreach (var call in new[]
                 {
                     (Tool(tools, "PcAct"), Json(new { action = "click_at", x = 3, y = 4, look = 1 })),
                     (Tool(tools, "PcAct"), Json(new { action = "type_focused", text = "x" })),
                     (Tool(tools, "PcScreenshot"), Json(new { })),
                 })
        {
            var assessment = call.Item1.Assess(call.Item2, Ctx);
            Assert.Equal(Kairo.Core.Permissions.PermissionDecision.Ask, engine.Evaluate(call.Item1.Spec.Name, assessment, Ctx).Decision);
        }
        Assert.Contains("(3, 4)", Tool(tools, "PcAct").Assess(Json(new { action = "click_at", x = 3, y = 4, look = 1 }), Ctx).Summary);
    }

    [Fact]
    public void With_autonomous_on_the_pc_tools_pass_in_bypass_but_still_ask_in_the_default_mode()
    {
        var tools = PcTools.For(Config("vision = true\nautonomous = true"), () => null, _desktop);
        var bypass = new Kairo.Core.Permissions.PermissionEngine(new Kairo.Core.Permissions.PermissionRules(), Kairo.Core.Permissions.PermissionMode.Bypass);
        var normal = new Kairo.Core.Permissions.PermissionEngine(new Kairo.Core.Permissions.PermissionRules(), Kairo.Core.Permissions.PermissionMode.Default);

        foreach (var call in new[]
                 {
                     (Tool(tools, "PcAct"), Json(new { action = "click_at", x = 3, y = 4, look = 1 })),
                     (Tool(tools, "PcAct"), Json(new { action = "type_focused", text = "x" })),
                     (Tool(tools, "PcScreenshot"), Json(new { })),
                     (Tool(tools, "PcObserve"), Json(new { })),
                 })
        {
            var assessment = call.Item1.Assess(call.Item2, Ctx);
            Assert.Equal(Kairo.Core.Permissions.PermissionDecision.Allow, bypass.Evaluate(call.Item1.Spec.Name, assessment, Ctx).Decision);
            Assert.Equal(Kairo.Core.Permissions.PermissionDecision.Ask, normal.Evaluate(call.Item1.Spec.Name, assessment, Ctx).Decision);
        }
    }
}
