using System.Text.Json;
using Kairo.Core.Models;
using Kairo.Core.Tests.Fakes;
using Kairo.Providers.Anthropic;
using Kairo.Providers.OpenAICompatible;

namespace Kairo.Core.Tests;

public class ProviderTests
{
    private static readonly ToolSpec ReadSpec = new("Read", "Reads a file.",
        JsonDocument.Parse("""{"type":"object","properties":{"file_path":{"type":"string"}},"required":["file_path"]}""").RootElement);

    private static ModelRequest Request(params ChatMessage[] history) =>
        new("test-model", history.Length > 0 ? history : [ChatMessage.UserText("Привет")], System: "Be brief.", Tools: [ReadSpec]);

    private static async Task<List<ModelStreamEvent>> Collect(IModelProvider provider, ModelRequest request)
    {
        var events = new List<ModelStreamEvent>();
        await foreach (var e in provider.StreamAsync(request))
            events.Add(e);
        return events;
    }

    // ---- Anthropic ----

    private const string AnthropicTextStream = """
        event: message_start
        data: {"type":"message_start","message":{"id":"msg_1","type":"message","role":"assistant","model":"test-model","content":[],"stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":12,"output_tokens":1,"cache_read_input_tokens":7}}}

        event: content_block_start
        data: {"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Привет"}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":", мир"}}

        event: content_block_stop
        data: {"type":"content_block_stop","index":0}

        event: message_delta
        data: {"type":"message_delta","delta":{"stop_reason":"end_turn","stop_sequence":null},"usage":{"output_tokens":5}}

        event: message_stop
        data: {"type":"message_stop"}


        """;

    [Fact]
    public async Task Anthropic_streams_text_and_sends_fallbacks_caching_and_eager_tools()
    {
        var handler = new FakeHttpHandler(AnthropicTextStream);

        var events = await Collect(new AnthropicProvider("test-key", new HttpClient(handler)), Request());

        Assert.Equal("Привет, мир", string.Concat(events.OfType<TextDelta>().Select(d => d.Text)));
        var done = Assert.IsType<ModelResponse>(events[^1]);
        Assert.Equal("end_turn", done.StopReason);
        Assert.Equal(new Usage(12, 5, 7), done.Usage);
        Assert.Equal("Привет, мир", done.Message.Text);

        var body = JsonDocument.Parse(handler.RequestBody!).RootElement;
        Assert.Equal("default", body.GetProperty("fallbacks").GetString());
        Assert.Equal("ephemeral", body.GetProperty("cache_control").GetProperty("type").GetString());
        Assert.Equal("Be brief.", body.GetProperty("system").GetString());
        var tool = body.GetProperty("tools")[0];
        Assert.Equal("Read", tool.GetProperty("name").GetString());
        Assert.True(tool.GetProperty("eager_input_streaming").GetBoolean());
        Assert.Equal("file_path", tool.GetProperty("input_schema").GetProperty("required")[0].GetString());
        Assert.Contains("server-side-fallback-2026-07-01", string.Join(",", handler.Request!.Headers.GetValues("anthropic-beta")));
    }

    [Fact]
    public async Task Anthropic_gateway_gets_a_plain_request_at_its_own_address()
    {
        var handler = new FakeHttpHandler(AnthropicTextStream);

        var events = await Collect(new AnthropicProvider("k", new HttpClient(handler), baseUrl: "https://gateway.example.com/v1/"), Request());

        Assert.Equal("Привет, мир", ((ModelResponse)events[^1]).Message.Text);
        Assert.Equal("https://gateway.example.com/v1/messages", handler.Request!.RequestUri!.GetLeftPart(UriPartial.Path));
        var body = JsonDocument.Parse(handler.RequestBody!).RootElement;
        Assert.False(body.TryGetProperty("fallbacks", out _));
        Assert.False(body.TryGetProperty("cache_control", out _));
        Assert.False(body.GetProperty("tools")[0].TryGetProperty("eager_input_streaming", out _));
        Assert.False(handler.Request.Headers.TryGetValues("anthropic-beta", out var betas) && betas.Any(b => b.Contains("fallback")));
    }

