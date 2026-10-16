using System.Net;
using System.Text;
using System.Text.Json;
using Kairo.Core.Decisions;
using Kairo.Providers.Jev;

namespace Kairo.Core.Tests;

/// <summary>Jev 1.14: pictures in a request and box questions (where is this in the picture).</summary>
public sealed class JevVisionTests
{
    private const string BoxReply = """
        {
          "model": "jev-1.14.0",
          "answers": {
            "f0": { "type": "box", "box": { "top": 0.2, "bottom": 0.66, "left": 0.19, "right": 0.68 }, "confidence": 0.92 },
            "v0": { "type": "noul", "noul": 0.97 }
          },
          "usage": { "input_tokens": 3755, "output_tokens": 21 }
        }
        """;

    private const string TextReply = """
        { "model": "jev-1.13.0", "answers": { "q": { "type": "noul", "noul": 0.5 } }, "usage": { "input_tokens": 5, "output_tokens": 1 } }
        """;

    private sealed class Recorder(string reply) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(reply, Encoding.UTF8, "application/json") };
        }
    }

    private static DecisionRequest WithPicture(byte[]? data = null) => new(
        DecisionState.Json("""{"screen":"shot-1"}"""),
        new Dictionary<string, DecisionQuestion>
        {
            ["f0"] = new BoxQuestion("the Save button in `screen`"),
            ["v0"] = new YesNoQuestion("Is there a Save button in `screen`?"),
        },
        new Dictionary<string, DecisionImage> { ["shot-1"] = new("png", data ?? [137, 80, 78, 71, 1, 2, 3]) });

    private static JevDecisionModel Model(HttpMessageHandler handler, string? visionModel = null) =>
        new("key-123", new HttpClient(handler), visionModel: visionModel);

    [Fact]
    public async Task A_picture_and_a_box_question_go_with_the_images_header_to_the_vision_model_and_the_box_comes_back()
    {
        var handler = new Recorder(BoxReply);

        var result = await Model(handler).DecideAsync(WithPicture(), default);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("true", Assert.Single(request.Headers.GetValues("TypeSafe-Danger-Zone-Images")));
        var body = JsonDocument.Parse(handler.Bodies[0]).RootElement;
        Assert.Equal("jev-1.14.0", body.GetProperty("model").GetString()); // not the pinned text model
        Assert.Equal("box", body.GetProperty("questions").GetProperty("f0").GetProperty("type").GetString());
        var asset = body.GetProperty("assets").GetProperty("shot-1");
        Assert.Equal("image", asset.GetProperty("type").GetString());
        Assert.Equal("png", asset.GetProperty("format").GetString());
        Assert.Equal(Convert.ToBase64String([137, 80, 78, 71, 1, 2, 3]), asset.GetProperty("data_base64").GetString());

        var box = result.Box("f0");
        Assert.Equal((0.19, 0.2, 0.68, 0.66, 0.92), (box.Left, box.Top, box.Right, box.Bottom, box.Confidence));
        Assert.Equal(0.435, box.CentreX, 3);
        Assert.Equal(0.97, result.YesNo("v0").Probability);
        Assert.Equal(3755, result.InputTokens);
    }

    [Fact]
    public async Task A_text_only_request_keeps_the_text_model_and_sends_no_images_header_or_assets()
    {
        var handler = new Recorder(TextReply);

        await Model(handler).DecideAsync(DecisionRequest.Single(DecisionState.Text("hi"), "q", new YesNoQuestion("Is it a greeting?")), default);

        Assert.DoesNotContain(handler.Requests[0].Headers, h => h.Key == "TypeSafe-Danger-Zone-Images");
        var body = JsonDocument.Parse(handler.Bodies[0]).RootElement;
        Assert.Equal("jev-1.13.0", body.GetProperty("model").GetString());
        Assert.False(body.TryGetProperty("assets", out _));
    }

    [Fact]
    public async Task The_vision_model_can_be_pinned_separately()
    {
        var handler = new Recorder(BoxReply);

        await Model(handler, visionModel: "jev-preview").DecideAsync(WithPicture(), default);

        Assert.Equal("jev-preview", JsonDocument.Parse(handler.Bodies[0]).RootElement.GetProperty("model").GetString());
    }

    [Fact]
    public async Task A_box_question_without_a_picture_and_a_bad_picture_are_refused_before_anything_is_sent()
    {
        var handler = new Recorder(BoxReply);
        var noPicture = new DecisionRequest(DecisionState.Text("x"), new Dictionary<string, DecisionQuestion> { ["f0"] = new BoxQuestion("the button") });
        var gif = new DecisionRequest(DecisionState.Json("""{"screen":"s"}"""), new Dictionary<string, DecisionQuestion> { ["f0"] = new BoxQuestion("the button in `screen`") },
            new Dictionary<string, DecisionImage> { ["s"] = new("gif", [1]) });

        var a = await Assert.ThrowsAsync<DecisionException>(() => Model(handler).DecideAsync(noPicture, default));
        var b = await Assert.ThrowsAsync<DecisionException>(() => Model(handler).DecideAsync(gif, default));

        Assert.Equal(DecisionFailure.Invalid, a.Kind);
        Assert.Contains("needs a picture", a.Message);
        Assert.Contains("png or jpg", b.Message);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task The_size_of_the_pictures_does_not_count_against_the_text_limit()
    {
        var handler = new Recorder(BoxReply);

        await Model(handler).DecideAsync(WithPicture(new byte[400_000]), default); // 530 KB as base64, far above the 100 KB text limit

        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("""{ "type": "box", "box": { "top": 0.5, "bottom": 0.4, "left": 0.1, "right": 0.2 }, "confidence": 0.5 }""", "no area")]
    [InlineData("""{ "type": "box", "confidence": 0.5 }""", "no box")]
    [InlineData("""{ "type": "box", "box": { "top": 0.1, "bottom": 0.4, "left": 0.1 }, "confidence": 0.5 }""", "no 'right'")]
    public async Task A_box_that_makes_no_sense_is_an_error_not_a_click(string answer, string message)
    {
        var handler = new Recorder("{ \"model\": \"jev-1.14.0\", \"answers\": { \"f0\": " + answer + ", \"v0\": { \"type\": \"noul\", \"noul\": 0.9 } }, \"usage\": { \"input_tokens\": 1, \"output_tokens\": 1 } }");

        var ex = await Assert.ThrowsAsync<DecisionException>(() => Model(handler).DecideAsync(WithPicture(), default));

        Assert.Contains(message, ex.Message);
    }

    [Fact]
    public async Task A_box_without_confidence_is_still_a_box_and_edges_are_kept_inside_the_picture()
    {
        var handler = new Recorder("""{ "model": "jev-1.14.0", "answers": { "f0": { "type": "box", "box": { "top": -0.2, "bottom": 1.3, "left": 0.1, "right": 0.3 }, "confidence": null }, "v0": { "type": "noul", "noul": 0.9 } }, "usage": { "input_tokens": 1, "output_tokens": 1 } }""");

        var box = (await Model(handler).DecideAsync(WithPicture(), default)).Box("f0");

        Assert.Null(box.Confidence);
        Assert.Equal((0.0, 1.0), (box.Top, box.Bottom));
    }
}
