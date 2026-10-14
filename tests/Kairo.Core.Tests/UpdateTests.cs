using System.Collections.Concurrent;
using Kairo.Core.Rpc;
using Kairo.Host;
using Kairo.Protocol;

namespace Kairo.Core.Tests;

public class UpdateTests
{
    private sealed class FakeUpdater : IAppUpdater
    {
        public bool CanUpdate { get; set; } = true;
        public string CurrentVersion => "0.1.0";
        public UpdateOffer? Offer { get; set; }
        public Exception? CheckFails { get; set; }
        public Exception? DownloadFails { get; set; }
        public TaskCompletionSource? DownloadGate { get; set; }
        public int Checks { get; private set; }
        public UpdateOffer? Applied { get; private set; }
        public UpdateOffer? AppliedOnExit { get; private set; }
        public int Downloads { get; private set; }
        public TaskCompletionSource AppliedSignal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<UpdateOffer?> CheckAsync(CancellationToken ct)
        {
            Checks++;
            return CheckFails is null ? Task.FromResult(Offer) : Task.FromException<UpdateOffer?>(CheckFails);
        }

        public async Task DownloadAsync(UpdateOffer offer, Action<int> progress, CancellationToken ct)
        {
            Downloads++;
            foreach (var p in new[] { 0, 1, 2, 3, 4, 5, 50, 100 })
                progress(p);
            if (DownloadGate is not null)
                await DownloadGate.Task;
            if (DownloadFails is not null)
                throw DownloadFails;
        }

        public void ApplyOnExit(UpdateOffer offer) => AppliedOnExit = offer;

        public void ApplyAndRestart(UpdateOffer offer)
        {
            Applied = offer;
            AppliedSignal.TrySetResult();
        }
    }

    private readonly FakeUpdater _updater = new();
    private readonly ConcurrentQueue<UpdateStatus> _events = new();
    private bool _agentBusy;
    private int _beforeRestart;

