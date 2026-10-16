using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Kairo.Core.Secrets;
using Kairo.Host;
using Kairo.Providers.Jev;

namespace Kairo.Cli;

/// <summary>
/// <c>kairo jev models</c> lists what the account's Jev offers; <c>kairo jev box picture.png "what to find" [model]</c> asks Jev to locate
/// something in a picture (a "box" question: the answer is a rectangle as fractions of the picture) and prints it with the time it took.
/// A way to find out whether Jev can say where to click on a screenshot, and how fast.
/// </summary>
internal static class JevVision
{
    public static async Task<int> RunAsync(CliOptions options, ISecretStore secrets)
    {
        var key = ProviderFactory.JevKey(secrets);
        if (string.IsNullOrEmpty(key))
            return Fail("No Jev key. Enter it in Settings (\"Ключ Jev\") or set TYPESAFE_API_KEY.");
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        var baseUri = JevDecisionModel.DefaultBaseUri.ToString().TrimEnd('/');

        if (options.Positional[0] == "models")
            return await ModelsAsync(http, baseUri).ConfigureAwait(false);

        if (options.Positional[0] == "boxeval")
        {
            // jev boxeval <image> <truth.json> [model] [rounds]
            if (options.Positional.Count < 3 || !File.Exists(options.Positional[1]) || !File.Exists(options.Positional[2]))
                return Fail("Usage: kairo jev boxeval <image.png> <truth.json> [model] [rounds]  (truth: {\"targets\": {\"what\": {\"left\":..,\"top\":..,\"right\":..,\"bottom\":..}}} as fractions)");
            var rounds2 = options.Positional.Count > 4 && int.TryParse(options.Positional[4], out var r2) ? Math.Clamp(r2, 1, 10) : 3;
            return await EvalAsync(http, baseUri, options.Positional[1], options.Positional[2], options.Positional.Count > 3 ? options.Positional[3] : "jev-latest", rounds2,
                zoom: options.Positional.Count > 5 && options.Positional[5] == "zoom").ConfigureAwait(false);
        }

        // jev box <image> "<what>" [model] [rounds]
        if (options.Positional.Count < 3)
            return Fail("Usage: kairo jev box <image.png> \"what to find\" [model] [rounds]");
        var path = options.Positional[1];
        var what = options.Positional[2];
        var model = options.Positional.Count > 3 ? options.Positional[3] : "jev-latest";
        var rounds = options.Positional.Count > 4 && int.TryParse(options.Positional[4], out var n) ? Math.Clamp(n, 1, 20) : 3;
        if (!File.Exists(path))
            return Fail($"No such file: {path}");
        return await BoxAsync(http, baseUri, path, what, model, rounds).ConfigureAwait(false);
    }

    private static async Task<int> ModelsAsync(HttpClient http, string baseUri)
    {
        using var response = await http.GetAsync($"{baseUri}/v1/models").ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            return Fail($"HTTP {(int)response.StatusCode}: {body}");
        ConsoleUi.Write(ConsoleColor.Magenta, "Kairo · Jev models\n\n");
        using var doc = JsonDocument.Parse(body);
        var list = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement
            : doc.RootElement.TryGetProperty("models", out var m) ? m
            : doc.RootElement.TryGetProperty("data", out var d) ? d : doc.RootElement;
        foreach (var model in list.EnumerateArray())
        {
            Console.WriteLine($"  {Str(model, "name"),-28} {Str(model, "release_date"),-11} {Str(model, "description")}");
        }
        return 0;
    }

