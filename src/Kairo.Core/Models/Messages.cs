using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kairo.Core.Models;

/// <summary>
/// Kairo's provider-neutral conversation format. Adapters translate it to each vendor's API;
/// sessions persist it as JSON. History is append-only — Claude rejects edited thinking history.
/// </summary>
public enum ChatRole { User, Assistant }

public sealed record ChatMessage(ChatRole Role, IReadOnlyList<MessagePart> Parts)
{
    public static ChatMessage UserText(string text) => new(ChatRole.User, [new TextPart(text)]);

    [JsonIgnore]
    public string Text => string.Concat(Parts.OfType<TextPart>().Select(p => p.Text));
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(TextPart), "text")]
[JsonDerivedType(typeof(ThinkingPart), "thinking")]
[JsonDerivedType(typeof(RedactedThinkingPart), "redacted_thinking")]
[JsonDerivedType(typeof(ToolCallPart), "tool_call")]
[JsonDerivedType(typeof(ToolResultPart), "tool_result")]
public abstract record MessagePart;

public sealed record TextPart(string Text) : MessagePart;

/// <summary>Model reasoning. Opaque to Kairo: must be sent back unchanged, signature included.</summary>
public sealed record ThinkingPart(string Thinking, string Signature) : MessagePart;

public sealed record RedactedThinkingPart(string Data) : MessagePart;

/// <summary>A tool call requested by the model. <see cref="Input"/> is the parsed JSON object.</summary>
public sealed record ToolCallPart(string Id, string Name, JsonElement Input) : MessagePart;

public sealed record ToolResultPart(string ToolCallId, string Content, bool IsError = false) : MessagePart;

/// <summary>A tool the model may call. <see cref="InputSchema"/> is a JSON Schema object.</summary>
public sealed record ToolSpec(string Name, string Description, JsonElement InputSchema);

public sealed record ModelRequest(
    string Model,
    IReadOnlyList<ChatMessage> Messages,
    string? System = null,
    IReadOnlyList<ToolSpec>? Tools = null,
    int MaxTokens = 64000,
    string? Effort = null);

public abstract record ModelStreamEvent;

public sealed record TextDelta(string Text) : ModelStreamEvent;

/// <summary>The finished assistant turn, emitted once at the end of the stream.</summary>
public sealed record ModelResponse(ChatMessage Message, string? StopReason, Usage Usage) : ModelStreamEvent;

public sealed record Usage(long? InputTokens, long? OutputTokens, long? CacheReadTokens = null);

/// <summary>
/// A tool call whose arguments were not valid JSON (possible with eager input streaming).
/// Kairo answers it with an error result instead of running the tool.
/// </summary>
public static class InvalidToolInput
{
    public const string Marker = "__kairo_invalid_json";

    public static JsonElement Wrap(string raw)
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString(Marker, raw);
            w.WriteEndObject();
        }
        return JsonDocument.Parse(buffer.ToArray()).RootElement.Clone();
    }

    public static bool TryUnwrap(JsonElement input, out string raw)
    {
        raw = "";
        if (input.ValueKind == JsonValueKind.Object && input.TryGetProperty(Marker, out var r) && r.ValueKind == JsonValueKind.String)
        {
            raw = r.GetString()!;
            return true;
        }
        return false;
    }

    /// <summary>Parses accumulated tool-call arguments strictly; returns a wrapped marker on failure.</summary>
    public static JsonElement ParseOrWrap(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return JsonDocument.Parse("{}").RootElement.Clone();
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object ? doc.RootElement.Clone() : Wrap(json);
        }
        catch (JsonException)
        {
            return Wrap(json);
        }
    }
}

public interface IModelProvider
{
    string Id { get; }

    IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken ct = default);
}

public sealed class ModelProviderException(string message, int? statusCode = null, Exception? inner = null) : Exception(message, inner)
{
    /// <summary>HTTP status of the failed request, when there was one.</summary>
    public int? StatusCode { get; } = statusCode;
}
