using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Kairo.Core.Permissions;
using Kairo.Core.Tests.Fakes;
using Kairo.Core.Tools;
using Kairo.Tools;
using Kairo.Tools.Web;

namespace Kairo.Core.Tests;

internal sealed class FuncHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests.Add(request);
        return Task.FromResult(respond(request));
    }

    public static HttpResponseMessage Text(string body, string mediaType = "text/html", HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, mediaType) };

    public static HttpResponseMessage Redirect(string to) =>
        new(HttpStatusCode.Found) { Headers = { Location = new Uri(to, UriKind.RelativeOrAbsolute) } };
}

public class WebGuardTests
{
    [Theory]
    [InlineData("8.8.8.8", true)]
    [InlineData("93.184.216.34", true)]
    [InlineData("172.32.0.1", true)]
    [InlineData("2606:4700::1111", true)]
    [InlineData("127.0.0.1", false)]
    [InlineData("10.1.2.3", false)]
    [InlineData("172.16.0.1", false)]
    [InlineData("172.31.255.255", false)]
    [InlineData("192.168.1.10", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("224.0.0.1", false)]
    [InlineData("255.255.255.255", false)]
    [InlineData("::1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("fc00::1", false)]
    [InlineData("fd12:3456::1", false)]
    [InlineData("::ffff:127.0.0.1", false)]
    [InlineData("::ffff:10.0.0.1", false)]
    public void Only_public_addresses_are_allowed(string address, bool expected)
    {
        Assert.Equal(expected, WebGuard.IsPublic(IPAddress.Parse(address)));
    }

    [Theory]
    [InlineData("https://example.com/a", false)]
    [InlineData("http://docs.example.org:8080/x?y=1", false)]
    [InlineData("file:///C:/Windows/win.ini", true)]
    [InlineData("ftp://example.com/a", true)]
    [InlineData("http://localhost:5000/", true)]
    [InlineData("http://app.localhost/", true)]
    [InlineData("http://127.0.0.1/", true)]
    [InlineData("http://[::1]/", true)]
    [InlineData("http://169.254.169.254/latest/meta-data/", true)]
    [InlineData("http://192.168.0.1/", true)]
    [InlineData("http://intranet/", true)]
    [InlineData("http://printer.local/", true)]
    [InlineData("http://user:pass@example.com/", true)]
    public void Addresses_are_checked_before_any_request(string url, bool rejected)
    {
        Assert.Equal(rejected, WebGuard.Reject(new Uri(url)) is not null);
    }

    [Fact]
    public async Task The_real_client_refuses_to_connect_to_a_local_address()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var client = new HttpClient(WebGuard.CreateHandler());

            var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync($"http://127.0.0.1:{port}/"));

            Assert.Contains("Blocked", ex.InnerException?.Message ?? ex.Message);
            Assert.False(listener.Pending()); // nothing connected
        }
        finally
        {
            listener.Stop();
        }
    }
}

public class HtmlTextTests
{
    [Fact]
    public void A_page_becomes_readable_text_without_scripts_or_markup()
    {
        const string html = """
            <html><head><title>Docs &amp; Guides</title><style>body{color:red}</style><script>var x = "<p>no</p>";</script></head>
            <body>
              <h1>Title</h1>
              <p>First   paragraph with <b>bold</b> and a <a href="/api/json">link</a>.</p>
              <ul><li>one</li><li>two &lt;3</li></ul>
              <!-- hidden comment -->
              <pre>line 1
              indented</pre>
              <table><tr><td>a</td><td>b</td></tr></table>
              <a href="#top">skip</a><a href="javascript:void(0)">skip too</a>
              <img src="x.png" alt="a diagram">
            </body></html>
            """;

        var (title, text) = HtmlText.Convert(html, new Uri("https://example.com/docs/index.html"));

        Assert.Equal("Docs & Guides", title);
        Assert.Contains("# Title", text);
        Assert.Contains("First paragraph with bold and a link (https://example.com/api/json).", text);
        Assert.Contains("- one\n- two <3", text);
        Assert.Contains("```\nline 1\n  indented\n```", text); // the raw string literal strips 12 of the 14 spaces
        Assert.Contains("a | b", text);
        Assert.Contains("[image: a diagram]", text);
        Assert.DoesNotContain("color:red", text);
        Assert.DoesNotContain("var x", text);
        Assert.DoesNotContain("hidden comment", text);
        Assert.DoesNotContain("#top", text); // links without a usable address keep their text only
        Assert.DoesNotContain("javascript:", text);
    }