    private UpdateService Create(bool autoCheck = true, bool autoInstall = false) =>
        new(_updater, autoCheck, () => _agentBusy, () => { _beforeRestart++; return Task.CompletedTask; }, s => { _events.Enqueue(s); return Task.CompletedTask; }, autoInstall);

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out.");
            await Task.Delay(5);
        }
    }

    [Fact]
    public async Task A_build_that_cannot_update_stays_disabled()
    {
        _updater.CanUpdate = false;
        using var service = Create();

        var status = await service.CheckAsync();

        Assert.Equal("disabled", status.State);
        Assert.Equal(0, _updater.Checks);
        Assert.Empty(_events);
    }

    [Fact]
    public async Task A_check_reports_an_available_version_or_none()
    {
        using var service = Create();

        var none = await service.CheckAsync();
        _updater.Offer = new UpdateOffer("0.2.0", "- faster");
        var offered = await service.CheckAsync();

        Assert.Equal("idle", none.State);
        Assert.Equal(new UpdateStatus("available", "0.1.0", "0.2.0", "- faster"), offered);
        Assert.Equal(["checking", "idle", "checking", "available"], _events.Select(e => e.State));
    }

    [Fact]
    public async Task A_failed_check_is_an_error_status_not_an_exception()
    {
        _updater.CheckFails = new HttpRequestException("boom");
        using var service = Create();

        var status = await service.CheckAsync();

        Assert.Equal("error", status.State);
        Assert.Contains("подключение", status.Error);
    }

    [Fact]
    public async Task Install_needs_an_offer_and_refuses_while_an_agent_works()
    {
        using var service = Create();
        Assert.Throws<RpcException>(service.Install); // nothing offered yet

        _updater.Offer = new UpdateOffer("0.2.0", null);
        await service.CheckAsync();
        _agentBusy = true;
        var ex = Assert.Throws<RpcException>(service.Install);

        Assert.Contains("Агент ещё работает", ex.Message);
        Assert.Equal("available", service.Status.State);
        Assert.Null(_updater.Applied);
    }

    [Fact]
    public async Task Install_downloads_with_throttled_progress_then_restarts()
    {
        _updater.Offer = new UpdateOffer("0.2.0", null);
        using var service = Create();
        await service.CheckAsync();

        service.Install();
        await _updater.AppliedSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("0.2.0", _updater.Applied!.Version);
        Assert.Equal(1, _beforeRestart);
        var progress = _events.Where(e => e.State == "downloading").Select(e => e.Progress).ToList();
        Assert.Equal([0, 5, 50, 100], progress); // 1-4 are skipped
    }

    [Fact]
    public async Task A_failed_download_is_reported_and_nothing_restarts()
    {
        _updater.Offer = new UpdateOffer("0.2.0", null);
        _updater.DownloadFails = new InvalidOperationException("disk full");
        using var service = Create();
        await service.CheckAsync();

        service.Install();
        await Until(() => service.Status.State == "error");

        Assert.Contains("disk full", service.Status.Error);
        Assert.Null(_updater.Applied);
        Assert.Equal(0, _beforeRestart);
    }

    [Fact]
    public async Task An_agent_started_during_the_download_postpones_the_restart()
    {
        _updater.Offer = new UpdateOffer("0.2.0", null);
        _updater.DownloadGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using var service = Create();
        await service.CheckAsync();

        service.Install();
        await Until(() => service.Status.State == "downloading");
        _agentBusy = true;
        _updater.DownloadGate.SetResult();
        await Until(() => service.Status.State == "available");

        Assert.Contains("Агент начал работу", service.Status.Error);
        Assert.Null(_updater.Applied);
    }

    [Fact]
    public async Task With_auto_install_a_found_update_is_downloaded_at_once_and_waits_for_the_next_close()
    {
        _updater.Offer = new UpdateOffer("0.2.0", "- faster");
        using var service = Create(autoInstall: true);

        await service.CheckAsync();
        await Until(() => service.Status.State == "ready");

        Assert.Equal(new UpdateStatus("ready", "0.1.0", "0.2.0", "- faster"), service.Status);
        Assert.Equal(1, _updater.Downloads);
        Assert.Null(_updater.Applied); // it does not restart on its own
        Assert.Equal(0, _beforeRestart);

        service.ApplyOnExit();

        Assert.Equal("0.2.0", _updater.AppliedOnExit!.Version);
    }

    [Fact]
    public async Task Without_auto_install_nothing_is_downloaded_until_the_user_asks()
    {
        _updater.Offer = new UpdateOffer("0.2.0", null);
        using var service = Create(autoInstall: false);

        await service.CheckAsync();
        await Task.Delay(100);
        service.ApplyOnExit();

        Assert.Equal("available", service.Status.State);
        Assert.Equal(0, _updater.Downloads);
        Assert.Null(_updater.AppliedOnExit);
    }

    [Fact]
    public async Task A_downloaded_update_restarts_at_once_on_request_but_not_while_an_agent_works()
    {
        _updater.Offer = new UpdateOffer("0.2.0", null);
        using var service = Create(autoInstall: true);
        await service.CheckAsync();
        await Until(() => service.Status.State == "ready");

        _agentBusy = true;
        Assert.Throws<RpcException>(service.Install);
        Assert.Null(_updater.Applied);

        _agentBusy = false;
        service.Install();
        await _updater.AppliedSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, _updater.Downloads); // not downloaded a second time
        Assert.Equal(1, _beforeRestart);
    }

    [Fact]
    public async Task A_failed_automatic_download_is_an_error_and_the_next_check_tries_again()
    {
        _updater.Offer = new UpdateOffer("0.2.0", null);
        _updater.DownloadFails = new InvalidOperationException("disk full");
        using var service = Create(autoInstall: true);

        await service.CheckAsync();
        await Until(() => service.Status.State == "error");

        _updater.DownloadFails = null;
        await service.CheckAsync();
        await Until(() => service.Status.State == "ready");
        Assert.Equal(2, _updater.Downloads);
    }

    [Fact]
    public void The_auto_install_switch_is_read_from_the_users_own_config_only()
    {
        using var dir = new Fakes.TempDir();
        var user = Path.Combine(dir.Path, "user");
        var project = Path.Combine(dir.Path, "project");
        Directory.CreateDirectory(user);
        Directory.CreateDirectory(Path.Combine(project, ".kairo"));
        File.WriteAllText(Path.Combine(user, "config.toml"), "[update]\nauto_install = false\n");
        File.WriteAllText(Path.Combine(project, ".kairo", "config.toml"), "[update]\nauto_install = true\n");

        Assert.False(Kairo.Core.Config.KairoConfig.Load(project, user, includeProject: true).UpdateAuto);
    }

    [Fact]
    public async Task Auto_check_runs_only_when_enabled_and_updatable()
    {
        using var on = Create(autoCheck: true);
        on.StartAutoCheck(TimeSpan.FromMilliseconds(10), TimeSpan.FromHours(1));
        await Until(() => _updater.Checks == 1);

        var checks = _updater.Checks;
        using var off = Create(autoCheck: false);
        off.StartAutoCheck(TimeSpan.FromMilliseconds(10), TimeSpan.FromHours(1));
        await Task.Delay(100);

        Assert.Equal(checks, _updater.Checks);
    }

    [Fact]
    public void Update_settings_come_only_from_the_users_own_config()
    {
        using var dir = new Fakes.TempDir();
        var user = Path.Combine(dir.Path, "user");
        var project = Path.Combine(dir.Path, "project");
        Directory.CreateDirectory(user);
        Directory.CreateDirectory(Path.Combine(project, ".kairo"));
        File.WriteAllText(Path.Combine(user, "config.toml"), "[update]\ncheck = false\n");
        File.WriteAllText(Path.Combine(project, ".kairo", "config.toml"), "[update]\ncheck = true\nurl = \"https://evil.example/feed\"\n");

        var config = Kairo.Core.Config.KairoConfig.Load(project, user, includeProject: true);

        Assert.False(config.UpdateCheck);
        Assert.Null(config.UpdateUrl);
    }
}