    [Theory]
    [InlineData("https://api.relaymodels.com/", "https://api.relaymodels.com")]
    [InlineData("https://api.relaymodels.com/v1", "https://api.relaymodels.com")]
    [InlineData(" https://x.dev/proxy/v1/ ", "https://x.dev/proxy")]
    public void Anthropic_base_url_drops_trailing_v1(string input, string expected) =>
        Assert.Equal(expected, AnthropicProvider.NormalizeBaseUrl(input));

    [Fact]
    public async Task Anthropic_assembles_thinking_and_streamed_tool_input()
    {
        const string sse = """
            event: message_start
            data: {"type":"message_start","message":{"id":"msg_2","type":"message","role":"assistant","model":"test-model","content":[],"stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":30,"output_tokens":1}}}

            event: content_block_start
            data: {"type":"content_block_start","index":0,"content_block":{"type":"thinking","thinking":"","signature":""}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"Need the file."}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":0,"delta":{"type":"signature_delta","signature":"sig-abc"}}

            event: content_block_stop
            data: {"type":"content_block_stop","index":0}

            event: content_block_start
            data: {"type":"content_block_start","index":1,"content_block":{"type":"tool_use","id":"toolu_1","name":"Read","input":{}}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":"{\"file_pa"}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":"th\": \"README.md\"}"}}

            event: content_block_stop
            data: {"type":"content_block_stop","index":1}

            event: message_delta
            data: {"type":"message_delta","delta":{"stop_reason":"tool_use","stop_sequence":null},"usage":{"output_tokens":20}}

            event: message_stop
            data: {"type":"message_stop"}


            """;

        var events = await Collect(new AnthropicProvider("k", new HttpClient(new FakeHttpHandler(sse))), Request());

        var response = Assert.IsType<ModelResponse>(events[^1]);
        Assert.Equal("tool_use", response.StopReason);
        Assert.Equal(new ThinkingPart("Need the file.", "sig-abc"), response.Message.Parts[0]);
        var call = Assert.IsType<ToolCallPart>(response.Message.Parts[1]);
        Assert.Equal(("toolu_1", "Read"), (call.Id, call.Name));
        Assert.Equal("README.md", call.Input.GetProperty("file_path").GetString());
    }

    [Fact]
    public async Task Anthropic_sends_history_with_thinking_tool_calls_and_results()
    {
        var handler = new FakeHttpHandler(AnthropicTextStream);
        var input = JsonDocument.Parse("""{"file_path":"a.txt"}""").RootElement;
        var history = new[]
        {
            ChatMessage.UserText("read a.txt"),
            new ChatMessage(ChatRole.Assistant, [new ThinkingPart("hmm", "sig"), new ToolCallPart("toolu_9", "Read", input)]),
            new ChatMessage(ChatRole.User, [new ToolResultPart("toolu_9", "file missing", IsError: true)]),
        };

        await Collect(new AnthropicProvider("k", new HttpClient(handler)), Request(history));

        var messages = JsonDocument.Parse(handler.RequestBody!).RootElement.GetProperty("messages");
        var assistant = messages[1].GetProperty("content");
        Assert.Equal("thinking", assistant[0].GetProperty("type").GetString());
        Assert.Equal("sig", assistant[0].GetProperty("signature").GetString());
        Assert.Equal("tool_use", assistant[1].GetProperty("type").GetString());
        Assert.Equal("a.txt", assistant[1].GetProperty("input").GetProperty("file_path").GetString());
        var result = messages[2].GetProperty("content")[0];
        Assert.Equal("tool_result", result.GetProperty("type").GetString());
        Assert.Equal("toolu_9", result.GetProperty("tool_use_id").GetString());
        Assert.True(result.GetProperty("is_error").GetBoolean());
    }

    // ---- OpenAI-compatible ----

