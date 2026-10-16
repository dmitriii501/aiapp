using System.Drawing;
using System.Drawing.Imaging;
using System.Text.Json;
using System.Text.RegularExpressions;
using Kairo.Core.Decisions;
using Kairo.Core.Tests.Fakes;
using Kairo.PcControl;

namespace Kairo.Core.Tests;

public sealed class GameMapTests
{
    private static Dictionary<string, object> Map(params (string, object)[] entries) =>
        entries.ToDictionary(e => e.Item1, e => e.Item2, StringComparer.Ordinal);

    private static object Eval(string text, Dictionary<string, object> vars) =>
        GameExpr.Parse(text).Eval(n => vars.GetValueOrDefault(n), () => 0.5);

    [Fact]
    public void A_map_is_read_with_get_has_and_count()
    {
        var vars = new Dictionary<string, object> { ["m"] = Map(("3", "star"), ("7", 4.0)), ["i"] = 3.0 };

        Assert.Equal("star", Eval("get(m, i)", vars));
        Assert.Equal("star", Eval("get(m, '3')", vars)); // 3 and "3" are the same key
        Assert.Equal(4.0, Eval("get(m, 7)", vars));
        Assert.Equal("", Eval("get(m, 9)", vars));
        Assert.Equal(-1.0, Eval("get(m, 9, -1)", vars));
        Assert.Equal(true, Eval("has(m, 7) && !has(m, 8)", vars));
        Assert.Equal(2.0, Eval("count(m)", vars));
    }

    [Fact]
    public void first_free_finds_the_first_number_that_is_not_a_key()
    {
        var vars = new Dictionary<string, object> { ["m"] = Map(("0", 1.0), ("1", 1.0), ("3", 1.0)) };

        Assert.Equal(2.0, Eval("first_free(m, 5)", vars));
        Assert.Equal(-1.0, Eval("first_free(m, 2)", vars));
    }

    [Fact]
    public void find_key_looks_for_another_key_with_the_same_value_outside_a_skip_map()
    {
        var vars = new Dictionary<string, object>
        {
            ["known"] = Map(("0", "star"), ("4", "heart"), ("6", "star"), ("9", "star")),
            ["done"] = Map(("6", 1.0)),
        };

        Assert.Equal(0.0, Eval("find_key(known, 'star', done, 9)", vars)); // not 6 (done), not 9 (excepted)
        Assert.Equal(-1.0, Eval("find_key(known, 'heart', done, 4)", vars));
        Assert.Equal(-1.0, Eval("find_key(known, 'diamond', done, -1)", vars));
    }

    [Fact]
    public void pair_a_and_pair_b_name_two_unmatched_cards_with_the_same_symbol()
    {
        var vars = new Dictionary<string, object>
        {
            ["known"] = Map(("2", "circle"), ("5", "star"), ("8", "circle"), ("10", "star")),
            ["done"] = Map(("2", 1.0), ("8", 1.0)),
        };

        Assert.Equal(5.0, Eval("pair_a(known, done)", vars));
        Assert.Equal(10.0, Eval("pair_b(known, done)", vars));
        vars["done"] = Map(("2", 1.0), ("8", 1.0), ("5", 1.0), ("10", 1.0));
        Assert.Equal(-1.0, Eval("pair_a(known, done)", vars));
    }

    [Fact]
    public void A_map_function_on_something_that_is_not_a_map_is_an_error_with_a_reason()
    {
        var ex = Assert.Throws<GameExprException>(() => Eval("get(x, 1)", new() { ["x"] = 5.0 }));
        Assert.Contains("must be a map", ex.Message);
        Assert.Throws<GameExprException>(() => Eval("count()", new()));
    }

    [Fact]
    public void A_policy_declares_maps_and_changes_them_with_put_del_and_clear()
    {
        var parsed = GamePolicyParser.Parse(JsonDocument.Parse("""
            {"goal":"g","app":"msedge.exe","see":{"check":{"a":"x"}},"vars":{"m":{},"n":0,"seed":{"0":"a"}},
             "rules":[{"when":"true","do":[
                {"put":{"map":"m","key":"n","value":"'x'"}},
                {"del":{"map":"m","key":"n"}},
                {"clear":"m"},
                {"set":"n = count(m)"}]}]}
            """).RootElement, new PcGuard([]));

        Assert.True(parsed.Ok, string.Join("; ", parsed.Errors));
        Assert.IsType<Dictionary<string, object>>(parsed.Policy!.Vars["m"]);
        Assert.Equal("a", ((Dictionary<string, object>)parsed.Policy.Vars["seed"])["0"]);
        Assert.Equal(["put m[n] = 'x'", "del m[n]", "clear m", "set n = count(m)"], parsed.Policy.Rules[0].Do.Select(GamePolicy.Describe));
    }

    [Theory]
    [InlineData("""{"put":{"map":"n","key":"1","value":"2"}}""", "map declared under vars")] // n is a number, not a map
    [InlineData("""{"put":{"map":"zzz","key":"1","value":"2"}}""", "map declared under vars")]
    [InlineData("""{"del":{"map":"n","key":"1"}}""", "map declared under vars")]
    [InlineData("""{"clear":"n"}""", "map declared under vars")]
    [InlineData("""{"set":"m = 3"}""", "declared under vars")] // a map cannot be overwritten as a value
    public void Map_actions_on_things_that_are_not_maps_are_refused(string action, string reason)
    {
        var parsed = GamePolicyParser.Parse(JsonDocument.Parse("{\"goal\":\"g\",\"app\":\"msedge.exe\",\"see\":{\"check\":{\"a\":\"x\"}},\"vars\":{\"m\":{},\"n\":0},\"rules\":[{\"when\":\"true\",\"do\":[" + action + "]}]}").RootElement, new PcGuard([]));

        Assert.False(parsed.Ok);
        Assert.Contains(parsed.Errors, e => e.Contains(reason));
    }
}

