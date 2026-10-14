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

    public const string NoteOpen = "<kairo-note>";
    public const string NoteClose = "</kairo-note>";

    /// <summary>Marks the message that replaces the earlier conversation after compaction (see <c>Compaction</c>).</summary>
    public const string SummaryOpen = "<kairo-summary>";
    public const string SummaryClose = "</kairo-summary>";

    [JsonIgnore]
    public bool IsSummary => Role == ChatRole.User && Parts.Count == 1 && Parts[0] is TextPart t && t.Text.StartsWith(SummaryOpen, StringComparison.Ordinal);

    [JsonIgnore]
    public string Text => string.Concat(Parts.OfType<TextPart>().Select(p => p.Text));

    /// <summary>What the user typed: the text without notes that Kairo added for the model.</summary>
    [JsonIgnore]
    public string UserTypedText => string.Concat(Parts.OfType<TextPart>()
        .Where(p => !p.Text.StartsWith(NoteOpen, StringComparison.Ordinal) && !p.Text.StartsWith(SummaryOpen, StringComparison.Ordinal))
        .Select(p => p.Text));
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

/// <summary>A picture a tool returns for a model that can look at images (a screenshot). Base64 so it survives any JSON hop.</summary>
public sealed record ToolImage(string MediaType, string Base64);

/// <summary>
/// Diff is for the UI transcript only; providers ignore it. <see cref="Images"/> are not saved with the session (they would swell it),
/// and a request carries only the latest few (see <see cref="ModelImages"/>); the text says what the picture showed.
/// </summary>
public sealed record ToolResultPart(
    string ToolCallId,
    string Content,
    bool IsError = false,
    string? Diff = null,
    [property: JsonIgnore] IReadOnlyList<ToolImage>? Images = null) : MessagePart;

/// <summary>Keeps pictures in a request only where they still matter: a screenshot is stale after the next one.</summary>
public static class ModelImages
{
    public const int Keep = 2;

    /// <summary>The messages with all but the newest <paramref name="keep"/> pictures replaced by a note. The stored history is not touched.</summary>
    public static IReadOnlyList<ChatMessage> ForRequest(IReadOnlyList<ChatMessage> history, int keep = Keep)
    {
        var total = history.Sum(m => m.Parts.OfType<ToolResultPart>().Count(p => p.Images is { Count: > 0 }));
        if (total <= keep)
            return history;

        var drop = total - keep;
        var result = new List<ChatMessage>(history.Count);
        foreach (var message in history)
        {
            if (drop == 0 || !message.Parts.Any(p => p is ToolResultPart { Images.Count: > 0 }))
            {
                result.Add(message);
                continue;
            }
            var parts = message.Parts.Select(p =>
            {
                if (drop > 0 && p is ToolResultPart { Images.Count: > 0 } r)
                {
                    drop--;
                    return r with { Images = null, Content = r.Content + "\n(An earlier screenshot is no longer attached; take a new one if you need to look.)" };
                }
                return p;
            }).ToList();
            result.Add(message with { Parts = parts });
        }
        return result;
    }
}

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