    [Fact]
    public async Task OpenAI_compatible_streams_text_and_usage()
    {
        const string sse = """
            data: {"choices":[{"index":0,"delta":{"role":"assistant","content":""}}]}

            data: {"choices":[{"index":0,"delta":{"content":"Hello"}}]}

            data: {"choices":[{"index":0,"delta":{"content":" world"},"finish_reason":"stop"}]}

            data: {"choices":[],"usage":{"prompt_tokens":9,"completion_tokens":2}}

            data: [DONE]


            """;
        var handler = new FakeHttpHandler(sse);
        var provider = new OpenAICompatibleProvider(new HttpClient(handler), new Uri("http://localhost:11434/v1/"), apiKey: "k");

        var events = await Collect(provider, Request());

        Assert.Equal("Hello world", string.Concat(events.OfType<TextDelta>().Select(d => d.Text)));
        var response = Assert.IsType<ModelResponse>(events[^1]);
        Assert.Equal("end_turn", response.StopReason);
        Assert.Equal(new Usage(9, 2), response.Usage);
        Assert.Equal("http://localhost:11434/v1/chat/completions", handler.Request!.RequestUri!.ToString());
        Assert.Equal("Bearer k", handler.Request.Headers.Authorization!.ToString());

        var body = JsonDocument.Parse(handler.RequestBody!).RootElement;
        Assert.Equal("system", body.GetProperty("messages")[0].GetProperty("role").GetString());
        Assert.Equal("Read", body.GetProperty("tools")[0].GetProperty("function").GetProperty("name").GetString());
    }

    [Fact]
    public async Task OpenAI_compatible_accumulates_tool_calls_and_flags_invalid_json()
    {
        const string sse = """
            data: {"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"call_a","type":"function","function":{"name":"Read","arguments":"{\"file_"}}]}}]}

            data: {"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"path\":\"x.cs\"}"}}]}}]}

            data: {"choices":[{"index":0,"delta":{"tool_calls":[{"index":1,"id":"call_b","type":"function","function":{"name":"Read","arguments":"{broken"}}]}}]}

            data: {"choices":[{"index":0,"delta":{},"finish_reason":"tool_calls"}]}

            data: [DONE]


            """;
        var provider = new OpenAICompatibleProvider(new HttpClient(new FakeHttpHandler(sse)), new Uri("http://x/v1/"), apiKey: null);

        var response = Assert.IsType<ModelResponse>((await Collect(provider, Request()))[^1]);

        Assert.Equal("tool_use", response.StopReason);
        var calls = response.Message.Parts.OfType<ToolCallPart>().ToList();
        Assert.Equal("x.cs", calls[0].Input.GetProperty("file_path").GetString());
        Assert.True(InvalidToolInput.TryUnwrap(calls[1].Input, out var raw));
        Assert.Equal("{broken", raw);
    }

    [Fact]
    public void OpenAI_compatible_maps_tool_results_to_tool_messages()
    {
        var input = JsonDocument.Parse("""{"file_path":"a"}""").RootElement;
        var request = Request(
            ChatMessage.UserText("go"),
            new ChatMessage(ChatRole.Assistant, [new ThinkingPart("x", "s"), new ToolCallPart("call_1", "Read", input)]),
            new ChatMessage(ChatRole.User, [new ToolResultPart("call_1", "nope", IsError: true)]));

        var messages = JsonDocument.Parse(OpenAICompatibleProvider.BuildBody(request)).RootElement.GetProperty("messages");

        var assistant = messages[2];
        Assert.Equal(JsonValueKind.Null, assistant.GetProperty("content").ValueKind);
        Assert.Equal("""{"file_path":"a"}""", assistant.GetProperty("tool_calls")[0].GetProperty("function").GetProperty("arguments").GetString());
        Assert.Equal("tool", messages[3].GetProperty("role").GetString());
        Assert.Equal("call_1", messages[3].GetProperty("tool_call_id").GetString());
        Assert.Equal("Error: nope", messages[3].GetProperty("content").GetString());
    }

    [Theory]
    [InlineData("stop", "end_turn")]
    [InlineData("length", "max_tokens")]
    [InlineData("tool_calls", "tool_use")]
    [InlineData("content_filter", "refusal")]
    public void OpenAI_finish_reasons_are_normalized(string finish, string expected) =>
        Assert.Equal(expected, OpenAICompatibleProvider.NormalizeStopReason(finish));

    [Fact]
    public async Task OpenAI_compatible_surfaces_http_errors()
    {
        var handler = new FakeHttpHandler("""{"error":"bad key"}""", System.Net.HttpStatusCode.Unauthorized);
        var provider = new OpenAICompatibleProvider(new HttpClient(handler), new Uri("http://localhost/v1/"), apiKey: null);

        var ex = await Assert.ThrowsAsync<ModelProviderException>(() => Collect(provider, Request()));
        Assert.Contains("401", ex.Message);
    }
}
