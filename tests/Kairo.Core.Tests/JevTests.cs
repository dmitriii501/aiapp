using System.Net;
using System.Text;
using System.Text.Json;
using Kairo.Core.Decisions;
using Kairo.Providers.Jev;

namespace Kairo.Core.Tests;

public sealed class JevTests
{
    // The example from https://docs.typesafe.ai/introduction/quickstart, as the service returns it.
    private const string DocumentedReply = """
        {
          "model": "jev-1.13.0",
          "answers": {
            "department": { "type": "choice", "choice": "technical", "confidence": 1.0, "probabilities": { "technical": 1.0, "billing": 0.0, "sales": 0.0 } },
            "frustration": { "type": "score", "score": 0.67, "confidence": 0.5, "legend": { "0": "Calm", "1": "Frustrated", "2": "Very angry" }, "probabilities": { "0": 0.33, "1": 0.67, "2": 0.0 } },
            "is_urgent": { "type": "noul", "noul": 0.99 }
          },
          "usage": { "input_tokens": 428, "output_tokens": 73 }
        }
        """;

    private sealed class Script(params Func<HttpRequestMessage, Task<HttpResponseMessage>>[] steps) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var index = Requests.Count;
            Requests.Add(request);
            Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct));
            return await steps[Math.Min(index, steps.Length - 1)](request);
        }
    }

    /// <summary>A server that never answers; unlike a plain delay it stops when the request is cancelled, as a real connection does.</summary>
    private sealed class Hanging : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private static Func<HttpRequestMessage, Task<HttpResponseMessage>> Reply(HttpStatusCode status, string body, Action<HttpResponseMessage>? configure = null) => _ =>
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        configure?.Invoke(response);
        return Task.FromResult(response);
    };

    private static DecisionRequest Triage() => new(
        DecisionState.Json("""{"message":"The API returns 500 errors"}"""),
        new Dictionary<string, DecisionQuestion>
        {
            ["department"] = new ChoiceQuestion("Which team should handle this",
                [new("billing", "Payment issues"), new("technical", "Bugs"), new("sales", null)]),
            ["frustration"] = new ScoreQuestion("How frustrated", ["Calm", "Frustrated", "Very angry"]),
            ["is_urgent"] = new YesNoQuestion("The message conveys urgency", WhenYes: "time-sensitive"),
        });

    private static JevDecisionModel Model(HttpMessageHandler handler, TimeSpan? deadline = null, Uri? baseUri = null, string? model = null) =>
        new("key-123", new HttpClient(handler), baseUri, model, deadline);

    [Fact]
    public async Task The_request_has_the_shape_the_api_documents()
    {
        var handler = new Script(Reply(HttpStatusCode.OK, DocumentedReply));

        await Model(handler).DecideAsync(Triage(), default);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://api.typesafe.ai/v1/systemone", request.RequestUri!.ToString());
        Assert.Equal("Bearer key-123", request.Headers.Authorization!.ToString());
        var body = JsonDocument.Parse(handler.Bodies[0]).RootElement;
        Assert.Equal("jev-1.13.0", body.GetProperty("model").GetString());
        Assert.Equal("The API returns 500 errors", body.GetProperty("state").GetProperty("message").GetString());
        var q = body.GetProperty("questions");
        var department = q.GetProperty("department");
        Assert.Equal("choice", department.GetProperty("type").GetString());
        Assert.Equal("Which team should handle this", department.GetProperty("instructions").GetString());
        Assert.Equal("Bugs", department.GetProperty("criteria").GetProperty("technical").GetString());
        Assert.Equal(JsonValueKind.Null, department.GetProperty("criteria").GetProperty("sales").ValueKind);
        var frustration = q.GetProperty("frustration");
        Assert.Equal("score", frustration.GetProperty("type").GetString());
        Assert.Equal(["Calm", "Frustrated", "Very angry"], frustration.GetProperty("criteria").EnumerateArray().Select(e => e.GetString()));
        var urgent = q.GetProperty("is_urgent");
        Assert.Equal("noul", urgent.GetProperty("type").GetString());
        Assert.Equal("time-sensitive", urgent.GetProperty("criteria").GetProperty("true").GetString());
        Assert.False(urgent.GetProperty("criteria").TryGetProperty("false", out _));
    }

    [Fact]
    public async Task A_yes_no_question_without_criteria_sends_none()
    {
        var handler = new Script(Reply(HttpStatusCode.OK, """{"model":"jev-1.13.0","answers":{"q":{"type":"noul","noul":0.8}}}"""));

        var yes = await Model(handler).YesAsync(DecisionState.Text("hello"), "Is this a greeting?", default);

        Assert.Equal(0.8, yes);
        var body = JsonDocument.Parse(handler.Bodies[0]).RootElement;
        Assert.Equal("hello", body.GetProperty("state").GetString());
        Assert.False(body.GetProperty("questions").GetProperty("q").TryGetProperty("criteria", out _));
    }

    [Fact]
    public async Task The_documented_reply_becomes_typed_answers()
    {
        var result = await Model(new Script(Reply(HttpStatusCode.OK, DocumentedReply))).DecideAsync(Triage(), default);

        Assert.Equal("jev-1.13.0", result.Model);
        Assert.Equal(428, result.InputTokens);
        var department = result.Choice("department");
        Assert.Equal("technical", department.Choice);
        Assert.Equal(1.0, department.Confidence);
        Assert.Equal(3, department.Probabilities.Count);
        var frustration = result.Score("frustration");
        Assert.Equal(0.67, frustration.Score, 5);
        Assert.Equal(0.5, frustration.Confidence);
        Assert.Equal(0.99, result.YesNo("is_urgent").Probability);
    }

    [Fact]
    public async Task A_choice_outside_the_offered_options_is_refused()
    {
        var reply = DocumentedReply.Replace("\"choice\": \"technical\"", "\"choice\": \"delete-everything\"");

        var ex = await Assert.ThrowsAsync<DecisionException>(() => Model(new Script(Reply(HttpStatusCode.OK, reply))).DecideAsync(Triage(), default));

        Assert.Contains("not one of the options", ex.Message);
    }

    [Fact]
    public async Task A_missing_answer_is_an_error_not_a_guess()
    {
        var reply = """{"model":"jev-1.13.0","answers":{"department":{"type":"choice","choice":"billing","confidence":0.9,"probabilities":{}}}}""";

        var ex = await Assert.ThrowsAsync<DecisionException>(() => Model(new Script(Reply(HttpStatusCode.OK, reply))).DecideAsync(Triage(), default));

        Assert.Contains("frustration", ex.Message);
    }

    [Fact]
    public async Task Numbers_are_kept_inside_their_ranges()
    {
        var reply = """{"model":"m","answers":{"a":{"type":"choice","choice":"x","confidence":1.4,"probabilities":{"x":2}},"b":{"type":"score","score":9,"confidence":-1,"probabilities":{}}}}""";
        var request = new DecisionRequest(DecisionState.Text("s"), new Dictionary<string, DecisionQuestion>
        {
            ["a"] = new ChoiceQuestion("pick", [new("x"), new("y")]),
            ["b"] = new ScoreQuestion("rate", ["lo", "hi"]),
        });

        var result = await Model(new Script(Reply(HttpStatusCode.OK, reply))).DecideAsync(request, default);

        Assert.Equal(1.0, result.Choice("a").Confidence);
        Assert.Equal(1.0, result.Choice("a").Probabilities["x"]);
        Assert.Equal(1.0, result.Score("b").Score);
        Assert.Equal(0.0, result.Score("b").Confidence);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, DecisionFailure.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden, DecisionFailure.Unauthorized)]
    [InlineData(HttpStatusCode.UnprocessableEntity, DecisionFailure.Invalid)]
    [InlineData(HttpStatusCode.NotFound, DecisionFailure.Invalid)]
    [InlineData(HttpStatusCode.InternalServerError, DecisionFailure.Unavailable)]
    public async Task Errors_say_what_kind_of_failure_they_are(HttpStatusCode status, DecisionFailure kind)
    {
        var handler = new Script(Reply(status, """{"detail":"questions.q.criteria is required"}"""));

        var ex = await Assert.ThrowsAsync<DecisionException>(() => Model(handler).DecideAsync(Triage(), default));

        Assert.Equal(kind, ex.Kind);
        Assert.Single(handler.Requests); // never retried: the answer would be the same
    }

    [Fact]
    public async Task An_invalid_question_reports_what_the_service_said()
    {
        var handler = new Script(Reply(HttpStatusCode.UnprocessableEntity, """{"detail":"questions.q.criteria is required"}"""));

        var ex = await Assert.ThrowsAsync<DecisionException>(() => Model(handler).DecideAsync(Triage(), default));

        Assert.Contains("criteria is required", ex.Message);
    }

    [Theory]
    [InlineData(429)]
    [InlineData(529)]
    public async Task A_busy_service_is_retried_quickly_and_the_answer_still_arrives(int status)
    {
        var handler = new Script(Reply((HttpStatusCode)status, "{}"), Reply((HttpStatusCode)status, "{}"), Reply(HttpStatusCode.OK, DocumentedReply));

        var result = await Model(handler).DecideAsync(Triage(), default);

        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal("technical", result.Choice("department").Choice);
    }

    [Fact]
    public async Task A_service_that_stays_busy_ends_in_Busy_inside_the_deadline()
    {
        var handler = new Script(Reply((HttpStatusCode)429, "{}"));
        var started = DateTime.UtcNow;

        var ex = await Assert.ThrowsAsync<DecisionException>(() => Model(handler, deadline: TimeSpan.FromSeconds(1)).DecideAsync(Triage(), default));

        Assert.Equal(DecisionFailure.Busy, ex.Kind);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(1.5));
    }

    [Fact]
    public async Task A_retry_is_not_made_when_it_cannot_finish_in_time()
    {
        var handler = new Script(Reply((HttpStatusCode)429, "{}", r => r.Headers.RetryAfter = new(TimeSpan.FromSeconds(30))));

        var ex = await Assert.ThrowsAsync<DecisionException>(() => Model(handler).DecideAsync(Triage(), default));

        Assert.Equal(DecisionFailure.Busy, ex.Kind);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task No_answer_within_the_deadline_is_a_timeout_not_a_hang()
    {
        var slow = new Hanging();
        var started = DateTime.UtcNow;

        var ex = await Assert.ThrowsAsync<DecisionException>(() => Model(slow, deadline: TimeSpan.FromMilliseconds(200)).DecideAsync(Triage(), default));

        Assert.Equal(DecisionFailure.Timeout, ex.Kind);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task Cancelling_is_not_reported_as_a_timeout()
    {
        var slow = new Hanging();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Model(slow).DecideAsync(Triage(), cts.Token));
    }

    [Fact]
    public async Task A_dropped_connection_is_retried_once_on_a_fresh_one()
    {
        var handler = new Script(_ => throw new HttpRequestException("connection reset"), Reply(HttpStatusCode.OK, DocumentedReply));

        var result = await Model(handler).DecideAsync(Triage(), default);

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("technical", result.Choice("department").Choice);
    }

    [Fact]
    public async Task A_network_that_stays_down_is_Unavailable()
    {
        var handler = new Script(_ => throw new HttpRequestException("no route"));

        var ex = await Assert.ThrowsAsync<DecisionException>(() => Model(handler).DecideAsync(Triage(), default));

        Assert.Equal(DecisionFailure.Unavailable, ex.Kind);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Bad_questions_are_caught_before_anything_is_sent()
    {
        var handler = new Script(Reply(HttpStatusCode.OK, DocumentedReply));
        var jev = Model(handler);
        DecisionRequest One(DecisionQuestion q) => DecisionRequest.Single(DecisionState.Text("s"), "q", q);

        var tooMany = Enumerable.Range(0, 256).Select(i => new ChoiceOption($"o{i}")).ToList();
        var cases = new DecisionRequest[]
        {
            One(new ChoiceQuestion("pick", tooMany)),
            One(new ChoiceQuestion("pick", [])),
            One(new ChoiceQuestion("pick", [new("a"), new("a")])),
            One(new ChoiceQuestion("pick", [new("")])),
            One(new ScoreQuestion("rate", ["only one"])),
            One(new ScoreQuestion("rate", Enumerable.Range(0, 11).Select(i => $"l{i}").ToList())),
            One(new YesNoQuestion("  ")),
            new(DecisionState.Text("s"), new Dictionary<string, DecisionQuestion>()),
        };

        foreach (var request in cases)
        {
            var ex = await Assert.ThrowsAsync<DecisionException>(() => jev.DecideAsync(request, default));
            Assert.Equal(DecisionFailure.Invalid, ex.Kind);
        }
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Up_to_255_options_are_accepted()
    {
        var options = Enumerable.Range(0, 255).Select(i => new ChoiceOption($"o{i}", "d")).ToList();
        var handler = new Script(Reply(HttpStatusCode.OK, """{"model":"m","answers":{"q":{"type":"choice","choice":"o254","confidence":0.7,"probabilities":{}}}}"""));

        var answer = await Model(handler).ChooseAsync(DecisionState.Text("s"), new ChoiceQuestion("pick", options), default);

        Assert.Equal("o254", answer.Choice);
    }

    [Fact]
    public async Task A_state_too_big_for_the_window_is_refused_with_the_reason()
    {
        var handler = new Script(Reply(HttpStatusCode.OK, DocumentedReply));
        var huge = DecisionState.Text(new string('я', 60_000));

        var ex = await Assert.ThrowsAsync<DecisionException>(() => Model(handler).YesAsync(huge, "ok?", default));

        Assert.Equal(DecisionFailure.Invalid, ex.Kind);
        Assert.Contains("Shrink the state", ex.Message);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task A_gateway_address_and_a_pinned_model_are_used_as_given()
    {
        var handler = new Script(Reply(HttpStatusCode.OK, DocumentedReply));

        await Model(handler, baseUri: new Uri("https://gateway.example.com/typesafe/"), model: "jev-1.14.0").DecideAsync(Triage(), default);

        Assert.Equal("https://gateway.example.com/typesafe/v1/systemone", handler.Requests[0].RequestUri!.ToString());
        Assert.Equal("jev-1.14.0", JsonDocument.Parse(handler.Bodies[0]).RootElement.GetProperty("model").GetString());
    }

    [Fact]
    public async Task Latency_is_recorded_for_the_numbers_the_settings_show()
    {
        var handler = new Script(Reply(HttpStatusCode.OK, DocumentedReply));
        var jev = Model(handler);

        for (var i = 0; i < 5; i++)
            await jev.DecideAsync(Triage(), default);

        var snapshot = jev.Latency.Snapshot();
        Assert.Equal(5, snapshot.Count);
        Assert.True(snapshot.P50Ms <= snapshot.P95Ms && snapshot.P95Ms <= snapshot.MaxMs);
    }

    [Fact]
    public void The_latency_window_keeps_only_the_latest_samples()
    {
        var window = new LatencyWindow(capacity: 4);
        foreach (var ms in new[] { 1000, 10, 20, 30, 40 })
            window.Record(TimeSpan.FromMilliseconds(ms));

        var snapshot = window.Snapshot();

        Assert.Equal(4, snapshot.Count);
        Assert.Equal(40, snapshot.MaxMs);
        Assert.Equal(20, snapshot.P50Ms);
    }

    [Fact]
    public async Task Warming_up_never_throws()
    {
        var handler = new Script(_ => throw new HttpRequestException("offline"));

        await Model(handler).WarmUpAsync();

        Assert.Equal(HttpMethod.Head, Assert.Single(handler.Requests).Method);
    }
}
