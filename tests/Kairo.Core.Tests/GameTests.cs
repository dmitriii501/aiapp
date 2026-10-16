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

public sealed class GameExprTests
{
    private static object Eval(string text, Dictionary<string, object>? names = null) =>
        GameExpr.Parse(text).Eval(n => names is not null && names.TryGetValue(n, out var v) ? v : null, () => 0.5);

    [Theory]
    [InlineData("1 + 2 * 3", 7.0)]
    [InlineData("(1 + 2) * 3", 9.0)]
    [InlineData("10 - 4 - 3", 3.0)]
    [InlineData("10 / 4", 2.5)]
    [InlineData("7 % 4", 3.0)]
    [InlineData("-3 + 5", 2.0)]
    [InlineData("min(4, 2, 9)", 2.0)]
    [InlineData("max(1, 5)", 5.0)]
    [InlineData("abs(-2.5)", 2.5)]
    [InlineData("round(2.5)", 3.0)]
    [InlineData("floor(2.9)", 2.0)]
    [InlineData("ceil(2.1)", 3.0)]
    [InlineData("random(10)", 5.0)] // the test's random is always 0.5
    public void Numbers_add_up_the_way_arithmetic_does(string text, double expected) => Assert.Equal(expected, (double)Eval(text));

    [Theory]
    [InlineData("1 < 2 && 2 < 3", true)]
    [InlineData("1 > 2 || 2 < 3", true)]
    [InlineData("!(1 > 2)", true)]
    [InlineData("1 == 1.0", true)]
    [InlineData("1 != 2", true)]
    [InlineData("'Red' == 'red'", true)]
    [InlineData("\"a\" != \"b\"", true)]
    [InlineData("true && false", false)]
    [InlineData("1 > 2 || 3 > 2 && 2 > 1", true)] // && binds tighter than ||
    [InlineData("2 >= 2 && 2 <= 2", true)]
    public void Comparisons_and_logic_work(string text, bool expected) => Assert.Equal(expected, (bool)Eval(text));

    [Fact]
    public void Names_are_read_from_the_lookup_and_listed()
    {
        var expr = GameExpr.Parse("find.card.x + offset > 100 && check.over < 0.5");

        Assert.Equal(["find.card.x", "offset", "check.over"], expr.Names.Order(StringComparer.Ordinal).OrderBy(n => n == "find.card.x" ? 0 : n == "offset" ? 1 : 2));
        Assert.True((bool)Eval("find.card.x + offset > 100 && check.over < 0.5",
            new() { ["find.card.x"] = 90.0, ["offset"] = 20.0, ["check.over"] = 0.1 }));
    }

