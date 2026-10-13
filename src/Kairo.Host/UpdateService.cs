using Kairo.Core.Rpc;
using Kairo.Protocol;

namespace Kairo.Host;

/// <summary>An update the feed offers. Notes are markdown from the release.</summary>
public sealed record UpdateOffer(string Version, string? Notes);

/// <summary>The installer technology behind updates (Velopack in the desktop app). Kept out of the host so it can be faked.</summary>
public interface IAppUpdater
{
    /// <summary>True only for an installed build with an update feed. A build run from the repository cannot update itself.</summary>
    bool CanUpdate { get; }

    string CurrentVersion { get; }

    /// <summary>Null when the installed version is the newest.</summary>
    Task<UpdateOffer?> CheckAsync(CancellationToken ct);

    Task DownloadAsync(UpdateOffer offer, Action<int> progress, CancellationToken ct);

    /// <summary>Starts the new version and ends this process.</summary>
    void ApplyAndRestart(UpdateOffer offer);
}

/// <summary>
/// Checks for updates in the background and installs one when the user asks. It never installs on its own,
/// and never while an agent is working or a permission is pending.
/// </summary>
public sealed class UpdateService(
    IAppUpdater updater,
    bool autoCheck,
    Func<bool> isBusy,
    Func<Task> beforeRestart,
    Func<UpdateStatus, Task> notify) : IDisposable
{
    public static readonly TimeSpan FirstCheckDelay = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);

    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private UpdateStatus _status = new(updater.CanUpdate ? "idle" : "disabled", updater.CurrentVersion);
    private UpdateOffer? _offer;
    private bool _busy;

    public UpdateStatus Status
    {
        get { lock (_gate) return _status; }
    }

    /// <summary>Starts the periodic check. Does nothing for builds that cannot update or when the user turned checks off.</summary>
    public void StartAutoCheck(TimeSpan? firstDelay = null, TimeSpan? interval = null)
    {
        if (!updater.CanUpdate || !autoCheck)
            return;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(firstDelay ?? FirstCheckDelay, _stop.Token).ConfigureAwait(false);
                using var timer = new PeriodicTimer(interval ?? CheckInterval);
                do
                {
                    // Do not replace an offer or a download in progress with a background check.
                    if (Status.State is "idle" or "error")
                        await CheckAsync().ConfigureAwait(false);
                }
                while (await timer.WaitForNextTickAsync(_stop.Token).ConfigureAwait(false));
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    public async Task<UpdateStatus> CheckAsync()
    {
        if (!updater.CanUpdate)
            return Status;
        lock (_gate)
        {
            if (_busy)
                return _status;
            _busy = true;
        }

        await Set(new UpdateStatus("checking", updater.CurrentVersion)).ConfigureAwait(false);
        try
        {
            var offer = await updater.CheckAsync(_stop.Token).ConfigureAwait(false);
            lock (_gate)
                _offer = offer;
            await Set(offer is null
                ? new UpdateStatus("idle", updater.CurrentVersion)
                : new UpdateStatus("available", updater.CurrentVersion, offer.Version, offer.Notes)).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            await Set(new UpdateStatus("error", updater.CurrentVersion, Error: Describe(ex))).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
                _busy = false;
        }
        return Status;
    }

    /// <summary>Starts downloading in the background and restarts when done. Progress and errors arrive as status changes.</summary>
    public void Install()
    {
        UpdateOffer offer;
        lock (_gate)
        {
            if (_offer is null || _status.State != "available")
                throw new RpcException(RpcErrorCodes.InvalidRequest, "Нет обновления для установки. Сначала проверьте обновления.");
            if (isBusy())
                throw new RpcException(RpcErrorCodes.InvalidRequest, "Агент ещё работает. Дождитесь ответа или остановите его, затем обновляйтесь.");
            if (_busy)
                throw new RpcException(RpcErrorCodes.InvalidRequest, "Обновление уже устанавливается.");
            _busy = true;
            offer = _offer;
        }
        _ = Task.Run(() => DownloadAndRestartAsync(offer));
    }

    public void Dispose() => _stop.Cancel();

    private async Task DownloadAndRestartAsync(UpdateOffer offer)
    {
        try
        {
            var last = -1;
            await Set(new UpdateStatus("downloading", updater.CurrentVersion, offer.Version, offer.Notes, Progress: 0)).ConfigureAwait(false);
            await updater.DownloadAsync(offer, percent =>
            {
                // The bridge is not a progress bar: one message per 5 percent is plenty.
                if (percent / 5 == last / 5 && percent < 100)
                    return;
                last = percent;
                _ = Set(new UpdateStatus("downloading", updater.CurrentVersion, offer.Version, offer.Notes, Progress: percent));
            }, _stop.Token).ConfigureAwait(false);

            // The user may have started an agent while the download ran.
            if (isBusy())
            {
                await Set(new UpdateStatus("available", updater.CurrentVersion, offer.Version, offer.Notes, Error: "Агент начал работу, пока шла загрузка. Установите обновление, когда он закончит.")).ConfigureAwait(false);
                return;
            }

            await beforeRestart().ConfigureAwait(false);
            updater.ApplyAndRestart(offer);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            await Set(new UpdateStatus("error", updater.CurrentVersion, Error: Describe(ex))).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
                _busy = false;
        }
    }

    private async Task Set(UpdateStatus status)
    {
        lock (_gate)
            _status = status;
        try
        {
            await notify(status).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The window is gone; the next UpdateGet will show the state.
        }
    }

    private static string Describe(Exception ex) => ex switch
    {
        HttpRequestException => "Не удалось связаться с сервером обновлений. Проверьте подключение к интернету.",
        _ => ex.Message.ReplaceLineEndings(" ") is { Length: > 300 } text ? text[..300] + "…" : ex.Message.ReplaceLineEndings(" "),
    };
}