    [Fact]
    public void The_main_part_of_a_page_is_preferred_over_navigation_and_footers()
    {
        var article = string.Concat(Enumerable.Repeat("This sentence is part of the article body. ", 12));
        var html = $"<body><nav>Home Products Pricing Login</nav><main><h1>Guide</h1><p>{article}</p></main><footer>Cookie settings</footer></body>";

        var (_, text) = HtmlText.Convert(html);

        Assert.Contains("# Guide", text);
        Assert.DoesNotContain("Pricing", text);
        Assert.DoesNotContain("Cookie", text);

        // A page whose main element is nearly empty is read whole.
        var (_, whole) = HtmlText.Convert("<nav>Menu items</nav><main>x</main><p>The real content is outside.</p>");
        Assert.Contains("The real content is outside.", whole);
    }

    [Fact]
    public void Broken_markup_does_not_throw()
    {
        var (_, text) = HtmlText.Convert("<div><p>open <b>tags <a href=\"x\">and an unterminated <i");

        Assert.Contains("open tags", text);
    }
}

public class WebFetchTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private ToolContext Ctx => new(_dir.Path);

    private static JsonElement Input(string url, int? offset = null) =>
        Json.Of(offset is null ? $$"""{"url":"{{url}}"}""" : $$"""{"url":"{{url}}","offset":{{offset}}}""");

    private static async Task<ToolResult> Fetch(FuncHandler handler, string url, int? offset = null, ToolContext? ctx = null)
    {
        var tool = new WebFetchTool(handler);
        return await tool.InvokeAsync(Input(url, offset), ctx ?? new ToolContext(Path.GetTempPath()), default);
    }

    [Fact]
    public async Task A_page_is_returned_as_marked_untrusted_text()
    {
        var handler = new FuncHandler(_ => FuncHandler.Text("<html><head><title>Hi</title></head><body><h1>Hello</h1><p>World</p></body></html>"));

        var result = await Fetch(handler, "https://example.com/page");

        Assert.False(result.IsError);
        Assert.Contains("untrusted", result.Content);
        Assert.Contains("(Hi)", result.Content);
        Assert.Contains("# Hello", result.Content);
        Assert.Contains("User-Agent", handler.Requests[0].Headers.ToString());
    }

    [Fact]
    public async Task A_long_page_comes_in_parts()
    {
        var body = string.Join("\n", Enumerable.Range(0, 5000).Select(i => $"line {i:D5} " + new string('x', 20)));
        var handler = new FuncHandler(_ => FuncHandler.Text(body, "text/plain"));

        var first = await Fetch(handler, "https://example.com/big.txt");
        var marker = "offset=";
        var next = int.Parse(first.Content[(first.Content.IndexOf(marker, StringComparison.Ordinal) + marker.Length)..].Split(' ')[0]);
        var second = await Fetch(handler, "https://example.com/big.txt", next);

        Assert.Contains("line 00000", first.Content);
        Assert.DoesNotContain("line 04999", first.Content);
        Assert.Contains($"Characters {next}-", second.Content);
        Assert.Equal(0, next % 32); // each line is 32 characters: the cut is at a line break
        Assert.Contains($"line {next / 32:D5} ", second.Content);
        Assert.True(next > 20_000 && next <= WebFetchTool.PageChars);
    }

    [Fact]
    public async Task A_redirect_inside_the_same_site_is_followed()
    {
        var handler = new FuncHandler(r => r.RequestUri!.AbsolutePath == "/old" ? FuncHandler.Redirect("https://www.example.com/new") : FuncHandler.Text("moved here", "text/plain"));

        var result = await Fetch(handler, "https://example.com/old");

        Assert.Contains("moved here", result.Content);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task A_redirect_to_another_host_is_reported_not_followed()
    {
        var handler = new FuncHandler(_ => FuncHandler.Redirect("https://other.example.net/landing"));

        var result = await Fetch(handler, "https://example.com/go");

        Assert.False(result.IsError);
        Assert.Contains("https://other.example.net/landing", result.Content);
        Assert.Contains("different host", result.Content);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("http://127.0.0.1:8080/admin")]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://localhost/")]
    public async Task A_redirect_into_the_local_network_is_refused(string target)
    {
        var handler = new FuncHandler(_ => FuncHandler.Redirect(target));

        var result = await Fetch(handler, "https://example.com/go");

        Assert.True(result.IsError);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task An_endless_redirect_chain_stops()
    {
        var handler = new FuncHandler(r => FuncHandler.Redirect(r.RequestUri!.AbsolutePath + "x"));

        var result = await Fetch(handler, "https://example.com/a");

        Assert.True(result.IsError);
        Assert.Contains("Too many redirects", result.Content);
        Assert.Equal(WebFetchTool.MaxRedirects + 1, handler.Requests.Count);
    }

    [Fact]
    public async Task Binary_content_and_http_errors_are_reported_to_the_model()
    {
        var binary = await Fetch(new FuncHandler(_ => FuncHandler.Text("MZ", "application/octet-stream")), "https://example.com/a.exe");
        var missing = await Fetch(new FuncHandler(_ => FuncHandler.Text("nope", "text/html", HttpStatusCode.NotFound)), "https://example.com/none");

        Assert.True(binary.IsError);
        Assert.Contains("not text", binary.Content);
        Assert.True(missing.IsError);
        Assert.Contains("404", missing.Content);
    }

    [Fact]
    public async Task A_huge_download_is_cut()
    {
        var handler = new FuncHandler(_ => FuncHandler.Text(new string('a', WebFetchTool.MaxBytes + 5000), "text/plain"));

        var result = await Fetch(handler, "https://example.com/huge");

        Assert.Contains("Characters 0-", result.Content);
        Assert.True(result.Content.Length < WebFetchTool.PageChars + 1000);
    }

    [Fact]
    public void A_fetch_is_a_network_call_on_the_host_and_never_reaches_local_addresses()
    {
        var tool = new WebFetchTool(new FuncHandler(_ => FuncHandler.Text("")));

        var assessment = tool.Assess(Input("https://Docs.Example.com/a/b?c=1"), Ctx);
        Assert.Equal(RiskLevel.Network, assessment.Risk);
        Assert.Equal("docs.example.com", assessment.Subject);
        Assert.Throws<ToolInputException>(() => tool.Assess(Input("http://localhost:3000"), Ctx));
        Assert.Throws<ToolInputException>(() => tool.Assess(Input("file:///C:/secrets.txt"), Ctx));
        Assert.Equal("example.com", tool.Assess(Input("example.com/path"), Ctx).Subject); // scheme is optional
    }

    [Fact]
    public void Fetching_asks_by_default_and_rules_can_allow_a_site()
    {
        var tool = new WebFetchTool(new FuncHandler(_ => FuncHandler.Text("")));
        var rules = PermissionRules.WithDefaults();
        rules.Allow.Add(PermissionRule.Parse("WebFetch(*.github.com)"));
        var engine = new PermissionEngine(rules, PermissionMode.Default);

        Assert.Equal(PermissionDecision.Allow, engine.Evaluate("WebFetch", tool.Assess(Input("https://docs.github.com/x"), Ctx), Ctx).Decision);
        Assert.Equal(PermissionDecision.Ask, engine.Evaluate("WebFetch", tool.Assess(Input("https://example.com/x"), Ctx), Ctx).Decision);
        Assert.Equal(PermissionDecision.Deny, new PermissionEngine(PermissionRules.WithDefaults(), PermissionMode.Plan)
            .Evaluate("WebFetch", tool.Assess(Input("https://example.com/x"), Ctx), Ctx).Decision);

        var always = new PermissionEngine(PermissionRules.WithDefaults(), PermissionMode.Default);
        var assessment = tool.Assess(Input("https://example.com/x"), Ctx);
        always.AllowForSession("WebFetch", assessment);
        Assert.Equal(PermissionDecision.Allow, always.Evaluate("WebFetch", tool.Assess(Input("https://example.com/y"), Ctx), Ctx).Decision);
        Assert.Equal(PermissionDecision.Ask, always.Evaluate("WebFetch", tool.Assess(Input("https://evil.example.net/y"), Ctx), Ctx).Decision);
    }
}

public class WebSearchTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private ToolContext Ctx => new(_dir.Path);

    private static JsonElement Input(string query, int? count = null) =>
        Json.Of(count is null ? $$"""{"query":"{{query}}"}""" : $$"""{"query":"{{query}}","count":{{count}}}""");

    private const string BraveReply = """
        {"web":{"results":[
          {"title":"<strong>.NET</strong> 10 released","url":"https://devblogs.microsoft.com/dotnet/net-10","description":"The <strong>new</strong> release &amp; its features"},
          {"title":"Download","url":"https://dotnet.microsoft.com/download","description":""},
          {"title":"no url","description":"skipped"}
        ]}}
        """;

    [Fact]
    public async Task Brave_results_are_listed_as_untrusted_data()
    {
        var handler = new FuncHandler(_ => FuncHandler.Text(BraveReply, "application/json"));
        var tool = new WebSearchTool(() => new WebSearchSettings(BraveKey: "key-123"), handler);

        var result = await tool.InvokeAsync(Input("dotnet 10 release"), Ctx, default);

        Assert.False(result.IsError);
        Assert.Contains("untrusted", result.Content);
        Assert.Contains("1. .NET 10 released", result.Content);
        Assert.Contains("https://devblogs.microsoft.com/dotnet/net-10", result.Content);
        Assert.Contains("The new release & its features", result.Content);
        Assert.Contains("2. Download", result.Content);
        Assert.DoesNotContain("no url", result.Content);
        var request = handler.Requests.Single();
        Assert.Equal("api.search.brave.com", request.RequestUri!.Host);
        Assert.Contains("q=dotnet%2010%20release", request.RequestUri.Query);
        Assert.Equal("key-123", request.Headers.GetValues("X-Subscription-Token").Single());
    }

    [Fact]
    public async Task A_searxng_instance_is_used_when_configured()
    {
        var handler = new FuncHandler(_ => FuncHandler.Text("""{"results":[{"title":"Hit","url":"https://example.com/hit","content":"about it"}]}""", "application/json"));
        var tool = new WebSearchTool(() => new WebSearchSettings(SearxngUrl: "http://localhost:8080/"), handler);

        var assessment = tool.Assess(Input("kairo"), Ctx);
        var result = await tool.InvokeAsync(Input("kairo"), Ctx, default);

        Assert.Equal("localhost:8080", assessment.Subject);
        Assert.Contains("1. Hit", result.Content);
        Assert.Equal("http://localhost:8080/search?q=kairo&format=json", handler.Requests.Single().RequestUri!.ToString());
    }

    [Fact]
    public void The_backend_can_be_chosen_when_both_are_set_up()
    {
        var handler = new FuncHandler(_ => FuncHandler.Text("{}"));
        var both = new WebSearchSettings("k", "http://localhost:8080", Backend: "brave");

        Assert.Equal("api.search.brave.com", new WebSearchTool(() => both, handler).Assess(Input("x"), Ctx).Subject);
        Assert.Equal("localhost:8080", new WebSearchTool(() => both with { Backend = "searxng" }, handler).Assess(Input("x"), Ctx).Subject);
        Assert.Equal("localhost:8080", new WebSearchTool(() => both with { Backend = null, BraveKey = null }, handler).Assess(Input("x"), Ctx).Subject);
    }

    [Fact]
    public void Without_a_backend_the_model_is_told_to_ask_the_user_and_nobody_is_prompted()
    {
        var tool = new WebSearchTool(() => new WebSearchSettings(), new FuncHandler(_ => FuncHandler.Text("{}")));

        var ex = Assert.Throws<ToolInputException>(() => tool.Assess(Input("x"), Ctx));

        Assert.Contains("not set up", ex.Message);
    }

    [Fact]
    public async Task Search_errors_are_reported_without_echoing_the_key()
    {
        var tool = new WebSearchTool(() => new WebSearchSettings(BraveKey: "secret-key"), new FuncHandler(_ => FuncHandler.Text("{\"error\":\"bad token\"}", "application/json", HttpStatusCode.Unauthorized)));

        var result = await tool.InvokeAsync(Input("x"), Ctx, default);

        Assert.True(result.IsError);
        Assert.Contains("401", result.Content);
        Assert.DoesNotContain("secret-key", result.Content);
    }

    [Fact]
    public async Task An_unexpected_reply_is_reported_as_such()
    {
        var tool = new WebSearchTool(() => new WebSearchSettings(SearxngUrl: "http://localhost:8080"), new FuncHandler(_ => FuncHandler.Text("<html>forbidden</html>")));

        var result = await tool.InvokeAsync(Input("x"), Ctx, default);

        Assert.True(result.IsError);
        Assert.Contains("json format", result.Content);
    }

    [Fact]
    public void Queries_are_validated_and_the_count_is_limited()
    {
        var tool = new WebSearchTool(() => new WebSearchSettings(BraveKey: "k"), new FuncHandler(_ => FuncHandler.Text(BraveReply)));

        Assert.Throws<ToolInputException>(() => tool.Assess(Input("   "), Ctx));
        Assert.Throws<ToolInputException>(() => tool.Assess(Input(new string('q', WebSearchTool.MaxQueryChars + 1)), Ctx));
        Assert.Equal(RiskLevel.Network, tool.Assess(Input("ok"), Ctx).Risk);
    }

    [Fact]
    public async Task The_result_count_follows_the_request()
    {
        var tool = new WebSearchTool(() => new WebSearchSettings(BraveKey: "k"), new FuncHandler(_ => FuncHandler.Text(BraveReply, "application/json")));

        var result = await tool.InvokeAsync(Input("x", count: 1), Ctx, default);

        Assert.Contains("1. .NET 10 released", result.Content);
        Assert.DoesNotContain("2. Download", result.Content);
    }
}

public class WebConfigTests
{
    [Fact]
    public void The_search_backend_comes_from_the_users_file_and_never_from_a_project()
    {
        using var dir = new TempDir();
        var user = Path.Combine(dir.Path, "user");
        var project = Path.Combine(dir.Path, "project");
        Directory.CreateDirectory(user);
        Directory.CreateDirectory(Path.Combine(project, ".kairo"));
        File.WriteAllText(Path.Combine(user, "config.toml"), "[web]\nsearch = \"searxng\"\nsearxng_url = \"http://localhost:8080\"\n");
        File.WriteAllText(Path.Combine(project, ".kairo", "config.toml"), "[web]\nsearch = \"brave\"\nsearxng_url = \"https://evil.example.net\"\n");

        var config = Kairo.Core.Config.KairoConfig.Load(project, user);

        Assert.Equal("searxng", config.WebSearch);
        Assert.Equal("http://localhost:8080", config.SearxngUrl);
    }
}