    [Fact]
    public void Text_can_be_joined_and_compared_but_not_ordered()
    {
        Assert.Equal("a1", Eval("'a' + 1"));
        Assert.Equal(true, Eval("choose.color == 'blue'", new() { ["choose.color"] = "blue" }));
        Assert.Throws<GameExprException>(() => Eval("'a' < 'b'"));
        Assert.Throws<GameExprException>(() => Eval("'a' * 2"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("1 +")]
    [InlineData("(1 + 2")]
    [InlineData("1 2")]
    [InlineData("foo(1)")]
    [InlineData("1 / 0")]
    [InlineData("find..x")]
    [InlineData("'open")]
    [InlineData("@")]
    public void Nonsense_is_refused_with_a_reason(string text)
    {
        var ex = Assert.Throws<GameExprException>(() => Eval(text));
        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
    }

    [Fact]
    public void A_name_that_has_no_value_is_an_error_not_zero()
    {
        var ex = Assert.Throws<GameExprException>(() => Eval("nothing + 1"));
        Assert.Contains("nothing", ex.Message);
    }

    [Fact]
    public void A_very_long_expression_is_refused()
    {
        Assert.Throws<GameExprException>(() => GameExpr.Parse(string.Join(" + ", Enumerable.Repeat("1", 300))));
    }
}

public sealed class GamePolicyTests
{
    private static GamePolicyParse Parse(string json) => GamePolicyParser.Parse(JsonDocument.Parse(json).RootElement, new PcGuard(["msedge.exe"]));

    private const string Good = """
        {"goal":"click the green circles","app":"msedge.exe",
         "see":{"find":{"green":"the green circle"},"check":{"over":"a Game over message is shown"},"choose":{"word":{"question":"Which colour does the text name?","options":{"red":"red","blue":"blue"}}}},
         "vars":{"clicks":0},
         "rules":[
           {"name":"end","when":"check.over > 0.8","do":[{"stop":"game over"}]},
           {"name":"hit","when":"find.green.seen > 0.7 && find.green.conf > 0.5","do":[{"click":"green"},{"set":"clicks = clicks + 1"}]}],
         "limits":{"max_seconds":60}}
        """;

    [Fact]
    public void A_good_policy_is_accepted_and_described_for_the_user()
    {
        var parsed = Parse(Good);

        Assert.True(parsed.Ok, string.Join("; ", parsed.Errors));
        var policy = parsed.Policy!;
        Assert.Equal(("msedge.exe", 1, 1, 1, 2, 60), (policy.App, policy.Finds.Count, policy.Checks.Count, policy.Chooses.Count, policy.Rules.Count, policy.Limits.MaxSeconds));
        var text = policy.Describe();
        Assert.Contains("click green", text);
        Assert.Contains("set clicks = clicks + 1", text);
        Assert.Contains("goes to Jev", text);
        Assert.Contains("Pause", text);
    }

    [Theory]
    [InlineData("""{"goal":"g","app":"msedge.exe","see":{"find":{"a":"x"}},"rules":[{"when":"find.b.seen > 1","do":[{"stop":"x"}]}]}""", "find.b.seen")]
    [InlineData("""{"goal":"g","app":"msedge.exe","see":{"find":{"a":"x"}},"rules":[{"when":"find.a.seen > 1","do":[{"click":"nope"}]}]}""", "no find with that name")]
    [InlineData("""{"goal":"g","app":"cmd.exe","see":{"find":{"a":"x"}},"rules":[{"when":"true","do":[{"stop":"x"}]}]}""", "cannot be controlled")]
    [InlineData("""{"goal":"g","app":"msedge.exe","see":{"find":{"a":"the Delete button"}},"rules":[{"when":"true","do":[{"click":"a"}]}]}""", "cannot be undone")]
    [InlineData("""{"goal":"g","app":"msedge.exe","see":{"check":{"a":"x"}},"rules":[{"when":"check.a > 0.5","do":[{"key":"alt+f4"}]}]}""", "Press it yourself")]
    [InlineData("""{"goal":"g","app":"msedge.exe","see":{"check":{"a":"x"}},"rules":[{"when":"check.a > 0.5","do":[{"key":"win+r"}]}]}""", "not a key Kairo presses")]
    [InlineData("""{"goal":"g","app":"msedge.exe","see":{"check":{"a":"x"}},"rules":[{"when":"check.a > 0.5","do":[{"set":"nope = 1"}]}]}""", "declared under vars")]
    [InlineData("""{"goal":"g","app":"msedge.exe","see":{"check":{"a":"x"}},"rules":[{"when":"check.a >","do":[{"stop":"x"}]}]}""", "ends too soon")]
    [InlineData("""{"goal":"g","app":"msedge.exe","rules":[{"when":"true","do":[{"stop":"x"}]}]}""", "ask something")]
    [InlineData("""{"goal":"g","app":"msedge.exe","see":{"check":{"a":"x"}}}""", "at least one rule")]
    [InlineData("""{"goal":"g","app":"msedge.exe","region":{"left":0.5,"right":0.55},"see":{"check":{"a":"x"}},"rules":[{"when":"true","do":[{"stop":"x"}]}]}""", "at least a tenth")]
    [InlineData("""{"goal":"g","app":"msedge.exe","width":99999,"see":{"check":{"a":"x"}},"rules":[{"when":"true","do":[{"stop":"x"}]}]}""", "320 to 1568")]
    [InlineData("""{"goal":"g","app":"msedge.exe","see":{"check":{"A B":"x"}},"rules":[{"when":"true","do":[{"stop":"x"}]}]}""", "lowercase")]
    public void A_policy_with_a_problem_is_refused_with_every_reason(string json, string reason)
    {
        var parsed = Parse(json);

        Assert.False(parsed.Ok);
        Assert.Contains(parsed.Errors, e => e.Contains(reason, StringComparison.Ordinal));
    }

    [Fact]
    public void All_the_problems_are_reported_at_once()
    {
        var parsed = Parse("""{"app":"cmd.exe","see":{"find":{"a":"the Delete button"}},"rules":[{"when":"zzz > 1","do":[{"click":"q"}]}]}""");

        Assert.True(parsed.Errors.Count >= 4, string.Join("; ", parsed.Errors));
    }

    [Fact]
    public void Too_many_questions_for_one_look_are_refused()
    {
        var finds = string.Join(",", Enumerable.Range(0, 16).Select(i => $"\"f{i}\":\"thing {i}\""));
        var checks = string.Join(",", Enumerable.Range(0, 12).Select(i => $"\"c{i}\":\"statement {i}\""));
        var parsed = Parse("{\"goal\":\"g\",\"app\":\"msedge.exe\",\"see\":{\"find\":{" + finds + "},\"check\":{" + checks + "}},\"rules\":[{\"when\":\"true\",\"do\":[{\"stop\":\"x\"}]}]}");

        Assert.Contains(parsed.Errors, e => e.Contains("Too many questions"));
    }

    [Fact]
    public void A_point_click_takes_expressions_in_window_pixels()
    {
        var parsed = Parse("""{"goal":"g","app":"msedge.exe","see":{"find":{"board":"the board"}},"vars":{"col":2},"rules":[{"when":"true","do":[{"click":{"x":"find.board.left + col * 50","y":"find.board.y"},"button":"double"}]}]}""");

        Assert.True(parsed.Ok, string.Join("; ", parsed.Errors));
        var click = Assert.IsType<ClickAction>(parsed.Policy!.Rules[0].Do[0]);
        Assert.Null(click.Target);
        Assert.Equal("double", click.Button);
    }
}

public sealed class GameRunnerTests : IDisposable
{
    private sealed record Frame(Dictionary<string, (double Seen, BoxAnswer Box)>? Finds = null, Dictionary<string, double>? Checks = null, Dictionary<string, string>? Chooses = null);

    /// <summary>A model that sees what the test scripts for each look, in the shape Jev answers: a box and a yes/no per find, a yes/no per check, a choice per choose.</summary>
    private sealed class ScriptedEyes(GamePolicy policy, Func<int, Frame> frames) : IDecisionModel
    {
        public string Id => "eyes";
        public List<DecisionRequest> Requests { get; } = [];
        public Func<int, DecisionException?>? Fail { get; set; }

        public Task<DecisionResult> DecideAsync(DecisionRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            var n = Requests.Count;
            if (Fail?.Invoke(n) is { } failure)
                throw failure;
            var frame = frames(n);
            var answers = new Dictionary<string, DecisionAnswer>();
            for (var i = 0; i < policy.Finds.Count; i++)
            {
                var name = policy.Finds[i].Name;
                var found = frame.Finds is not null && frame.Finds.TryGetValue(name, out var f) ? f : (0.0, new BoxAnswer(0.1, 0.1, 0.2, 0.2, 0.9));
                answers[$"f{i}"] = found.Item2;
                if (request.Questions.ContainsKey($"v{i}"))
                    answers[$"v{i}"] = new YesNoAnswer(found.Item1);
            }
            for (var i = 0; i < policy.Checks.Count; i++)
                answers[$"c{i}"] = new YesNoAnswer(frame.Checks is not null && frame.Checks.TryGetValue(policy.Checks[i].Name, out var c) ? c : 0.0);
            for (var i = 0; i < policy.Chooses.Count; i++)
            {
                var key = frame.Chooses is not null && frame.Chooses.TryGetValue(policy.Chooses[i].Name, out var k) ? k : policy.Chooses[i].Options[0].Key;
                answers[$"h{i}"] = new ChoiceAnswer(key, 0.9, policy.Chooses[i].Options.ToDictionary(o => o.Key, o => o.Key == key ? 1.0 : 0.0));
            }
            return Task.FromResult(new DecisionResult("jev-1.14.0", answers, 1500, TimeSpan.FromMilliseconds(3)));
        }
    }

    private readonly FakeDesktop _desktop = new();
    private readonly PcGuard _guard = new(["notepad.exe"]);
    private readonly TempDir _dir = new();

    public GameRunnerTests()
    {
        // A real picture, plain; the same bytes every time unless a test changes them.
        _desktop.PictureBytes = Png(Color.LightGray);
        _desktop.PictureWidth = 800;
        _desktop.PictureHeight = 500;
        _desktop.StaticPicture = true;
    }

    public void Dispose() => _dir.Dispose();

    private static byte[] Png(Color color)
    {
        using var bitmap = new Bitmap(800, 500);
        using (var g = Graphics.FromImage(bitmap))
            g.Clear(color);
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }

    private static GamePolicy Policy(string json)
    {
        var parsed = GamePolicyParser.Parse(JsonDocument.Parse(json).RootElement, new PcGuard(["notepad.exe"]));
        Assert.True(parsed.Ok, string.Join("; ", parsed.Errors));
        return parsed.Policy!;
    }

    private static BoxAnswer Box(double left, double top, double right, double bottom, double conf = 0.9) => new(left, top, right, bottom, conf);

    private Task<GameResult> Run(GamePolicy policy, IDecisionModel eyes, GameRunOptions? options = null) =>
        GameRunner.RunAsync(_desktop, _guard, eyes, policy, _desktop.Window.Handle, options, default);

    private const string Circles = """
        {"goal":"click the green circle","app":"notepad.exe",
         "see":{"find":{"green":"the green circle"},"check":{"over":"the game is over"}},
         "rules":[
           {"name":"end","when":"check.over > 0.8","do":[{"stop":"game over"}]},
           {"name":"hit","when":"find.green.seen > 0.7","do":[{"click":"green"}]}],
         "limits":{"max_ticks":50,"stall_ticks":5}}
        """;

    [Fact]
    public async Task It_clicks_what_is_found_at_the_pixel_centre_until_a_rule_says_stop()
    {
        var policy = Policy(Circles);
        var eyes = new ScriptedEyes(policy, n => n < 4
            ? new Frame(new() { ["green"] = (0.95, Box(0.4, 0.4, 0.6, 0.6)) })
            : new Frame(Checks: new() { ["over"] = 0.97 }));
        _desktop.StaticPicture = false; // the picture changes after every click, as a game does

        var result = await Run(policy, eyes);

        Assert.Equal(GameStatus.Done, result.Status);
        Assert.Equal("game over", result.Reason);
        Assert.Equal((4, 3), (result.Ticks, result.Actions));
        Assert.All(_desktop.Performed, a => Assert.Equal((DesktopActionKind.ClickAt, 400, 250), (a.Kind, a.X!.Value, a.Y!.Value)));
        // Every look is one request with the picture.
        Assert.All(eyes.Requests, r => Assert.Single(r.Images!));
        Assert.Equal(["f0", "v0", "c0"], eyes.Requests[0].Questions.Keys);
    }

    [Fact]
    public async Task A_region_is_cut_out_and_the_boxes_are_mapped_back_into_window_pixels()
    {
        var policy = Policy("""
            {"goal":"g","app":"notepad.exe","region":{"left":0.5,"top":0.25,"right":1,"bottom":0.75},"width":640,
             "see":{"find":{"b":"the button"}},"rules":[{"when":"find.b.seen > 0.5","do":[{"click":"b"},{"stop":"clicked"}]}]}
            """);
        var eyes = new ScriptedEyes(policy, _ => new Frame(new() { ["b"] = (0.9, Box(0.5, 0.5, 0.6, 0.7)) }));

        var result = await Run(policy, eyes);

        // The part is 400x250 px at (400, 125); the box (0.5..0.6, 0.5..0.7) of it is x 600..640, y 250..300: centre (620, 275).
        Assert.Equal(GameStatus.Done, result.Status);
        var click = Assert.Single(_desktop.Performed);
        Assert.Equal((620, 275), (click.X!.Value, click.Y!.Value));
        // What was sent is the part, not the window, and not smaller than Jev takes.
        using var sent = new Bitmap(new MemoryStream(eyes.Requests[0].Images!.Single().Value.Data));
        Assert.True(sent.Width >= 280 && sent.Height >= 280, $"{sent.Width}x{sent.Height}");
        Assert.True(sent.Width < 800);
    }

    [Fact]
    public async Task A_wide_picture_is_sent_no_wider_than_the_policy_says()
    {
        var policy = Policy("""{"goal":"g","app":"notepad.exe","width":640,"see":{"check":{"a":"x"}},"rules":[{"when":"true","do":[{"stop":"x"}]}]}""");
        var eyes = new ScriptedEyes(policy, _ => new Frame());

        await Run(policy, eyes);

        using var sent = new Bitmap(new MemoryStream(eyes.Requests[0].Images!.Single().Value.Data));
        Assert.Equal((640, 400), (sent.Width, sent.Height));
    }

    [Fact]
    public async Task The_first_matching_rule_wins_and_once_and_cooldown_are_kept()
    {
        var policy = Policy("""
            {"goal":"g","app":"notepad.exe","see":{"check":{"a":"x"}},"vars":{"n":0,"m":0},
             "rules":[
               {"name":"first","when":"tick == 1","once":true,"do":[{"set":"n = n + 10"}]},
               {"name":"every3","when":"true","cooldown":3,"do":[{"set":"m = m + 1"}]},
               {"name":"stop","when":"tick >= 7","do":[{"stop":"enough"}]}],
             "limits":{"stall_ticks":50}}
            """);
        var eyes = new ScriptedEyes(policy, _ => new Frame());

        var result = await Run(policy, eyes);

        // tick 1: first. tick 2: every3 (cooldown from never). ticks 3-4: cooldown, so the stop rule is next but not yet true: nothing.
        // tick 5: every3. 6: nothing. 7: stop (every3 is on cooldown since 5).
        Assert.Equal(GameStatus.Done, result.Status);
        Assert.Equal(7, result.Ticks);
        Assert.Contains(result.Log, l => l.Contains("rule first") && l.Contains("set n = 10"));
        Assert.Equal(2, result.Log.Count(l => l.Contains("rule every3")));
    }

    [Fact]
    public async Task Variables_carry_over_between_ticks_and_feed_arithmetic_in_a_point_click()
    {
        var policy = Policy("""
            {"goal":"g","app":"notepad.exe","see":{"find":{"board":"the board"}},"vars":{"col":0},
             "rules":[
               {"name":"done","when":"col >= 3","do":[{"stop":"all columns"}]},
               {"name":"next","when":"find.board.seen > 0.5","do":[{"click":{"x":"find.board.left + col * 100 + 50","y":"find.board.top + 20"}},{"set":"col = col + 1"}]}]}
            """);
        var eyes = new ScriptedEyes(policy, _ => new Frame(new() { ["board"] = (0.99, Box(0.25, 0.2, 0.75, 0.8)) }));
        _desktop.StaticPicture = false;

        var result = await Run(policy, eyes);

        Assert.Equal(GameStatus.Done, result.Status);
        Assert.Equal([(250, 120), (350, 120), (450, 120)], _desktop.Performed.Select(a => (a.X!.Value, a.Y!.Value)));
    }

    [Fact]
    public async Task A_click_on_what_is_not_there_or_what_is_doubtful_is_skipped_not_made()
    {
        var policy = Policy("""
            {"goal":"g","app":"notepad.exe","see":{"find":{"a":"the thing"}},
             "rules":[{"when":"true","do":[{"click":"a"}]}],"limits":{"stall_ticks":2}}
            """);
        var absent = new ScriptedEyes(policy, _ => new Frame(new() { ["a"] = (0.1, Box(0.4, 0.4, 0.6, 0.6)) }));
        var doubtful = new ScriptedEyes(policy, _ => new Frame(new() { ["a"] = (0.95, Box(0.4, 0.4, 0.6, 0.6, 0.2)) }));

        var a = await Run(policy, absent);
        var d = await Run(policy, doubtful);

        Assert.Empty(_desktop.Performed);
        Assert.Equal(GameStatus.Stalled, a.Status);
        Assert.Contains(a.Log, l => l.Contains("not seen"));
        Assert.Contains(d.Log, l => l.Contains("box doubtful"));
    }

    [Fact]
    public async Task A_choice_picks_the_rule_and_a_handover_gives_the_screen_back_with_what_was_seen()
    {
        var policy = Policy("""
            {"goal":"g","app":"notepad.exe","see":{"find":{"red":"the red button","blue":"the blue button"},"choose":{"word":{"question":"Which colour does the text name?","options":{"red":"red","blue":"blue","other":"anything else"}}}},
             "rules":[
               {"when":"choose.word == 'blue'","do":[{"click":"blue"},{"stop":"clicked blue"}]},
               {"when":"choose.word == 'red'","do":[{"click":"red"},{"stop":"clicked red"}]},
               {"when":"true","do":[{"handover":"cannot tell the colour"}]}]}
            """);
        Frame Look(string word) => new(new()
        {
            ["red"] = (0.99, Box(0.1, 0.5, 0.2, 0.6)),
            ["blue"] = (0.99, Box(0.7, 0.5, 0.8, 0.6)),
        }, Chooses: new() { ["word"] = word });

        var blue = await Run(policy, new ScriptedEyes(policy, _ => Look("blue")));
        var click = _desktop.Performed.Single();
        _desktop.Performed.Clear();
        var other = await Run(policy, new ScriptedEyes(policy, _ => Look("other")));

        Assert.Equal((GameStatus.Done, "clicked blue"), (blue.Status, blue.Reason));
        Assert.Equal((600, 275), (click.X!.Value, click.Y!.Value));
        Assert.Equal((GameStatus.Handover, "cannot tell the colour"), (other.Status, other.Reason));
        Assert.Empty(_desktop.Performed);
        var text = GameRunner.Describe(other);
        Assert.Contains("Handed back to you", text);
        Assert.Contains("red: at (120, 275)", text); // what Jev saw, with pixel centres to go on from
        Assert.Contains("word: other", text);
    }

    [Fact]
    public async Task Rules_that_have_nothing_to_do_stop_the_run_instead_of_looping()
    {
        var policy = Policy("""{"goal":"g","app":"notepad.exe","see":{"check":{"a":"x"}},"rules":[{"when":"check.a > 0.5","do":[{"stop":"x"}]}],"limits":{"stall_ticks":4}}""");

        var result = await Run(policy, new ScriptedEyes(policy, _ => new Frame()));

        Assert.Equal(GameStatus.Stalled, result.Status);
        Assert.Equal(4, result.Ticks);
        Assert.Contains("no rule had anything to do", result.Reason);
    }

    [Fact]
    public async Task Clicking_that_changes_nothing_on_the_screen_stops_the_run()
    {
        var policy = Policy("""{"goal":"g","app":"notepad.exe","see":{"find":{"a":"x"}},"rules":[{"when":"find.a.seen > 0.5","do":[{"click":"a"}]}],"limits":{"stall_ticks":3}}""");
        var eyes = new ScriptedEyes(policy, _ => new Frame(new() { ["a"] = (0.9, Box(0.4, 0.4, 0.6, 0.6)) }));

        var result = await Run(policy, eyes); // the picture is the same bytes each time

        Assert.Equal(GameStatus.Stalled, result.Status);
        Assert.Contains("did not change", result.Reason);
        Assert.True(_desktop.Performed.Count is >= 3 and <= 5);
    }

    [Fact]
    public async Task The_limits_of_ticks_and_time_end_the_run()
    {
        var ticks = Policy("""{"goal":"g","app":"notepad.exe","see":{"check":{"a":"x"}},"rules":[{"when":"true","do":[{"set":"x = 1"}]}],"vars":{"x":0},"limits":{"max_ticks":5,"stall_ticks":100}}""");
        var time = Policy("""{"goal":"g","app":"notepad.exe","see":{"check":{"a":"x"}},"rules":[{"when":"true","do":[{"wait":300}]}],"limits":{"max_seconds":5,"stall_ticks":100,"max_ticks":1000}}""");

        var a = await Run(ticks, new ScriptedEyes(ticks, _ => new Frame()));

        Assert.Equal((GameStatus.TickLimit, 5), (a.Status, a.Ticks));
        Assert.Equal(GameStatus.TimeLimit, (await GameRunner.RunAsync(_desktop, _guard, new ScriptedEyes(time, _ => new Frame()), time with { Limits = time.Limits with { MaxSeconds = 1 } }, _desktop.Window.Handle, null, default)).Status);
    }

    [Fact]
    public async Task The_stop_key_and_cancelling_end_the_run_at_once()
    {
        var policy = Policy(Circles);
        _desktop.Stop = true;

        var stopped = await Run(policy, new ScriptedEyes(policy, _ => new Frame()));

        Assert.Equal(GameStatus.Aborted, stopped.Status);
        Assert.Equal(0, stopped.Ticks);
        Assert.Contains("Pause", stopped.Reason);
    }

    [Fact]
    public async Task A_window_that_is_gone_or_off_limits_ends_the_run()
    {
        var policy = Policy(Circles);
        _desktop.Gone = true;
        var gone = await Run(policy, new ScriptedEyes(policy, _ => new Frame()));
        _desktop.Gone = false;
        _desktop.Window = _desktop.Window with { Process = "cmd.exe" };
        var denied = await Run(policy, new ScriptedEyes(policy, _ => new Frame()));

        Assert.Equal((GameStatus.Failed, "the window is gone"), (gone.Status, gone.Reason));
        Assert.Equal(GameStatus.Failed, denied.Status);
        Assert.Contains("cannot be controlled", denied.Reason);
    }

    [Fact]
    public async Task A_dry_run_looks_once_and_does_nothing()
    {
        var policy = Policy(Circles);
        var eyes = new ScriptedEyes(policy, _ => new Frame(new() { ["green"] = (0.95, Box(0.4, 0.4, 0.6, 0.6)) }));

        var result = await Run(policy, eyes, new GameRunOptions(DryRun: true));

        Assert.Equal(1, result.Ticks);
        Assert.Empty(_desktop.Performed);
        Assert.Contains("dry run", result.Reason);
        Assert.Contains("click green @ (400,250) [dry run]", result.Log[0]);
    }

    [Fact]
    public async Task A_look_that_times_out_is_skipped_a_couple_of_times_and_a_refusal_ends_the_run()
    {
        var policy = Policy(Circles);
        var flaky = new ScriptedEyes(policy, _ => new Frame(Checks: new() { ["over"] = 0.99 }))
        {
            Fail = n => n <= 2 ? new DecisionException("slow", DecisionFailure.Timeout) : null,
        };
        var refused = new ScriptedEyes(policy, _ => new Frame())
        {
            Fail = _ => new DecisionException("not available in this region (HTTP 451)", DecisionFailure.Unavailable),
        };

        var ok = await Run(policy, flaky);
        var failed = await Run(policy, refused);

        Assert.Equal(GameStatus.Done, ok.Status);
        Assert.Equal(1, ok.Ticks); // the two that timed out did not count as ticks
        Assert.Equal(GameStatus.Failed, failed.Status);
        Assert.Contains("451", failed.Reason);
    }

    [Fact]
    public async Task A_rule_that_cannot_be_evaluated_fails_loudly()
    {
        var policy = Policy("""{"goal":"g","app":"notepad.exe","see":{"choose":{"w":{"question":"q","options":{"a":"a","b":"b"}}}},"rules":[{"when":"choose.w * 2 > 1","do":[{"stop":"x"}]}]}""");

        var result = await Run(policy, new ScriptedEyes(policy, _ => new Frame()));

        Assert.Equal(GameStatus.Failed, result.Status);
        Assert.Contains("not a number", result.Reason);
    }

    [Fact]
    public async Task The_summary_says_how_fast_it_went()
    {
        var policy = Policy("""{"goal":"g","app":"notepad.exe","see":{"check":{"a":"x"}},"rules":[{"when":"tick >= 3","do":[{"stop":"enough"}]},{"when":"true","do":[{"wait":50}]}]}""");

        var result = await Run(policy, new ScriptedEyes(policy, _ => new Frame()));
        var text = GameRunner.Describe(result);

        Assert.Equal(3, result.Ticks);
        Assert.Contains("3 ticks in", text);
        Assert.Contains("a minute", text);
        Assert.Contains("Jev", text);
    }

    private sealed class Slow(IDecisionModel inner, int milliseconds) : IDecisionModel
    {
        public string Id => inner.Id;

        public async Task<DecisionResult> DecideAsync(DecisionRequest request, CancellationToken ct)
        {
            await Task.Delay(milliseconds, ct);
            return await inner.DecideAsync(request, ct);
        }
    }

    private const string Pipelined = """
        {"goal":"click the green circle","app":"notepad.exe",
         "see":{"find":{"green":"the green circle"}},
         "rules":[
           {"name":"end","when":"elapsed > 1.6","do":[{"stop":"enough"}]},
           {"name":"hit","when":"find.green.seen > 0.7","do":[{"click":"green"}]}],
         "limits":{"max_ticks":100,"stall_ticks":50,"pipeline":%PIPELINE%}}
        """;

    [Fact]
    public async Task A_pipelined_run_decides_more_often_and_does_not_click_the_same_thing_twice()
    {
        _desktop.StaticPicture = false;
        var plain = Policy(Pipelined.Replace("%PIPELINE%", "false"));
        var piped = Policy(Pipelined.Replace("%PIPELINE%", "true"));
        Assert.False(plain.Limits.Pipeline);
        Assert.True(piped.Limits.Pipeline);
        Frame Green(int _) => new(new() { ["green"] = (0.95, Box(0.4, 0.4, 0.6, 0.6)) });

        var slowly = await Run(plain, new Slow(new ScriptedEyes(plain, Green), 400));
        _desktop.Performed.Clear();
        var fast = await Run(piped, new Slow(new ScriptedEyes(piped, Green), 400));

        Assert.Equal(GameStatus.Done, fast.Status);
        Assert.True(fast.Ticks > slowly.Ticks, $"{fast.Ticks} ticks pipelined against {slowly.Ticks}");
        // The target never moves, so the second look of a pair finds it where the first one already clicked.
        Assert.True(fast.Log.Any(l => l.Contains("clicked there since this picture was taken")), string.Join(" | ", fast.Log));
        Assert.True(_desktop.Performed.Count < fast.Ticks - 1, $"{_desktop.Performed.Count} clicks in {fast.Ticks} ticks");
    }

    [Fact]
    public void A_pipelined_policy_may_not_keep_notes()
    {
        var parsed = GamePolicyParser.Parse(JsonDocument.Parse("""
            {"goal":"g","app":"notepad.exe","vars":{"n":0},"rules":[{"when":"true","do":[{"set":"n = n + 1"}]}],"limits":{"pipeline":true}}
            """).RootElement, new PcGuard(["notepad.exe"]));

        Assert.False(parsed.Ok);
        Assert.Contains(parsed.Errors, e => e.Contains("pipeline"));
    }
}

public sealed class PcPlayToolTests : IDisposable
{
    private readonly FakeDesktop _desktop = new();
    private readonly TempDir _dir = new();

    public PcPlayToolTests()
    {
        using var bitmap = new Bitmap(800, 500);
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        _desktop.PictureBytes = stream.ToArray();
        _desktop.PictureWidth = 800;
        _desktop.PictureHeight = 500;
        _desktop.StaticPicture = false;
        _desktop.Elements = [El("e1", "pane", "Game")];
    }

    public void Dispose() => _dir.Dispose();

    private sealed class Sees(Func<DecisionRequest, DecisionResult> answer) : IDecisionModel
    {
        public string Id => "sees";
        public Task<DecisionResult> DecideAsync(DecisionRequest request, CancellationToken ct) => Task.FromResult(answer(request));
    }

    private IReadOnlyList<ITool> Tools(bool jevVision = true, IDecisionModel? model = null)
    {
        var dir = Path.Combine(_dir.Path, "cfg");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "config.toml"), $"[pc]\nenabled = true\napps = [\"notepad.exe\"]\nvision = true\njev_vision = {(jevVision ? "true" : "false")}");
        return PcTools.For(KairoConfig.Load(dir, dir, includeProject: false), () => model, _desktop);
    }

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private ToolContext Ctx => new(_dir.Path);

