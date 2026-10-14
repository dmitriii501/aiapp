using System.Text.Json;
using Kairo.Core.Models;
using Kairo.Core.Tests.Fakes;
using Kairo.Providers.Anthropic;
using Kairo.Providers.OpenAICompatible;

namespace Kairo.Core.Tests;

public sealed class ImageResultTests
{
    private static readonly ToolImage Picture = new("image/png", Convert.ToBase64String([1, 2, 3, 4]));

    private static ChatMessage Call(string id) => new(ChatRole.Assistant, [new ToolCallPart(id, "PcScreenshot", JsonDocument.Parse("{}").RootElement.Clone())]);

    private static ChatMessage Shot(string id, ToolImage? image) =>
        new(ChatRole.User, [new ToolResultPart(id, "Screenshot of the window.", Images: image is null ? null : [image])]);

    private static async Task Run(IModelProvider provider, params ChatMessage[] history)
    {
        var request = new ModelRequest("m", [ChatMessage.UserText("look"), .. history], "sys");
        await foreach (var _ in provider.StreamAsync(request))
        {
        }
    }

    private const string AnthropicReply = """
        event: message_start
        data: {"type":"message_start","message":{"id":"m","type":"message","role":"assistant","model":"m","content":[],"stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":1,"output_tokens":1}}}

        event: message_delta
        data: {"type":"message_delta","delta":{"stop_reason":"end_turn","stop_sequence":null},"usage":{"output_tokens":1}}

        event: message_stop
        data: {"type":"message_stop"}


        """;

    private const string OpenAiReply = """
        data: {"choices":[{"index":0,"delta":{"content":"ok"},"finish_reason":"stop"}]}

        data: [DONE]


        """;

    [Fact]
    public async Task Anthropic_gets_the_picture_inside_the_tool_result_after_its_text()
    {
        var handler = new FakeHttpHandler(AnthropicReply);

        await Run(new AnthropicProvider("k", new HttpClient(handler)), Call("t1"), Shot("t1", Picture));

        var messages = JsonDocument.Parse(handler.RequestBody!).RootElement.GetProperty("messages");
        var result = messages[messages.GetArrayLength() - 1].GetProperty("content")[0];
        Assert.Equal("tool_result", result.GetProperty("type").GetString());
        var content = result.GetProperty("content");
        Assert.Equal("text", content[0].GetProperty("type").GetString());
        Assert.Equal("Screenshot of the window.", content[0].GetProperty("text").GetString());
        var image = content[1];
        Assert.Equal("image", image.GetProperty("type").GetString());
        Assert.Equal("base64", image.GetProperty("source").GetProperty("type").GetString());
        Assert.Equal("image/png", image.GetProperty("source").GetProperty("media_type").GetString());
        Assert.Equal(Picture.Base64, image.GetProperty("source").GetProperty("data").GetString());
    }

    [Fact]
    public async Task Anthropic_without_a_picture_still_sends_plain_text_results()
    {
        var handler = new FakeHttpHandler(AnthropicReply);

        await Run(new AnthropicProvider("k", new HttpClient(handler)), Call("t1"), Shot("t1", null));

        var messages = JsonDocument.Parse(handler.RequestBody!).RootElement.GetProperty("messages");
        var result = messages[messages.GetArrayLength() - 1].GetProperty("content")[0];
        Assert.Equal("Screenshot of the window.", result.GetProperty("content").GetString());
    }

    [Fact]
    public async Task OpenAI_compatible_follows_the_tool_message_with_a_user_message_that_carries_the_picture()
    {
        var handler = new FakeHttpHandler(OpenAiReply);

        await Run(new OpenAICompatibleProvider(new HttpClient(handler), new Uri("http://x/v1/"), apiKey: null), Call("t1"), Shot("t1", Picture));

        var messages = JsonDocument.Parse(handler.RequestBody!).RootElement.GetProperty("messages");
        var count = messages.GetArrayLength();
        var tool = messages[count - 2];
        var user = messages[count - 1];
        Assert.Equal("tool", tool.GetProperty("role").GetString());
        Assert.Equal("Screenshot of the window.", tool.GetProperty("content").GetString());
        Assert.Equal("user", user.GetProperty("role").GetString());
        var parts = user.GetProperty("content");
        Assert.Contains("t1", parts[0].GetProperty("text").GetString());
        Assert.Equal($"data:image/png;base64,{Picture.Base64}", parts[1].GetProperty("image_url").GetProperty("url").GetString());
    }

    [Fact]
    public void Only_the_newest_pictures_stay_attached_and_the_stored_history_is_not_touched()
    {
        var history = new List<ChatMessage>();
        for (var i = 1; i <= 4; i++)
        {
            history.Add(Call($"t{i}"));
            history.Add(Shot($"t{i}", Picture));
        }

        var sent = ModelImages.ForRequest(history);

        var attached = sent.SelectMany(m => m.Parts.OfType<ToolResultPart>()).Select(r => r.Images is { Count: > 0 }).ToList();
        Assert.Equal([false, false, true, true], attached);
        Assert.Contains("no longer attached", sent[1].Parts.OfType<ToolResultPart>().Single().Content);
        Assert.DoesNotContain("no longer attached", sent[7].Parts.OfType<ToolResultPart>().Single().Content);
        // The conversation itself keeps every picture; only the request is lighter.
        Assert.All(history.SelectMany(m => m.Parts.OfType<ToolResultPart>()), r => Assert.NotNull(r.Images));
    }

    [Fact]
    public void A_history_with_few_pictures_is_sent_as_it_is()
    {
        var history = new List<ChatMessage> { Call("t1"), Shot("t1", Picture), Call("t2"), Shot("t2", Picture) };

        Assert.Same(history, ModelImages.ForRequest(history));
    }

    [Fact]
    public void Pictures_are_not_saved_with_the_session_and_the_text_is()
    {
        var message = Shot("t1", Picture);

        var json = JsonSerializer.Serialize(message, Kairo.Core.Sessions.SessionJsonContext.Default.ChatMessage);
        var back = JsonSerializer.Deserialize(json, Kairo.Core.Sessions.SessionJsonContext.Default.ChatMessage)!;

        Assert.DoesNotContain(Picture.Base64, json);
        var result = Assert.IsType<ToolResultPart>(back.Parts[0]);
        Assert.Equal("Screenshot of the window.", result.Content);
        Assert.Null(result.Images);
    }
}
