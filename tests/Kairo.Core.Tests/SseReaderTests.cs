using System.Text;
using Kairo.Providers.Http;

namespace Kairo.Core.Tests;

public class SseReaderTests
{
    private static async Task<List<SseEvent>> Read(string body)
    {
        var events = new List<SseEvent>();
        await foreach (var e in SseReader.ReadAsync(new MemoryStream(Encoding.UTF8.GetBytes(body))))
            events.Add(e);
        return events;
    }

    [Fact]
    public async Task Parses_event_names_and_data()
    {
        var events = await Read("event: ping\ndata: {\"a\":1}\n\nevent: done\ndata: x\n\n");

        Assert.Equal([new SseEvent("ping", "{\"a\":1}"), new SseEvent("done", "x")], events);
    }

    [Fact]
    public async Task Joins_multiline_data_and_skips_comments()
    {
        var events = await Read(": keep-alive\ndata: line1\ndata: line2\n\n");

        Assert.Equal([new SseEvent(null, "line1\nline2")], events);
    }

    [Fact]
    public async Task Emits_last_event_without_trailing_blank_line()
    {
        var events = await Read("data: tail");

        Assert.Equal([new SseEvent(null, "tail")], events);
    }

    [Fact]
    public async Task Handles_crlf_line_endings()
    {
        var events = await Read("data: a\r\n\r\ndata: b\r\n\r\n");

        Assert.Equal(["a", "b"], events.Select(e => e.Data));
    }
}