    private const string StopAtOnce = """{"policy":{"goal":"test","app":"notepad.exe","see":{"check":{"a":"x"}},"rules":[{"when":"check.a > 0.5","do":[{"stop":"yes"}]}]}}""";

    private static DecisionResult Answer(double check) => new("jev-1.14.0", new Dictionary<string, DecisionAnswer> { ["c0"] = new YesNoAnswer(check) }, 100, TimeSpan.FromMilliseconds(2));

    [Fact]
    public void The_tool_exists_only_with_the_users_setting()
    {
        Assert.DoesNotContain(Tools(jevVision: false), t => t.Spec.Name == "PcPlay");
        Assert.Contains(Tools(), t => t.Spec.Name == "PcPlay");
    }

    [Fact]
    public async Task It_runs_the_policy_and_reports_how_it_went()
    {
        var tool = Tools(model: new Sees(_ => Answer(0.99))).Single(t => t.Spec.Name == "PcPlay");

        var result = await tool.InvokeAsync(Json(StopAtOnce), Ctx, default);

        Assert.False(result.IsError, result.Content);
        Assert.Contains("Finished: yes", result.Content);
        Assert.Contains("1 ticks in", result.Content);
        Assert.Contains("Window title now", result.Content);
    }

    [Fact]
    public void The_whole_policy_is_shown_to_the_user_with_the_question_and_asks_like_the_others()
    {
        var tool = Tools(model: new Sees(_ => Answer(0))).Single(t => t.Spec.Name == "PcPlay");

        var assessment = tool.Assess(Json(StopAtOnce), Ctx);

        Assert.Equal(RiskLevel.System, assessment.Risk);
        Assert.Contains("a screenshot goes to Jev every tick", assessment.Summary);
        Assert.Contains("rule r1: when check.a > 0.5", assessment.Detail);
    }

