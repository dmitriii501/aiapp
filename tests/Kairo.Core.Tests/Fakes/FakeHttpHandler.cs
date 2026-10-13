using System.Net;
using System.Text;

namespace Kairo.Core.Tests.Fakes;

/// <summary>Returns a canned SSE body and records the request, so provider adapters can be tested offline.</summary>
internal sealed class FakeHttpHandler(string sseBody, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
{
    public HttpRequestMessage? Request { get; private set; }
    public string? RequestBody { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Request = request;
        RequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
        return new HttpResponseMessage(status)
        {
            Content = new StringContent(sseBody, Encoding.UTF8, "text/event-stream"),
        };
    }
}
