using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Kairo.Core.Tests.Fakes;
using Kairo.Core.Tools;
using Kairo.Tools.Web;

namespace Kairo.Core.Tests;

public sealed class DownloadTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    /// <summary>A stream that does not know its length, like a chunked response.</summary>
    private sealed class NoLength(Stream inner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);

    private ToolContext Ctx => new(_dir.Path);

    private static HttpResponseMessage Bytes(byte[] data, string mediaType, HttpStatusCode status = HttpStatusCode.OK)
    {
        var content = new ByteArrayContent(data);
        content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        return new HttpResponseMessage(status) { Content = content };
    }

    [Fact]
    public async Task A_picture_is_saved_into_the_downloads_folder_of_the_working_directory()
    {
        var tool = new DownloadTool(new FuncHandler(_ => Bytes([1, 2, 3, 4], "image/jpeg")));

        var result = await tool.InvokeAsync(Json(new { url = "https://upload.example.org/a/Photo%20One.jpg" }), Ctx, default);

        Assert.False(result.IsError, result.Content);
        var saved = Path.Combine(_dir.Path, "kairo-downloads", "Photo One.jpg");
        Assert.Equal([1, 2, 3, 4], File.ReadAllBytes(saved));
        Assert.Contains(saved, result.Content);
        Assert.Contains("untrusted", result.Content);
    }

    [Fact]
    public async Task A_second_download_with_the_same_name_does_not_overwrite_the_first()
    {
        var tool = new DownloadTool(new FuncHandler(_ => Bytes([9], "image/png")));
        await tool.InvokeAsync(Json(new { url = "https://example.org/x.png" }), Ctx, default);

        var again = await tool.InvokeAsync(Json(new { url = "https://example.org/x.png" }), Ctx, default);

        Assert.False(again.IsError, again.Content);
        Assert.True(File.Exists(Path.Combine(_dir.Path, "kairo-downloads", "x.png")));
        Assert.True(File.Exists(Path.Combine(_dir.Path, "kairo-downloads", "x (1).png")));
    }

    [Theory]
    [InlineData("https://example.org/setup.exe", "application/x-msdownload")]
    [InlineData("https://example.org/run.ps1", "text/x-powershell")]
    [InlineData("https://example.org/photo.jpg", "application/x-msdownload")] // the server says it is a program
    [InlineData("https://example.org/app.msi", "application/octet-stream")]
    public async Task Programs_and_scripts_are_never_saved(string url, string mediaType)
    {
        var tool = new DownloadTool(new FuncHandler(_ => Bytes([77, 90], mediaType)));

        var result = await tool.InvokeAsync(Json(new { url }), Ctx, default);

        Assert.True(result.IsError);
        Assert.Contains("Programs and scripts never are", result.Content);
        Assert.False(Directory.Exists(Path.Combine(_dir.Path, "kairo-downloads")) && Directory.GetFiles(Path.Combine(_dir.Path, "kairo-downloads")).Length > 0);
    }

    [Fact]
    public async Task The_name_cannot_leave_the_downloads_folder()
    {
        var tool = new DownloadTool(new FuncHandler(_ => Bytes([1], "image/png")));

        var result = await tool.InvokeAsync(Json(new { url = "https://example.org/a.png", filename = "..\\..\\evil.png" }), Ctx, default);

        Assert.False(result.IsError, result.Content);
        Assert.Empty(Directory.GetFiles(_dir.Path)); // nothing next to the working directory's own files
        Assert.Single(Directory.GetFiles(Path.Combine(_dir.Path, "kairo-downloads")));
    }

    [Fact]
    public async Task A_file_over_the_limit_is_cut_off_and_removed()
    {
        var big = new byte[(int)(DownloadTool.MaxBytes + 1)];
        var tool = new DownloadTool(new FuncHandler(_ => Bytes(big, "application/pdf")));

        var result = await tool.InvokeAsync(Json(new { url = "https://example.org/big.pdf" }), Ctx, default);

        Assert.True(result.IsError);
        var folder = Path.Combine(_dir.Path, "kairo-downloads");
        Assert.True(!Directory.Exists(folder) || Directory.GetFiles(folder).Length == 0);
    }

    [Fact]
    public async Task A_file_whose_size_is_not_announced_is_cut_off_while_it_arrives_and_removed()
    {
        var big = new byte[(int)(DownloadTool.MaxBytes + 1)];
        var tool = new DownloadTool(new FuncHandler(_ =>
        {
            var content = new StreamContent(new NoLength(new MemoryStream(big)));
            content.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }));

        var result = await tool.InvokeAsync(Json(new { url = "https://example.org/big.pdf" }), Ctx, default);

        Assert.True(result.IsError);
        Assert.Empty(Directory.GetFiles(Path.Combine(_dir.Path, "kairo-downloads")));
    }

    [Fact]
    public async Task A_private_address_is_refused_before_anything_is_sent()
    {
        var handler = new FuncHandler(_ => Bytes([1], "image/png"));
        var tool = new DownloadTool(handler);

        Assert.Throws<ToolInputException>(() => tool.Assess(Json(new { url = "http://192.168.1.1/a.png" }), Ctx));
        await Task.CompletedTask;
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public void It_asks_like_other_network_tools_and_names_the_host()
    {
        var assessment = new DownloadTool().Assess(Json(new { url = "https://Upload.Example.org/a.png" }), Ctx);

        Assert.Equal(RiskLevel.Network, assessment.Risk);
        Assert.Equal("upload.example.org", assessment.Subject);
    }
}