    [Fact]
    public void A_policy_with_problems_is_refused_listing_all_of_them_and_a_string_of_json_is_accepted()
    {
        var tool = Tools(model: new Sees(_ => Answer(0))).Single(t => t.Spec.Name == "PcPlay");

        var ex = Assert.Throws<ToolInputException>(() => tool.Assess(Json("""{"policy":{"app":"cmd.exe"}}"""), Ctx));
        var asString = JsonSerializer.SerializeToElement(new { policy = JsonDocument.Parse(StopAtOnce).RootElement.GetProperty("policy").GetRawText() });

        Assert.Contains("goal", ex.Message);
        Assert.Contains("cannot be controlled", ex.Message);
        Assert.Contains("rules", ex.Message);
        Assert.NotNull(tool.Assess(asString, Ctx));
    }

    [Fact]
    public async Task A_dry_run_does_not_click_and_a_missing_model_is_explained()
    {
        var clicking = """{"dry_run":true,"policy":{"goal":"test","app":"notepad.exe","see":{"find":{"a":"the thing"}},"rules":[{"when":"find.a.seen > 0.5","do":[{"click":"a"}]}]}}""";
        var model = new Sees(request => new DecisionResult("jev-1.14.0", new Dictionary<string, DecisionAnswer>
        {
            ["f0"] = new BoxAnswer(0.4, 0.4, 0.6, 0.6, 0.9),
            ["v0"] = new YesNoAnswer(0.95),
        }, 100, TimeSpan.FromMilliseconds(2)));

        var dry = await Tools(model: model).Single(t => t.Spec.Name == "PcPlay").InvokeAsync(Json(clicking), Ctx, default);
        var none = await Tools(model: null).Single(t => t.Spec.Name == "PcPlay").InvokeAsync(Json(StopAtOnce), Ctx, default);

        Assert.Contains("dry run", dry.Content);
        Assert.Empty(_desktop.Performed);
        Assert.True(none.IsError);
        Assert.Contains("not set up", none.Content);
    }
}
