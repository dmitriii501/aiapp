using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Kairo.Core.Models;
using Kairo.Providers.Http;

namespace Kairo.Providers.OpenAICompatible;

/// <summary>
/// Any endpoint that speaks the Chat Completions streaming format: OpenRouter, DeepSeek, Groq,
/// vLLM, LM Studio, Ollama (<c>http://localhost:11434/v1</c>) and others.
/// </summary>
public sealed class OpenAICompatibleProvider(HttpClient http, Uri baseUrl, string? apiKey) : IModelProvider
{
    public string Id => "openai-compatible";

    public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
        ModelRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUrl, "chat/completions"))
        {
            Content = new ByteArrayContent(BuildBody(request)) { Headers = { ContentType = new MediaTypeHeaderValue("application/json") } },
        };
        if (!string.IsNullOrEmpty(apiKey))
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new ModelProviderException($"{(int)response.StatusCode} {response.ReasonPhrase}: {Truncate(body, 500)}", (int)response.StatusCode);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);

        var text = new StringBuilder();
        var calls = new SortedDictionary<int, (string Id, string Name, StringBuilder Args)>();
        string? finish = null;
        long? inputTokens = null, outputTokens = null;

        await foreach (var sse in SseReader.ReadAsync(stream, ct).ConfigureAwait(false))
        {
            if (sse.Data == "[DONE]")
                break;

            using var doc = JsonDocument.Parse(sse.Data);
            var root = doc.RootElement;

            if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
            {
                if (usage.TryGetProperty("prompt_tokens", out var pt)) inputTokens = pt.GetInt64();
                if (usage.TryGetProperty("completion_tokens", out var cpt)) outputTokens = cpt.GetInt64();
            }

            if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
                continue;

            var choice = choices[0];
            if (choice.TryGetProperty("delta", out var delta))
            {
                if (delta.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String
                    && content.GetString() is { Length: > 0 } chunk)
                {
                    text.Append(chunk);
                    yield return new TextDelta(chunk);
                }

                if (delta.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.ValueKind == JsonValueKind.Array)
                {
                    foreach (var tc in toolCalls.EnumerateArray())
                    {
                        var index = tc.TryGetProperty("index", out var i) ? i.GetInt32() : 0;
                        if (!calls.TryGetValue(index, out var acc))
                            acc = ("", "", new StringBuilder());
                        if (tc.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                            acc.Id = id.GetString()!;
                        if (tc.TryGetProperty("function", out var fn))
                        {
                            if (fn.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
                                acc.Name += name.GetString();
                            if (fn.TryGetProperty("arguments", out var args) && args.ValueKind == JsonValueKind.String)
                                acc.Args.Append(args.GetString());
                        }
                        calls[index] = acc;
                    }
                }
            }

            if (choice.TryGetProperty("finish_reason", out var fr) && fr.ValueKind == JsonValueKind.String)
                finish = fr.GetString();
        }

        var parts = new List<MessagePart>();
        if (text.Length > 0)
            parts.Add(new TextPart(text.ToString()));
        foreach (var (index, c) in calls)
            parts.Add(new ToolCallPart(c.Id is { Length: > 0 } ? c.Id : $"call_{index}", c.Name, InvalidToolInput.ParseOrWrap(c.Args.ToString())));

        yield return new ModelResponse(new ChatMessage(ChatRole.Assistant, parts), NormalizeStopReason(finish), new Usage(inputTokens, outputTokens));
    }

    /// <summary>Maps Chat Completions finish reasons onto the Anthropic-style names the agent uses.</summary>
    internal static string? NormalizeStopReason(string? finish) => finish switch
    {
        "stop" => "end_turn",
        "length" => "max_tokens",
        "tool_calls" or "function_call" => "tool_use",
        "content_filter" => "refusal",
        _ => finish,
    };

    internal static byte[] BuildBody(ModelRequest request)
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("model", request.Model);
            w.WriteNumber("max_tokens", request.MaxTokens);
            w.WriteBoolean("stream", true);
            w.WriteStartObject("stream_options");
            w.WriteBoolean("include_usage", true);
            w.WriteEndObject();
            if (request.Effort is { } effort)
                w.WriteString("reasoning_effort", effort);

            w.WriteStartArray("messages");
            if (request.System is { } system)
                WriteSimple(w, "system", system);
            foreach (var m in request.Messages)
                WriteMessage(w, m);
            w.WriteEndArray();

            if (request.Tools is { Count: > 0 } tools)
            {
                w.WriteStartArray("tools");
                foreach (var t in tools)
                {
                    w.WriteStartObject();
                    w.WriteString("type", "function");
                    w.WriteStartObject("function");
                    w.WriteString("name", t.Name);
                    w.WriteString("description", t.Description);
                    w.WritePropertyName("parameters");
                    t.InputSchema.WriteTo(w);
                    w.WriteEndObject();
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }

            w.WriteEndObject();
        }
        return buffer.ToArray();
    }

    private static void WriteMessage(Utf8JsonWriter w, ChatMessage m)
    {
        if (m.Role == ChatRole.User)
        {
            // Tool results become "tool" messages; any text goes in a user message after them.
            foreach (var r in m.Parts.OfType<ToolResultPart>())
            {
                w.WriteStartObject();
                w.WriteString("role", "tool");
                w.WriteString("tool_call_id", r.ToolCallId);
                w.WriteString("content", r.IsError ? "Error: " + r.Content : r.Content);
                w.WriteEndObject();
            }
            // A tool message cannot carry a picture in this API: the pictures follow in a user message that says where they came from.
            var pictures = m.Parts.OfType<ToolResultPart>().Where(r => r.Images is { Count: > 0 }).ToList();
            if (pictures.Count > 0)
            {
                w.WriteStartObject();
                w.WriteString("role", "user");
                w.WriteStartArray("content");
                foreach (var r in pictures)
                {
                    w.WriteStartObject();
                    w.WriteString("type", "text");
                    w.WriteString("text", $"The picture(s) returned by tool call {r.ToolCallId}:");
                    w.WriteEndObject();
                    foreach (var image in r.Images!)
                    {
                        w.WriteStartObject();
                        w.WriteString("type", "image_url");
                        w.WriteStartObject("image_url");
                        w.WriteString("url", $"data:{image.MediaType};base64,{image.Base64}");
                        w.WriteEndObject();
                        w.WriteEndObject();
                    }
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }
            if (m.Text is { Length: > 0 } userText)
                WriteSimple(w, "user", userText);
            return;
        }

        // Thinking parts are Anthropic-specific and are dropped here.
        var calls = m.Parts.OfType<ToolCallPart>().ToList();
        w.WriteStartObject();
        w.WriteString("role", "assistant");
        if (m.Text is { Length: > 0 } text)
            w.WriteString("content", text);
        else
            w.WriteNull("content");
        if (calls.Count > 0)
        {
            w.WriteStartArray("tool_calls");
            foreach (var c in calls)
            {
                w.WriteStartObject();
                w.WriteString("id", c.Id);
                w.WriteString("type", "function");
                w.WriteStartObject("function");
                w.WriteString("name", c.Name);
                w.WriteString("arguments", InvalidToolInput.TryUnwrap(c.Input, out var raw) ? raw : c.Input.GetRawText());
                w.WriteEndObject();
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }
        w.WriteEndObject();
    }

    private static void WriteSimple(Utf8JsonWriter w, string role, string content)
    {
        w.WriteStartObject();
        w.WriteString("role", role);
        w.WriteString("content", content);
        w.WriteEndObject();
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
