using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Kairo.Core.Decisions;

namespace Kairo.Providers.Jev;

/// <summary>
/// Jev (TypeSafe AI) as a decision model: <c>POST /v1/systemone</c> with a state and named questions, answers back with probabilities
/// (https://docs.typesafe.ai/api). Built for speed, because a decision is only worth having if it arrives before the user would have
/// clicked: one long-lived HTTP/2 connection, every question of a step in one request, a hard deadline, and retries only while
/// they still fit in it.
/// </summary>
public sealed class JevDecisionModel : IDecisionModel, IWarmUp
{
    /// <summary>Pinned on purpose: confidence thresholds are tuned per release, and an alias can move under them.</summary>
    public const string DefaultModel = "jev-1.13.0";

    /// <summary>Pictures and box questions need 1.14 (1.13 takes pictures but cannot say where something is); pinned for the same reason.</summary>
    public const string DefaultVisionModel = "jev-1.14.0";

    /// <summary>A request with a picture takes longer than a text one (measured: 0.3 to 0.8 s, now and then 3 s).</summary>
    public static readonly TimeSpan DefaultImageDeadline = TimeSpan.FromSeconds(8);

    /// <summary>Jev's limit for all pictures of one request.</summary>
    internal const int MaxImageBytes = 24 * 1024 * 1024;
    public static readonly Uri DefaultBaseUri = new("https://api.typesafe.ai/");

    /// <summary>A decision that takes longer than this is of no use to a live loop; the caller escalates instead.</summary>
    public static readonly TimeSpan DefaultDeadline = TimeSpan.FromSeconds(3);

    public const int MaxOptions = 255;
    public const int MaxLevels = 10;

    /// <summary>The documented window is 32k tokens for the state plus the longest question; at about 3 bytes a token this stays under it.</summary>
    internal const int MaxRequestBytes = 100_000;

    private static readonly Lazy<HttpClient> SharedClient = new(() => CreateClient());

    private readonly HttpClient _http;
    private readonly Uri _endpoint;
    private readonly string _apiKey;
    private readonly string _model;
    private readonly string _visionModel;
    private readonly TimeSpan _deadline;

    public JevDecisionModel(string apiKey, HttpClient? http = null, Uri? baseUri = null, string? model = null, TimeSpan? deadline = null, string? visionModel = null)
    {
        _apiKey = apiKey;
        _http = http ?? SharedClient.Value;
        _endpoint = new Uri((baseUri ?? DefaultBaseUri).ToString().TrimEnd('/') + "/v1/systemone");
        _model = string.IsNullOrWhiteSpace(model) ? DefaultModel : model.Trim();
        _visionModel = string.IsNullOrWhiteSpace(visionModel) ? DefaultVisionModel : visionModel.Trim();
        _deadline = deadline is { } d && d > TimeSpan.Zero ? d : DefaultDeadline;
    }

    public string Id => "jev";

    /// <summary>How long the last decisions took, as the service answered them.</summary>
    public LatencyWindow Latency { get; } = new();