/// <summary>The memory game of tests/games, simulated: the real policy file plays 12 cards without anybody telling it where the pairs are.</summary>
public sealed class GameMemoryTests : IDisposable
{
    private static readonly string[] Keys = ["star", "circle", "triangle", "square", "heart", "diamond"];

    private sealed class Sim(int seed)
    {
        public string[] Symbols = Enumerable.Range(0, 6).SelectMany(i => new[] { Keys[i], Keys[i] }).OrderBy(_ => 0).ToArray();
        public bool[] Up = new bool[12];
        public bool[] Done = new bool[12];
        public int? First;
        public int? Last;
        public (int A, int B)? PendingHide;
        public bool PendingSeen;
        public int Moves;
        public int Pairs => Done.Count(d => d) / 2;

        public void Shuffle()
        {
            var rnd = new Random(seed);
            Symbols = [.. Symbols.OrderBy(_ => rnd.Next())];
        }

        // The board is the box (0.2, 0.2)-(0.8, 0.8) of an 800x500 window: x 160..640, y 100..400, cards 120 by 100.
        public void Click(int x, int y)
        {
            var col = (x - 160) / 120;
            var row = (y - 100) / 100;
            if (col is < 0 or > 3 || row is < 0 or > 2)
                return;
            var i = row * 4 + col;
            if (PendingHide is not null || Up[i] || Done[i])
                return;
            Up[i] = true;
            Last = i;
            if (First is null)
            {
                First = i;
                return;
            }
            Moves++;
            if (Symbols[First.Value] == Symbols[i])
            {
                Done[First.Value] = Done[i] = true;
                Up[First.Value] = Up[i] = false;
            }
            else
            {
                PendingHide = (First.Value, i);
                PendingSeen = false;
            }
            First = null;
        }

        /// <summary>What a look sees. A card that does not match turns back after it has been looked at once, as the real one does after 0.8 s.</summary>
        public string Look()
        {
            if (PendingHide is { } hide)
            {
                if (!PendingSeen)
                    PendingSeen = true;
                else
                {
                    Up[hide.A] = Up[hide.B] = false;
                    Last = null;
                    PendingHide = null;
                }
            }
            return Last is { } last && (Up[last] || Done[last]) ? Symbols[last] : "none";
        }
    }

    private sealed class Eyes(Sim sim) : IDecisionModel
    {
        public string Id => "sim";

        public Task<DecisionResult> DecideAsync(DecisionRequest request, CancellationToken ct)
        {
            var answers = new Dictionary<string, DecisionAnswer>
            {
                ["f0"] = new BoxAnswer(0.2, 0.2, 0.8, 0.8, 0.95),
                ["c0"] = new YesNoAnswer(sim.Pairs == 6 ? 0.99 : 0.01),
            };
            var symbol = sim.Look();
            answers["h0"] = new ChoiceAnswer(symbol, 0.95, new Dictionary<string, double> { [symbol] = 0.95 });
            return Task.FromResult(new DecisionResult("jev-1.14.0", answers, 1500, TimeSpan.FromMilliseconds(1)));
        }
    }

    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private static string PolicyText()
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "tests", "games", "memory.policy.json"));
        // The real waits are for a browser's animation; the simulation needs none.
        return Regex.Replace(File.ReadAllText(path), "\"wait\":\\s*\\d+", "\"wait\": 1");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public async Task The_real_policy_file_finds_all_six_pairs_by_remembering(int seed)
    {
        var parsed = GamePolicyParser.Parse(JsonDocument.Parse(PolicyText()).RootElement, new PcGuard(["msedge.exe"]));
        Assert.True(parsed.Ok, string.Join("; ", parsed.Errors));
        var sim = new Sim(seed);
        sim.Shuffle();
        using var bitmap = new Bitmap(800, 500);
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        var desktop = new FakeDesktop
        {
            Window = new WindowInfo(0x10, 100, "msedge.exe", "Memory", true, false),
            PictureBytes = stream.ToArray(),
            PictureWidth = 800,
            PictureHeight = 500,
            StaticPicture = false,
        };
        var version = 0;
        desktop.OnPerform = action =>
        {
            if (action.Kind == DesktopActionKind.ClickAt)
            {
                sim.Click(action.X!.Value, action.Y!.Value);
                // The picture changes with every click, as a game's does.
                using var next = new Bitmap(800, 500);
                using (var g = Graphics.FromImage(next))
                    g.Clear(Color.FromArgb(255, (++version * 37) % 256, (version * 91) % 256, (version * 53) % 256));
                using var nextStream = new MemoryStream();
                next.Save(nextStream, ImageFormat.Png);
                desktop.PictureBytes = nextStream.ToArray();
            }
            return ActionOutcome.Success();
        };

        var result = await GameRunner.RunAsync(desktop, new PcGuard(["msedge.exe"]), new Eyes(sim), parsed.Policy!, 0x10, null, default);

        Assert.True(result.Status == GameStatus.Done, $"{result.Status}: {result.Reason}; pairs {sim.Pairs}; log: {string.Join(" | ", result.Log.Take(14))}");
        Assert.Equal(6, sim.Pairs);
        // Remembering beats guessing by a lot: a player who forgot everything needs about 20 moves; this one at most 12 (a sure upper bound is 6 + 6).
        Assert.True(sim.Moves <= 12, $"{sim.Moves} moves (seed {seed}): {string.Join(" | ", result.Log.Take(12))}");
    }
}
