using System.Drawing;
using System.Drawing.Imaging;
using System.Text.Json;
using Kairo.Core.Config;
using Kairo.Core.Decisions;
using Kairo.Core.Tests.Fakes;
using Kairo.Core.Tools;
using Kairo.PcControl;
using static Kairo.Core.Tests.Fakes.FakeDesktop;

namespace Kairo.Core.Tests;

public sealed class PcSeeTests : IDisposable
{
    /// <summary>A model that sees: answers every box and yes/no question from what the test says, and records the requests.</summary>
    private sealed class Eyes : IDecisionModel
    {
        public string Id => "eyes";
        public List<DecisionRequest> Requests { get; } = [];

        /// <summary>The box for the nth box question asked in all (the first look is 0, a second look is 1 …).</summary>
        public Func<int, string, BoxAnswer> Box { get; set; } = (_, _) => new BoxAnswer(0.4, 0.4, 0.6, 0.6, 0.9);

        public double Visible { get; set; } = 0.95;
        public double Check { get; set; } = 0.97;
        private int _boxes;

        public Task<DecisionResult> DecideAsync(DecisionRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            var answers = new Dictionary<string, DecisionAnswer>();
            foreach (var (id, question) in request.Questions)
            {
                answers[id] = question switch
                {
                    BoxQuestion => Box(_boxes++, question.Instructions),
                    _ when id.StartsWith('v') => new YesNoAnswer(Visible),
                    _ => new YesNoAnswer(Check),
                };
            }
            return Task.FromResult(new DecisionResult("jev-1.14.0", answers, 3755, TimeSpan.FromMilliseconds(5)));
        }
    }

    private readonly FakeDesktop _desktop = new();
    private readonly Eyes _eyes = new();
    private readonly TempDir _dir = new();

    public PcSeeTests()
    {
        // A real picture: a small target is looked at again, which cuts a piece out of it.
        using var bitmap = new Bitmap(800, 500);
        using (var g = Graphics.FromImage(bitmap))
            g.Clear(Color.LightGray);
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        _desktop.PictureBytes = stream.ToArray();
        _desktop.PictureWidth = 800;
        _desktop.PictureHeight = 500;
        _desktop.Elements = [El("e1", "pane", "Canvas")];
    }

    public void Dispose() => _dir.Dispose();

