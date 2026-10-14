using System.Reflection;
using Kairo.Host;
using Velopack;
using Velopack.Sources;

namespace Kairo.Desktop;

/// <summary>
/// Updates through Velopack. Works only in a build that Velopack installed and that has a feed: a GitHub repository,
/// a web address or a folder. The feed comes from KAIRO_UPDATE_URL, the user's config.toml, or the address baked in at
/// release time. A project's own config can never change it: that would let a repository replace the app.
/// </summary>
internal sealed class VelopackUpdater : IAppUpdater
{
    private readonly UpdateManager? _manager;
    private UpdateInfo? _pending;

    public VelopackUpdater(string? feed)
    {
        if (!string.IsNullOrWhiteSpace(feed))
            _manager = new UpdateManager(CreateSource(feed.Trim()));
    }

    /// <summary>The address compiled in by the release build (<c>-p:KairoUpdateUrl=…</c>); empty in local builds.</summary>
    public static string? BakedInFeed =>
        typeof(VelopackUpdater).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "KairoUpdateUrl")?.Value is { Length: > 0 } url ? url : null;

    public bool CanUpdate => _manager is { IsInstalled: true };

    public string CurrentVersion =>
        _manager?.CurrentVersion?.ToString()
        ?? typeof(VelopackUpdater).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? "0.0.0";

    public async Task<UpdateOffer?> CheckAsync(CancellationToken ct)
    {
        var info = await _manager!.CheckForUpdatesAsync().WaitAsync(ct).ConfigureAwait(false);
        _pending = info;
        return info is null ? null : new UpdateOffer(info.TargetFullRelease.Version.ToString(), info.TargetFullRelease.NotesMarkdown);
    }

    public Task DownloadAsync(UpdateOffer offer, Action<int> progress, CancellationToken ct) =>
        _manager!.DownloadUpdatesAsync(_pending ?? throw new InvalidOperationException("Сначала проверьте обновления."), progress, ct);

    public void ApplyAndRestart(UpdateOffer offer) =>
        _manager!.ApplyUpdatesAndRestart(_pending?.TargetFullRelease ?? throw new InvalidOperationException("Сначала проверьте обновления."));

    public void ApplyOnExit(UpdateOffer offer) =>
        _manager!.WaitExitThenApplyUpdates(_pending?.TargetFullRelease ?? throw new InvalidOperationException("Сначала проверьте обновления."), silent: true, restart: false);

    private static IUpdateSource CreateSource(string feed)
    {
        if (Directory.Exists(feed))
            return new SimpleFileSource(new DirectoryInfo(feed));
        if (Uri.TryCreate(feed, UriKind.Absolute, out var uri) && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
            return new GithubSource(feed, accessToken: null, prerelease: false);
        return new SimpleWebSource(feed);
    }
}
