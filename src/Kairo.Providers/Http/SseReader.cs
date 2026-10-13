using System.Runtime.CompilerServices;
using System.Text;

namespace Kairo.Providers.Http;

public sealed record SseEvent(string? Event, string Data);

/// <summary>Minimal Server-Sent Events parser (WHATWG event-stream format).</summary>
public static class SseReader
{
    public static async IAsyncEnumerable<SseEvent> ReadAsync(
        Stream stream,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8);
        string? eventName = null;
        var data = new StringBuilder();

        while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
        {
            if (line.Length == 0)
            {
                if (data.Length > 0)
                {
                    yield return new SseEvent(eventName, data.ToString());
                    data.Clear();
                }
                eventName = null;
                continue;
            }

            if (line[0] == ':')
                continue; // comment / keep-alive

            var colon = line.IndexOf(':');
            var field = colon < 0 ? line : line[..colon];
            var value = colon < 0 ? "" : line[(colon + 1)..];
            if (value.StartsWith(' '))
                value = value[1..];

            switch (field)
            {
                case "event":
                    eventName = value;
                    break;
                case "data":
                    if (data.Length > 0)
                        data.Append('\n');
                    data.Append(value);
                    break;
            }
        }

        if (data.Length > 0)
            yield return new SseEvent(eventName, data.ToString());
    }
}