    private IReadOnlyList<ITool> Tools(bool jevVision = true, bool withModel = true)
    {
        var dir = Path.Combine(_dir.Path, "cfg");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "config.toml"), $"[pc]\nenabled = true\napps = [\"notepad.exe\"]\nvision = true\njev_vision = {(jevVision ? "true" : "false")}");
        return PcTools.For(KairoConfig.Load(dir, dir, includeProject: false), () => withModel ? _eyes : null, _desktop);
    }

    private static ITool Tool(IReadOnlyList<ITool> tools, string name) => tools.Single(t => t.Spec.Name == name);

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private ToolContext Ctx => new(_dir.Path);

    [Fact]
    public void The_tools_exist_only_when_the_user_allowed_pictures_to_go_to_Jev()
    {
        Assert.DoesNotContain(Tools(jevVision: false), t => t.Spec.Name is "PcSee" or "PcClick");
        Assert.Contains(Tools(), t => t.Spec.Name == "PcSee");
        Assert.Contains(Tools(), t => t.Spec.Name == "PcClick");
    }

    [Fact]
    public async Task A_thing_is_found_with_its_pixel_centre_and_one_request_carries_everything_asked()
    {
        var tools = Tools();

        var result = await Tool(tools, "PcSee").InvokeAsync(Json("""{"find":["the Save button","the red circle"],"check":["a dialog is open"]}"""), Ctx, default);

        Assert.False(result.IsError, result.Content);
        var request = Assert.Single(_eyes.Requests);
        Assert.Equal(["f0", "v0", "f1", "v1", "c0"], request.Questions.Keys);
        Assert.IsType<BoxQuestion>(request.Questions["f0"]);
        Assert.Contains("the Save button", request.Questions["f0"].Instructions);
        var picture = Assert.Single(request.Images!);
        Assert.Equal("jpg", picture.Value.Format); // not the PNG: a picture with real graphics is megabytes, and the upload is the time
        using (var sent = new Bitmap(new MemoryStream(picture.Value.Data)))
            Assert.Equal((800, 500), (sent.Width, sent.Height));
        Assert.Contains(picture.Key, request.State.Value.GetRawText()); // the question can point at the picture by name
        // Box (0.4..0.6, 0.4..0.6) of an 800x500 picture: centre (400, 250).
        Assert.Contains("FOUND \"the Save button\": click at (400, 250), box (320, 200)-(480, 300)", result.Content);
        Assert.Contains("sure of the box 90%", result.Content);
        Assert.Contains("CHECK \"a dialog is open\": yes (97%)", result.Content);
        Assert.Contains("look=1", result.Content);
    }

    [Fact]
    public async Task What_is_probably_not_on_the_screen_is_said_so_and_its_box_is_not_offered_as_a_target()
    {
        _eyes.Visible = 0.08;

        var result = await Tool(Tools(), "PcSee").InvokeAsync(Json("""{"find":["the unicorn"]}"""), Ctx, default);

        Assert.Contains("NOT SEEN \"the unicorn\"", result.Content);
        Assert.Contains("do not click it", result.Content);
        Assert.DoesNotContain("click at (", result.Content);
    }

    [Fact]
    public async Task A_small_target_gets_a_second_look_at_an_enlarged_piece_and_the_box_is_mapped_back()
    {
        // First look: a 24x20 px box centred at (400, 250). Second look: the piece is 360x225 px at (220, 137.5), shown at twice the size.
        _eyes.Box = (n, _) => n == 0
            ? new BoxAnswer(388.0 / 800, 240.0 / 500, 412.0 / 800, 260.0 / 500, 0.66)
            : new BoxAnswer(0.25, 0.5, 0.30, 0.6, 0.88);
        var tools = Tools();

        var result = await Tool(tools, "PcSee").InvokeAsync(Json("""{"find":["the plus button"]}"""), Ctx, default);

        Assert.Equal(2, _eyes.Requests.Count);
        var enlarged = _eyes.Requests[1].Images!.Single().Value;
        using (var crop = new Bitmap(new MemoryStream(enlarged.Data)))
            Assert.Equal((720, 450), (crop.Width, crop.Height)); // 360x225 at twice the size
        // left = 220 + 0.25*360 = 310, right = 220 + 0.30*360 = 328, top = 137.5 + 0.5*225 = 250, bottom = 137.5 + 0.6*225 = 272.5
        Assert.Contains("click at (319, 261)", result.Content);
        Assert.Contains("second look at an enlarged part", result.Content);
        Assert.Contains("sure of the box 88%", result.Content);
    }

    [Fact]
    public async Task A_big_target_is_not_looked_at_twice()
    {
        await Tool(Tools(), "PcSee").InvokeAsync(Json("""{"find":["the canvas"]}"""), Ctx, default);

        Assert.Single(_eyes.Requests);
    }

    [Fact]
    public async Task A_second_look_that_is_less_sure_than_the_first_is_not_believed()
    {
        _eyes.Box = (n, _) => n == 0
            ? new BoxAnswer(388.0 / 800, 240.0 / 500, 412.0 / 800, 260.0 / 500, 0.8)
            : new BoxAnswer(0.9, 0.9, 0.95, 0.95, 0.3);

        var result = await Tool(Tools(), "PcSee").InvokeAsync(Json("""{"find":["the plus button"]}"""), Ctx, default);

        Assert.Contains("click at (400, 250)", result.Content);
        Assert.DoesNotContain("second look", result.Content);
    }

    [Fact]
    public async Task A_window_too_small_for_Jev_is_reported_and_nothing_is_sent()
    {
        _desktop.PictureWidth = 200;
        _desktop.PictureHeight = 100;

        var result = await Tool(Tools(), "PcSee").InvokeAsync(Json("""{"find":["x"]}"""), Ctx, default);

        Assert.True(result.IsError);
        Assert.Contains("280", result.Content);
        Assert.Empty(_eyes.Requests);
    }

    [Fact]
    public async Task Without_a_decision_model_it_says_so()
    {
        var result = await Tool(Tools(withModel: false), "PcSee").InvokeAsync(Json("""{"find":["x"]}"""), Ctx, default);

        Assert.True(result.IsError);
        Assert.Contains("not set up", result.Content);
    }

    [Fact]
    public void It_asks_like_the_other_pc_tools_and_says_that_a_picture_goes_to_Jev()
    {
        var assessment = Tool(Tools(), "PcSee").Assess(Json("""{"find":["a"],"check":["b"]}"""), Ctx);

        Assert.Equal(RiskLevel.System, assessment.Risk);
        Assert.Contains("goes to Jev", assessment.Summary);
        Assert.Throws<ToolInputException>(() => Tool(Tools(), "PcSee").Assess(Json("{}"), Ctx));
    }

    [Fact]
    public async Task PcClick_clicks_the_centre_of_what_was_found_on_the_screenshot_it_was_found_on()
    {
        var tools = Tools();

        var result = await Tool(tools, "PcClick").InvokeAsync(Json("""{"target":"the Save button"}"""), Ctx, default);

        Assert.False(result.IsError, result.Content);
        var click = Assert.Single(_desktop.Performed);
        Assert.Equal((DesktopActionKind.ClickAt, 1, 400, 250), (click.Kind, click.ObservationVersion, click.X, click.Y));
        Assert.Contains("Clicked \"the Save button\" at (400, 250)", result.Content);
        Assert.Contains("Screenshot", result.Content); // the new picture comes back, like after click_at
    }

    [Theory]
    [InlineData("right", DesktopActionKind.RightClickAt)]
    [InlineData("double", DesktopActionKind.DoubleClickAt)]
    public async Task PcClick_can_right_click_and_double_click(string button, DesktopActionKind expected)
    {
        await Tool(Tools(), "PcClick").InvokeAsync(Json($$"""{"target":"the card","button":"{{button}}"}"""), Ctx, default);

        Assert.Equal(expected, Assert.Single(_desktop.Performed).Kind);
    }

    [Fact]
    public async Task PcClick_does_not_click_what_is_probably_not_there_or_a_doubtful_box()
    {
        _eyes.Visible = 0.1;
        var gone = await Tool(Tools(), "PcClick").InvokeAsync(Json("""{"target":"the unicorn"}"""), Ctx, default);

        _eyes.Visible = 0.95;
        _eyes.Box = (_, _) => new BoxAnswer(0.4, 0.4, 0.6, 0.6, 0.31);
        var doubtful = await Tool(Tools(), "PcClick").InvokeAsync(Json("""{"target":"the thing"}"""), Ctx, default);

        Assert.True(gone.IsError);
        Assert.Contains("probably not on the screen", gone.Content);
        Assert.True(doubtful.IsError);
        Assert.Contains("doubtful", doubtful.Content);
        Assert.Empty(_desktop.Performed);
    }

    [Fact]
    public void PcClick_refuses_an_unknown_button_and_asks_like_the_others()
    {
        var tool = Tool(Tools(), "PcClick");

        Assert.Throws<ToolInputException>(() => tool.Assess(Json("""{"target":"x","button":"middle"}"""), Ctx));
        Assert.Equal(RiskLevel.System, tool.Assess(Json("""{"target":"x"}"""), Ctx).Risk);
    }
}