    private static async Task<int> BoxAsync(HttpClient http, string baseUri, string path, string what, string model, int rounds)
    {
        var image = Convert.ToBase64String(await File.ReadAllBytesAsync(path).ConfigureAwait(false));
        var format = Path.GetExtension(path).TrimStart('.').ToLowerInvariant() is "jpg" or "jpeg" ? "jpg" : "png";
        var body = new MemoryStream();
        using (var w = new Utf8JsonWriter(body))
        {
            w.WriteStartObject();
            w.WriteString("model", model);
            w.WriteString("state", "{{shot}}");
            w.WriteStartObject("questions");
            w.WriteStartObject("target");
            w.WriteString("type", "box");
            w.WriteString("instructions", what);
            w.WriteEndObject();
            w.WriteEndObject();
            w.WriteStartObject("assets");
            w.WriteStartObject("{{shot}}");
            w.WriteString("type", "image");
            w.WriteString("format", format);
            w.WriteString("data_base64", image);
            w.WriteEndObject();
            w.WriteEndObject();
            w.WriteEndObject();
        }

        ConsoleUi.Write(ConsoleColor.Magenta, "Kairo · Jev box");
        ConsoleUi.Write(ConsoleColor.DarkGray, $"  model {model} · \"{what}\" · {new FileInfo(path).Length / 1024} KB\n\n");
        var times = new List<double>();
        for (var i = 0; i < rounds; i++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUri}/v1/systemone") { Content = new ByteArrayContent(body.ToArray()) };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            request.Headers.Add("TypeSafe-Danger-Zone-Images", "true");
            var clock = Stopwatch.StartNew();
            using var response = await http.SendAsync(request).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            var ms = clock.Elapsed.TotalMilliseconds;
            times.Add(ms);
            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"  {ms,6:0} ms  HTTP {(int)response.StatusCode}: {Clip(text)}");
                continue;
            }
            Console.WriteLine($"  {ms,6:0} ms  {Describe(text)}");
        }
        times.Sort();
        Console.WriteLine($"\n  median {times[times.Count / 2]:0} ms");
        return 0;
    }

    private sealed record Box(double Left, double Top, double Right, double Bottom, double? Confidence);

    private static byte[] BoxBody(string imageBase64, string format, string model, string what)
    {
        var body = new MemoryStream();
        using (var w = new Utf8JsonWriter(body))
        {
            w.WriteStartObject();
            w.WriteString("model", model);
            w.WriteString("state", "{{shot}}");
            w.WriteStartObject("questions");
            w.WriteStartObject("target");
            w.WriteString("type", "box");
            w.WriteString("instructions", what);
            w.WriteEndObject();
            w.WriteEndObject();
            w.WriteStartObject("assets");
            w.WriteStartObject("{{shot}}");
            w.WriteString("type", "image");
            w.WriteString("format", format);
            w.WriteString("data_base64", imageBase64);
            w.WriteEndObject();
            w.WriteEndObject();
            w.WriteEndObject();
        }
        return body.ToArray();
    }

    private static async Task<(Box? Box, double Ms, string? Error, long Tokens)> AskAsync(HttpClient http, string baseUri, byte[] body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUri}/v1/systemone") { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.Add("TypeSafe-Danger-Zone-Images", "true");
        var clock = Stopwatch.StartNew();
        using var response = await http.SendAsync(request).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        var ms = clock.Elapsed.TotalMilliseconds;
        if (!response.IsSuccessStatusCode)
            return (null, ms, $"HTTP {(int)response.StatusCode}: {Clip(text)}", 0);
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        long tokens = root.TryGetProperty("usage", out var u) && u.TryGetProperty("input_tokens", out var t) ? t.GetInt64() : 0;
        if (!root.TryGetProperty("answers", out var answers) || !answers.TryGetProperty("target", out var a) || !a.TryGetProperty("box", out var b))
            return (null, ms, "no box in the answer: " + Clip(text), tokens);
        double G(string n) => b.TryGetProperty(n, out var v) ? v.GetDouble() : double.NaN;
        double? conf = a.TryGetProperty("confidence", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetDouble() : null;
        return (new Box(G("left"), G("top"), G("right"), G("bottom"), conf), ms, null, tokens);
    }

    /// <summary>Asks for every target of a picture whose true places are known, and scores the answers: overlap with the true box, how far the centre is, whether the centre lies inside the true box (a click would hit).</summary>
    private static async Task<int> EvalAsync(HttpClient http, string baseUri, string imagePath, string truthPath, string model, int rounds, bool zoom = false)
    {
        var image = Convert.ToBase64String(await File.ReadAllBytesAsync(imagePath).ConfigureAwait(false));
        var format = Path.GetExtension(imagePath).TrimStart('.').ToLowerInvariant() is "jpg" or "jpeg" ? "jpg" : "png";
        using var truth = JsonDocument.Parse(await File.ReadAllTextAsync(truthPath).ConfigureAwait(false));
        var width = truth.RootElement.TryGetProperty("w", out var wv) ? wv.GetDouble() : 1;
        var height = truth.RootElement.TryGetProperty("h", out var hv) ? hv.GetDouble() : 1;
        ConsoleUi.Write(ConsoleColor.Magenta, "Kairo · Jev box eval");
        ConsoleUi.Write(ConsoleColor.DarkGray, $"  model {model} · {width:0}x{height:0} · {rounds} rounds each\n\n");

        var hits = 0;
        var total = 0;
        var allMs = new List<double>();
        foreach (var target in truth.RootElement.GetProperty("targets").EnumerateObject())
        {
            var t = new Box(target.Value.GetProperty("left").GetDouble(), target.Value.GetProperty("top").GetDouble(), target.Value.GetProperty("right").GetDouble(), target.Value.GetProperty("bottom").GetDouble(), null);
            var line = new List<string>();
            for (var i = 0; i < rounds; i++)
            {
                var (box, ms, error, tokens) = await AskAsync(http, baseUri, BoxBody(image, format, model, target.Name)).ConfigureAwait(false);
                if (zoom && box is not null)
                {
                    // Second look: the region around the first answer, enlarged, so a small target is not a few pixels of a large picture.
                    var (second, ms2, _, tokens2) = await ZoomAsync(http, baseUri, imagePath, model, target.Name, box).ConfigureAwait(false);
                    ms += ms2;
                    tokens += tokens2;
                    box = second ?? box;
                }
                allMs.Add(ms);
                total++;
                if (box is null)
                {
                    line.Add($"{ms:0} ms FAIL {error}");
                    continue;
                }
                var iw = Math.Max(0, Math.Min(box.Right, t.Right) - Math.Max(box.Left, t.Left));
                var ih = Math.Max(0, Math.Min(box.Bottom, t.Bottom) - Math.Max(box.Top, t.Top));
                var inter = iw * ih;
                var union = (box.Right - box.Left) * (box.Bottom - box.Top) + (t.Right - t.Left) * (t.Bottom - t.Top) - inter;
                var iou = union <= 0 ? 0 : inter / union;
                var cx = (box.Left + box.Right) / 2;
                var cy = (box.Top + box.Bottom) / 2;
                var dx = (cx - (t.Left + t.Right) / 2) * width;
                var dy = (cy - (t.Top + t.Bottom) / 2) * height;
                var inside = cx >= t.Left && cx <= t.Right && cy >= t.Top && cy <= t.Bottom;
                hits += inside ? 1 : 0;
                line.Add($"{ms:0} ms  IoU {iou:0.00}  centre off by {Math.Sqrt(dx * dx + dy * dy):0} px  {(inside ? "HIT" : "miss")}  conf {box.Confidence:0.00}  tokens {tokens}");
            }
            Console.WriteLine($"  {target.Name}");
            foreach (var l in line)
                Console.WriteLine($"      {l}");
        }
        allMs.Sort();
        Console.WriteLine($"\n  a click on the centre would hit: {hits} of {total};  latency median {allMs[allMs.Count / 2]:0} ms, slowest {allMs[^1]:0} ms");
        return 0;
    }

    private static async Task<(Box? Box, double Ms, string? Error, long Tokens)> ZoomAsync(HttpClient http, string baseUri, string imagePath, string model, string what, Box first)
    {
        using var source = new System.Drawing.Bitmap(imagePath);
        var cx = (first.Left + first.Right) / 2;
        var cy = (first.Top + first.Bottom) / 2;
        // A window of a fifth of the picture's width (never smaller than 360 px), same shape as the picture, around the first answer.
        var wPx = Math.Max(360, source.Width * 0.22);
        var hPx = wPx * source.Height / source.Width;
        var left = Math.Clamp(cx * source.Width - wPx / 2, 0, source.Width - wPx);
        var top = Math.Clamp(cy * source.Height - hPx / 2, 0, source.Height - hPx);
        var scale = 2.0;
        using var crop = new System.Drawing.Bitmap((int)(wPx * scale), (int)(hPx * scale));
        using (var g = System.Drawing.Graphics.FromImage(crop))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.DrawImage(source, new System.Drawing.Rectangle(0, 0, crop.Width, crop.Height), new System.Drawing.RectangleF((float)left, (float)top, (float)wPx, (float)hPx), System.Drawing.GraphicsUnit.Pixel);
        }
        using var stream = new MemoryStream();
        crop.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
        var (box, ms, error, tokens) = await AskAsync(http, baseUri, BoxBody(Convert.ToBase64String(stream.ToArray()), "png", model, what)).ConfigureAwait(false);
        if (box is null)
            return (null, ms, error, tokens);
        double X(double f) => (left + f * wPx) / source.Width;
        double Y(double f) => (top + f * hPx) / source.Height;
        return (new Box(X(box.Left), Y(box.Top), X(box.Right), Y(box.Bottom), box.Confidence), ms, null, tokens);
    }

    private static string Describe(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var sb = new StringBuilder();
        if (root.TryGetProperty("answers", out var answers) && answers.TryGetProperty("target", out var a) && a.TryGetProperty("box", out var box))
        {
            double G(string n) => box.TryGetProperty(n, out var v) ? v.GetDouble() : double.NaN;
            sb.Append($"box left {G("left"):0.000} top {G("top"):0.000} right {G("right"):0.000} bottom {G("bottom"):0.000}");
            sb.Append($"  centre ({(G("left") + G("right")) / 2:0.000}, {(G("top") + G("bottom")) / 2:0.000})");
            if (a.TryGetProperty("confidence", out var c) && c.ValueKind == JsonValueKind.Number)
                sb.Append($"  confidence {c.GetDouble():0.00}");
        }
        else
        {
            sb.Append(Clip(json));
        }
        if (root.TryGetProperty("usage", out var u))
            sb.Append($"  tokens in {u.GetProperty("input_tokens")}");
        return sb.ToString();
    }

    private static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";

    private static string Clip(string text) => text.ReplaceLineEndings(" ") is { Length: > 400 } flat ? flat[..400] + "…" : text.ReplaceLineEndings(" ");

    private static int Fail(string message)
    {
        ConsoleUi.Write(ConsoleColor.Red, message + "\n");
        return 1;
    }
}