    /// <summary>
    /// Opens the connection (DNS, TLS, HTTP/2) ahead of the first decision, so that one does not pay for it. Safe to ignore failures:
    /// the first real request opens the connection itself.
    /// </summary>
    public async Task WarmUpAsync(CancellationToken ct = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, new Uri(_endpoint, "/"));
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or TimeoutException)
        {
            return;
        }

        // The first decision after a quiet spell has been seen to take seconds, not a third of a second: one real, tiny request, once per
        // process, takes that cost off the first step of a run.
        if (Interlocked.Exchange(ref _primed, 1) == 0)
        {
            try
            {
                await DecideAsync(DecisionRequest.Single(DecisionState.Text("warm-up"), "next",
                    new ChoiceQuestion("Which option is listed first?", [new ChoiceOption("a"), new ChoiceOption("b")])), ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is DecisionException or OperationCanceledException)
            {
            }
        }
    }

    private static int _primed;

    public async Task<DecisionResult> DecideAsync(DecisionRequest request, CancellationToken ct)
    {
        var withImages = request.Images is { Count: > 0 } || request.Questions.Values.Any(q => q is BoxQuestion);
        var body = BuildBody(request, withImages ? _visionModel : _model);
        var clock = Stopwatch.StartNew();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(withImages && _deadline < DefaultImageDeadline ? DefaultImageDeadline : _deadline);

        for (var attempt = 0; ; attempt++)
        {
            HttpResponseMessage response;
            try
            {
                using var message = new HttpRequestMessage(HttpMethod.Post, _endpoint) { Content = new ReadOnlyMemoryContent(body) };
                message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                if (withImages)
                    message.Headers.Add("TypeSafe-Danger-Zone-Images", "true"); // Jev asks for this on every request with a picture or a box question
                message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
                response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new DecisionException($"No answer from Jev within {_deadline.TotalSeconds:0.#} s.", DecisionFailure.Timeout);
            }
            catch (HttpRequestException ex)
            {
                // A dropped connection is worth one immediate retry on a fresh one if the deadline allows.
                if (attempt == 0 && clock.Elapsed < _deadline / 2)
                    continue;
                throw new DecisionException($"Jev is not reachable: {ex.Message}", DecisionFailure.Unavailable, ex);
            }

            using (response)
            {
                var status = (int)response.StatusCode;
                if (response.IsSuccessStatusCode)
                {
                    try
                    {
                        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false), cancellationToken: deadline.Token).ConfigureAwait(false);
                        var result = ParseResponse(doc.RootElement, request, clock.Elapsed);
                        Latency.Record(clock.Elapsed);
                        return result;
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        throw new DecisionException($"No answer from Jev within {_deadline.TotalSeconds:0.#} s.", DecisionFailure.Timeout);
                    }
                    catch (JsonException ex)
                    {
                        throw new DecisionException("Jev sent a reply that is not JSON.", DecisionFailure.Unavailable, ex);
                    }
                }

                // 429 and 529 clear quickly: back off a little, but only while the answer can still arrive in time.
                if (status is 429 or 529)
                {
                    var wait = BackoffFor(response, attempt);
                    if (attempt < 2 && clock.Elapsed + wait + TimeSpan.FromMilliseconds(150) < _deadline)
                    {
                        await Task.Delay(wait, deadline.Token).ConfigureAwait(false);
                        continue;
                    }
                    throw new DecisionException(status == 429 ? "Jev: rate limit reached." : "Jev is overloaded.", DecisionFailure.Busy);
                }

                var detail = await ReadErrorAsync(response).ConfigureAwait(false);
                throw status switch
                {
                    451 => new DecisionException("The fast model (Jev) is not available in this region (HTTP 451), usually because a VPN is off. Do not retry it: carry on without it (PcObserve and PcAct) and tell the user once that the fast model is unavailable.", DecisionFailure.Unavailable),
                    401 or 403 => new DecisionException("Jev refused the API key. Check it in the settings.", DecisionFailure.Unauthorized),
                    400 or 422 => new DecisionException($"Jev rejected the question: {detail}", DecisionFailure.Invalid),
                    404 => new DecisionException($"Jev has no such model or address ('{_model}', {_endpoint}). {detail}", DecisionFailure.Invalid),
                    _ => new DecisionException($"Jev error {status}. {detail}", DecisionFailure.Unavailable),
                };
            }
        }
    }

    // ---- Request ----

    internal static byte[] BuildBody(DecisionRequest request, string model)
    {
        Validate(request);

        var buffer = new ArrayBufferWriter<byte>(1024);
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WritePropertyName("state");
            request.State.Value.WriteTo(json);
            json.WriteString("model", model);
            json.WriteStartObject("questions");
            foreach (var (id, question) in request.Questions)
            {
                json.WriteStartObject(id);
                json.WriteString("instructions", question.Instructions);
                switch (question)
                {
                    case ChoiceQuestion choice:
                        json.WriteString("type", "choice");
                        json.WriteStartObject("criteria");
                        foreach (var option in choice.Options)
                        {
                            if (option.Description is null)
                                json.WriteNull(option.Key);
                            else
                                json.WriteString(option.Key, option.Description);
                        }
                        json.WriteEndObject();
                        break;
                    case ScoreQuestion score:
                        json.WriteString("type", "score");
                        json.WriteStartArray("criteria");
                        foreach (var level in score.Levels)
                            json.WriteStringValue(level);
                        json.WriteEndArray();
                        break;
                    case BoxQuestion:
                        json.WriteString("type", "box");
                        break;
                    case YesNoQuestion yesNo:
                        json.WriteString("type", "noul");
                        if (yesNo.WhenYes is not null || yesNo.WhenNo is not null)
                        {
                            json.WriteStartObject("criteria");
                            if (yesNo.WhenYes is not null)
                                json.WriteString("true", yesNo.WhenYes);
                            if (yesNo.WhenNo is not null)
                                json.WriteString("false", yesNo.WhenNo);
                            json.WriteEndObject();
                        }
                        break;
                }
                json.WriteEndObject();
            }
            json.WriteEndObject();
            if (request.Images is { Count: > 0 } images)
            {
                json.WriteStartObject("assets");
                foreach (var (key, image) in images)
                {
                    json.WriteStartObject(key);
                    json.WriteString("type", "image");
                    json.WriteString("format", image.Format);
                    json.WriteString("data_base64", Convert.ToBase64String(image.Data));
                    json.WriteEndObject();
                }
                json.WriteEndObject();
            }
            json.WriteEndObject();
        }

        // The window of 32k tokens is for the text; the pictures are counted apart, by their size.
        var pictureBytes = request.Images?.Values.Sum(i => (long)i.Data.Length * 4 / 3) ?? 0;
        if (buffer.WrittenCount - pictureBytes > MaxRequestBytes)
            throw new DecisionException(
                $"The request is too large for Jev (about {(buffer.WrittenCount - pictureBytes) / 3:N0} tokens, the limit is 32k for the state and the longest question). Shrink the state.",
                DecisionFailure.Invalid);
        return buffer.WrittenSpan.ToArray();
    }

    private static void Validate(DecisionRequest request)
    {
        if (request.Questions.Count == 0)
            throw new DecisionException("There is nothing to decide: no questions.", DecisionFailure.Invalid);

        if (request.Questions.Values.Any(q => q is BoxQuestion) && request.Images is not { Count: > 0 })
            throw new DecisionException("A box question needs a picture in the request.", DecisionFailure.Invalid);
        if (request.Images is { Count: > 0 } pictures)
        {
            if (pictures.Values.Any(i => i.Format is not ("png" or "jpg" or "jpeg") || i.Data.Length == 0))
                throw new DecisionException("Pictures must be png or jpg and not empty.", DecisionFailure.Invalid);
            if (pictures.Values.Sum(i => (long)i.Data.Length) > MaxImageBytes)
                throw new DecisionException("The pictures are larger than Jev takes in one request.", DecisionFailure.Invalid);
        }

        foreach (var (id, question) in request.Questions)
        {
            if (string.IsNullOrWhiteSpace(id))
                throw new DecisionException("A question has an empty id.", DecisionFailure.Invalid);
            if (string.IsNullOrWhiteSpace(question.Instructions))
                throw new DecisionException($"Question '{id}' has no instructions.", DecisionFailure.Invalid);

            switch (question)
            {
                case ChoiceQuestion { Options.Count: 0 }:
                    throw new DecisionException($"Question '{id}' offers no options.", DecisionFailure.Invalid);
                case ChoiceQuestion choice when choice.Options.Count > MaxOptions:
                    throw new DecisionException($"Question '{id}' has {choice.Options.Count} options; Jev takes at most {MaxOptions}. Filter the candidates first.", DecisionFailure.Invalid);
                case ChoiceQuestion choice when choice.Options.Any(o => string.IsNullOrEmpty(o.Key)) || choice.Options.Select(o => o.Key).Distinct().Count() != choice.Options.Count:
                    throw new DecisionException($"Question '{id}' has empty or repeated option keys.", DecisionFailure.Invalid);
                case ScoreQuestion score when score.Levels.Count is < 2 or > MaxLevels:
                    throw new DecisionException($"Question '{id}' has {score.Levels.Count} levels; a score needs 2 to {MaxLevels}.", DecisionFailure.Invalid);
            }
        }
    }

    // ---- Response ----

    internal static DecisionResult ParseResponse(JsonElement root, DecisionRequest request, TimeSpan elapsed)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("answers", out var answers) || answers.ValueKind != JsonValueKind.Object)
            throw new DecisionException("Jev's reply has no answers.");

        var result = new Dictionary<string, DecisionAnswer>();
        foreach (var (id, question) in request.Questions)
        {
            if (!answers.TryGetProperty(id, out var a) || a.ValueKind != JsonValueKind.Object)
                throw new DecisionException($"Jev did not answer question '{id}'.");

            result[id] = question switch
            {
                ChoiceQuestion choice => ParseChoice(id, a, choice),
                ScoreQuestion score => ParseScore(id, a, score),
                BoxQuestion => ParseBox(id, a),
                _ => new YesNoAnswer(Probability(id, a, "noul")),
            };
        }

        var model = root.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString()! : "jev";
        var tokens = root.TryGetProperty("usage", out var usage) && usage.TryGetProperty("input_tokens", out var t) && t.TryGetInt64(out var n) ? n : 0;
        return new DecisionResult(model, result, tokens, elapsed);
    }

    private static ChoiceAnswer ParseChoice(string id, JsonElement a, ChoiceQuestion question)
    {
        var choice = a.TryGetProperty("choice", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString()! : null;
        // The model is held to the offered options, but the core does not take that on trust: only a listed option can ever be acted on.
        if (choice is null || !question.Options.Any(o => o.Key == choice))
            throw new DecisionException($"Jev's answer to '{id}' ('{choice}') is not one of the options offered.");
        return new ChoiceAnswer(choice, Probability(id, a, "confidence"), Probabilities(a));
    }

    private static ScoreAnswer ParseScore(string id, JsonElement a, ScoreQuestion question)
    {
        if (!a.TryGetProperty("score", out var s) || !s.TryGetDouble(out var score) || double.IsNaN(score))
            throw new DecisionException($"Jev's answer to '{id}' has no score.");
        return new ScoreAnswer(Math.Clamp(score, 0, question.Levels.Count - 1), Probability(id, a, "confidence"), Probabilities(a));
    }

    private static BoxAnswer ParseBox(string id, JsonElement a)
    {
        if (!a.TryGetProperty("box", out var b) || b.ValueKind != JsonValueKind.Object)
            throw new DecisionException($"Jev's answer to '{id}' has no box.");
        double Edge(string name) =>
            b.TryGetProperty(name, out var v) && v.TryGetDouble(out var d) && !double.IsNaN(d) ? Math.Clamp(d, 0, 1) : throw new DecisionException($"Jev's box for '{id}' has no '{name}'.");
        var left = Edge("left");
        var top = Edge("top");
        var right = Edge("right");
        var bottom = Edge("bottom");
        if (right <= left || bottom <= top)
            throw new DecisionException($"Jev's box for '{id}' has no area.");
        double? confidence = a.TryGetProperty("confidence", out var c) && c.ValueKind == JsonValueKind.Number && c.TryGetDouble(out var cd) ? Math.Clamp(cd, 0, 1) : null;
        return new BoxAnswer(left, top, right, bottom, confidence);
    }

    private static double Probability(string id, JsonElement a, string field)
    {
        if (!a.TryGetProperty(field, out var v) || !v.TryGetDouble(out var p) || double.IsNaN(p))
            throw new DecisionException($"Jev's answer to '{id}' has no '{field}'.");
        return Math.Clamp(p, 0, 1);
    }

    private static Dictionary<string, double> Probabilities(JsonElement a)
    {
        var result = new Dictionary<string, double>();
        if (a.TryGetProperty("probabilities", out var p) && p.ValueKind == JsonValueKind.Object)
        {
            foreach (var entry in p.EnumerateObject())
            {
                if (entry.Value.TryGetDouble(out var value))
                    result[entry.Name] = Math.Clamp(value, 0, 1);
            }
        }
        return result;
    }

    // ---- Transport ----

    private static TimeSpan BackoffFor(HttpResponseMessage response, int attempt)
    {
        if (response.Headers.RetryAfter?.Delta is { } delta)
            return delta;
        // 40 ms, 80 ms, 160 ms with a little jitter so parallel callers do not retry in step.
        return TimeSpan.FromMilliseconds(40 * (1 << attempt) + Random.Shared.Next(0, 20));
    }

    private static async Task<string> ReadErrorAsync(HttpResponseMessage response)
    {
        try
        {
            var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            return text.Length > 300 ? text[..300] + "…" : text;
        }
        catch (HttpRequestException)
        {
            return "";
        }
    }

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            // Keep one warm HTTP/2 connection: a new TLS handshake costs more than the model does.
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(30),
            // A connection that the network dropped without a word looks alive until a request hangs on it; pinging often finds out sooner.
            KeepAlivePingDelay = TimeSpan.FromSeconds(10),
            KeepAlivePingTimeout = TimeSpan.FromSeconds(3),
            KeepAlivePingPolicy = HttpKeepAlivePingPolicy.Always,
            EnableMultipleHttp2Connections = true,
            ConnectTimeout = TimeSpan.FromSeconds(3),
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
        };
        return new HttpClient(handler, disposeHandler: true)
        {
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
            Timeout = Timeout.InfiniteTimeSpan, // the deadline of each decision is enforced there, not here
        };
    }
}

/// <summary>The last N request times, for p50/p95 numbers in diagnostics and settings.</summary>
public sealed class LatencyWindow(int capacity = 64)
{
    private readonly Queue<double> _samples = new();
    private readonly object _gate = new();

    public void Record(TimeSpan elapsed)
    {
        lock (_gate)
        {
            _samples.Enqueue(elapsed.TotalMilliseconds);
            while (_samples.Count > capacity)
                _samples.Dequeue();
        }
    }

    public LatencySnapshot Snapshot()
    {
        double[] sorted;
        lock (_gate)
            sorted = [.. _samples.Order()];
        if (sorted.Length == 0)
            return new LatencySnapshot(0, 0, 0, 0);
        return new LatencySnapshot(sorted.Length, At(sorted, 0.5), At(sorted, 0.95), sorted[^1]);
    }

    private static double At(double[] sorted, double q) => sorted[Math.Min(sorted.Length - 1, (int)Math.Ceiling(q * sorted.Length) - 1)];
}

public sealed record LatencySnapshot(int Count, double P50Ms, double P95Ms, double MaxMs);
