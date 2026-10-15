using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Kairo.Core.Config;
using Kairo.Core.Secrets;
using Kairo.Host;

namespace Kairo.Cli;

/// <summary>
/// <c>kairo models list</c> shows what the configured gateway offers; <c>kairo models bench [ids…]</c> sends each model one tiny request
/// (a few tokens) over the streaming Messages API and prints how long the first words and the whole answer took. The key is read from the
/// usual places and never printed.
/// </summary>
internal static class ModelsCommand
{
    private sealed record Sample(bool Ok, double FirstOutputMs, double FirstTextMs, double TotalMs, string? Error, string? Usage = null);

    public static async Task<int> RunAsync(CliOptions options, string userConfigDir, ISecretStore secrets)
    {
        var verb = options.Positional.FirstOrDefault();
        if (verb is not ("list" or "bench"))
            return Fail("Usage: kairo models list | kairo models bench [--rounds n] [model ids…]");

        KairoConfig config;
        try
        {
            config = KairoConfig.Load(userConfigDir, userConfigDir, includeProject: false);
        }
        catch (FormatException ex)
        {
            return Fail(ex.Message);
        }
        var baseUrl = (options.BaseUrl ?? config.AnthropicBaseUrl ?? "https://api.anthropic.com").TrimEnd('/');
        if (baseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            baseUrl = baseUrl[..^3];
        var key = ProviderFactory.ApiKey(ProviderFactory.Anthropic, secrets);
        if (string.IsNullOrEmpty(key))
            return Fail("No API key for the anthropic provider (Settings or ANTHROPIC_API_KEY).");

        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.Add("x-api-key", key);
        http.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");

        List<string> models;
        try
        {
            models = await ListAsync(http, baseUrl).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException or TaskCanceledException)
        {
            return Fail($"Could not list models at {baseUrl}: {ex.Message}");
        }

        if (verb == "list")
        {
            ConsoleUi.Write(ConsoleColor.Magenta, "Kairo · models");
            ConsoleUi.Write(ConsoleColor.DarkGray, $"  {baseUrl}  ({models.Count})\n\n");
            foreach (var id in models.Order(StringComparer.OrdinalIgnoreCase))
                Console.WriteLine("  " + id);
            return 0;
        }

        var rounds = 2;
        string? image = null;
        var pad = 0;
        var wanted = new List<string>();
        foreach (var arg in options.Positional.Skip(1))
        {
            if (arg.StartsWith("pad:", StringComparison.Ordinal) && int.TryParse(arg[4..], out var words))
            {
                pad = Math.Clamp(words, 0, 100_000);
                continue;
            }
            if (arg.StartsWith("image:", StringComparison.Ordinal))
            {
                image = arg[6..];
                continue;
            }
            if (int.TryParse(arg, out var n) && n is >= 1 and <= 10)
                rounds = n;
            else
                wanted.Add(arg);
        }
        var chosen = wanted.Count == 0 ? models : wanted;

        ConsoleUi.Write(ConsoleColor.Magenta, "Kairo · models bench");
        ConsoleUi.Write(ConsoleColor.DarkGray, $"  {baseUrl}  {chosen.Count} models × {rounds} requests of a few tokens\n\n");
        var results = new List<(string Model, List<Sample> Samples)>();
        foreach (var model in chosen)
        {
            var samples = new List<Sample>();
            for (var i = 0; i < rounds; i++)
            {
                var sample = await OneAsync(http, baseUrl, model, image, pad).ConfigureAwait(false);
                samples.Add(sample);
                if (!sample.Ok)
                    break;
            }
            results.Add((model, samples));
            var first = samples[0];
            Console.WriteLine(first.Ok ? $"  {model,-44} {first.FirstTextMs,7:0} ms to the first word{(first.Usage is null ? "" : "   usage " + first.Usage)}" : $"  {model,-44} failed: {first.Error}");
        }

        var good = results.Where(r => r.Samples.All(s => s.Ok)).Select(r => (r.Model, First: Median(r.Samples.Select(s => s.FirstTextMs)), Total: Median(r.Samples.Select(s => s.TotalMs)), Think: Median(r.Samples.Select(s => s.FirstOutputMs)))).OrderBy(r => r.Total).ToList();
        Console.WriteLine("\nBy the time of the whole short answer (median):");
        foreach (var r in good)
            Console.WriteLine($"  {r.Model,-44} first output {r.Think,6:0} ms   first word {r.First,6:0} ms   whole {r.Total,6:0} ms");
        var failed = results.Count - good.Count;
        if (failed > 0)
            Console.WriteLine($"\n{failed} models did not answer (see above).");
        return 0;
    }

    private static async Task<List<string>> ListAsync(HttpClient http, string baseUrl)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var response = await http.GetAsync($"{baseUrl}/v1/models?limit=1000", cts.Token).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"HTTP {(int)response.StatusCode}: {Clip(body)}");
        using var doc = JsonDocument.Parse(body);
        var ids = new List<string>();
        if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            foreach (var m in data.EnumerateArray())
                if (m.TryGetProperty("id", out var id) && id.GetString() is { Length: > 0 } s)
                    ids.Add(s);
        return ids;
    }

    private static async Task<Sample> OneAsync(HttpClient http, string baseUrl, string model, string? image = null, int pad = 0)
    {
        var body = new MemoryStream();
        using (var w = new Utf8JsonWriter(body))
        {
            w.WriteStartObject();
            w.WriteString("model", model);
            w.WriteNumber("max_tokens", 24);
            w.WriteBoolean("stream", true);
            w.WriteStartArray("messages");
            w.WriteStartObject();
            w.WriteString("role", "user");
            if (image is null)
            {
                w.WriteString("content", (pad > 0 ? string.Join(' ', Enumerable.Repeat("lorem ipsum dolor sit amet", pad / 5)) + "\n\n" : "") + "Reply with the single word: ok");
            }
            else
            {
                // A picture with the question, to see how the gateway counts image tokens.
                w.WriteStartArray("content");
                w.WriteStartObject();
                w.WriteString("type", "image");
                w.WriteStartObject("source");
                w.WriteString("type", "base64");
                w.WriteString("media_type", "image/png");
                w.WriteString("data", Convert.ToBase64String(File.ReadAllBytes(image)));
                w.WriteEndObject();
                w.WriteEndObject();
                w.WriteStartObject();
                w.WriteString("type", "text");
                w.WriteString("text", "Reply with the single word: ok");
                w.WriteEndObject();
                w.WriteEndArray();
            }
            w.WriteEndObject();
            w.WriteEndArray();
            w.WriteEndObject();
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var clock = Stopwatch.StartNew();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/v1/messages") { Content = new ByteArrayContent(body.ToArray()) };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return new(false, 0, 0, 0, $"HTTP {(int)response.StatusCode} {Clip(await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false))}");

            double firstOutput = -1, firstText = -1;
            string? usage = null;
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false), Encoding.UTF8);
            string? line;
            while ((line = await reader.ReadLineAsync(cts.Token).ConfigureAwait(false)) is not null)
            {
                if (line.StartsWith("data:", StringComparison.Ordinal) && line.Contains("\"usage\"", StringComparison.Ordinal))
                    usage = UsageOf(line);
                if (!line.StartsWith("data:", StringComparison.Ordinal) || !line.Contains("content_block_delta", StringComparison.Ordinal))
                    continue;
                if (firstOutput < 0)
                    firstOutput = clock.Elapsed.TotalMilliseconds;
                if (firstText < 0 && line.Contains("\"text_delta\"", StringComparison.Ordinal))
                    firstText = clock.Elapsed.TotalMilliseconds;
            }
            var total = clock.Elapsed.TotalMilliseconds;
            if (firstOutput < 0)
                return new(false, 0, 0, total, "no text came back");
            return new(true, firstOutput, firstText < 0 ? total : firstText, total, null, usage);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            return new(false, 0, 0, clock.Elapsed.TotalMilliseconds, ex is TaskCanceledException ? "no answer in 60 s" : ex.Message);
        }
    }

    /// <summary>The token counters of a streamed event, as the gateway reports them (to see how it counts, e.g. cached input).</summary>
    private static string? UsageOf(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line[5..].Trim());
            var root = doc.RootElement;
            if (root.TryGetProperty("message", out var m) && m.TryGetProperty("usage", out var mu))
                return mu.GetRawText();
            return root.TryGetProperty("usage", out var u) ? u.GetRawText() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToList();
        return sorted.Count == 0 ? 0 : sorted[sorted.Count / 2];
    }

    private static string Clip(string text) => text.ReplaceLineEndings(" ") is { Length: > 160 } flat ? flat[..160] + "…" : text.ReplaceLineEndings(" ");

    private static int Fail(string message)
    {
        ConsoleUi.Write(ConsoleColor.Red, message + "\n");
        return 1;
    }
}