/// <summary>The whole path against the real Jev (a picture of a made-up interface with known positions). Needs a key and the internet: KAIRO_LIVE_JEV=1.</summary>
public sealed class PcSeeLiveTests
{
    [Fact]
    public async Task The_real_Jev_finds_things_through_PcSee_and_PcClick()
    {
        if (Environment.GetEnvironmentVariable("KAIRO_LIVE_JEV") != "1")
            return;
        var dir = Path.Combine(Environment.GetEnvironmentVariable("TEMP")!, "jevbox");
        var key = Kairo.Host.ProviderFactory.JevKey(new Kairo.Windows.CredentialStore());
        Assert.False(string.IsNullOrEmpty(key));
        var jev = new Kairo.Providers.Jev.JevDecisionModel(key!);

        var desktop = new FakeDesktop { PictureBytes = File.ReadAllBytes(Path.Combine(dir, "ui.png")), PictureWidth = 1568, PictureHeight = 848 };
        desktop.Elements = [El("e1", "pane", "Canvas")];
        var config = Path.Combine(dir, "cfg");
        Directory.CreateDirectory(config);
        File.WriteAllText(Path.Combine(config, "config.toml"), "[pc]\nenabled = true\napps = [\"notepad.exe\"]\nvision = true\njev_vision = true");
        var tools = PcTools.For(KairoConfig.Load(config, config, includeProject: false), () => jev, desktop);
        var ctx = new ToolContext(dir);

        var see = await tools.Single(t => t.Spec.Name == "PcSee").InvokeAsync(
            JsonDocument.Parse("""{"find":["the text input box at the bottom where \"/\" has been typed","the \"+\" button next to СЕССИИ in the left panel","the settings gear icon at the bottom left"],"check":["a list of commands is open above the input box","the window is a dark theme"]}""").RootElement, ctx, default);
        Console.WriteLine(see.Content);
        Assert.False(see.IsError, see.Content);
        Assert.Contains("FOUND", see.Content);

        var click = await tools.Single(t => t.Spec.Name == "PcClick").InvokeAsync(
            JsonDocument.Parse("""{"target":"the text input box at the bottom where \"/\" has been typed"}""").RootElement, ctx, default);
        Console.WriteLine(click.Content);
        Assert.False(click.IsError, click.Content);
        var action = Assert.Single(desktop.Performed);
        // The box is at (332..1252, 759..795) of 1568x848: the click must land inside it.
        Assert.InRange(action.X!.Value, 332, 1252);
        Assert.InRange(action.Y!.Value, 759, 795);
    }
}
