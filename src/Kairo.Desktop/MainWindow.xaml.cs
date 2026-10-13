using Kairo.Core.Config;
using Kairo.Core.Rpc;
using Kairo.Core.Sessions;
using Kairo.Host;
using Kairo.Mcp;
using Kairo.Protocol;
using Kairo.Swarm;
using Kairo.Windows;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Windows.Graphics;
using Windows.Storage.Pickers;
using Windows.System;

namespace Kairo.Desktop;

public sealed partial class MainWindow : Window
{
    private const string AppHost = "app.kairo.local";

    private readonly RpcDispatcher _rpc;
    private readonly SessionStore _store;
    private string _origin = "";

    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.Resize(new SizeInt32(1280, 820));

        // Core → UI. Handlers run off the UI thread, so marshal every send back to it.
        _rpc = new RpcDispatcher(json =>
        {
            DispatcherQueue.TryEnqueue(() => Web.CoreWebView2?.PostWebMessageAsJson(json));
            return Task.CompletedTask;
        });

        _store = new SessionStore(SessionStore.DefaultPath);
        var settings = new SettingsService(KairoConfig.UserConfigDirectory, new CredentialStore());
        var ctx = ProtocolJsonContext.Default;
        var mcpConfig = new McpConfigStore(McpConfigStore.DefaultPath);
        var mcpManager = new McpManager(mcpConfig);
        var host = new AgentHost(
            _store,
            settings,
            notify: ev => _rpc.NotifyAsync(Notifications.AgentEvent, ev, ctx.AgentEventDto),
            sessionsChanged: () => _rpc.NotifyAsync(Notifications.SessionsChanged, new Empty(), ctx.Empty),
            mcp: mcpManager);
        var swarmStore = new SwarmStore(SwarmStore.DefaultPath);
        var swarms = new SwarmService(
            swarmStore,
            _store,
            settings,
            detail => _rpc.NotifyAsync(Notifications.SwarmChanged, detail, ctx.SwarmDetail),
            mcp: mcpManager);
        var shell = ShellLocator.Find();
        var terminals = new TerminalService(
            new ConPtyFactory(),
            _store,
            shell.CommandLine,
            shell.Title,
            output => _rpc.NotifyAsync(Notifications.TerminalOutputEvent, output, ctx.TerminalOutput),
            exited => _rpc.NotifyAsync(Notifications.TerminalExitedEvent, exited, ctx.TerminalExited));

        // Stops the user's shells and the stdio servers so they do not outlive the window or an update restart.
        Task ShutDown()
        {
            terminals.Dispose();
            swarms.StopAll();
            swarmStore.Dispose();
            mcpManager.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3));
            _store.Dispose();
            return Task.CompletedTask;
        }

        // The update feed comes from the environment, the user's own config, or the release build; never from a project.
        var userConfig = settings.LoadUser();
        var feed = Environment.GetEnvironmentVariable("KAIRO_UPDATE_URL") ?? userConfig.UpdateUrl ?? VelopackUpdater.BakedInFeed;
        var updates = new UpdateService(
            new VelopackUpdater(feed),
            autoCheck: userConfig.UpdateCheck ?? true,
            isBusy: () => host.AnyRunning() || swarms.AnyRunning(),
            beforeRestart: ShutDown,
            notify: status => _rpc.NotifyAsync(Notifications.UpdateChanged, status, ctx.UpdateStatus));

        HostMethods.Register(_rpc, host, settings, _store, new McpService(mcpManager, mcpConfig), swarms, terminals, updates, PickFolderAsync, hostName: "desktop");

        // Servers connect and updates are checked in the background; the window does not wait for them.
        _ = mcpManager.ReloadAsync();
        updates.StartAutoCheck();

        Closed += (_, _) =>
        {
            updates.Dispose();
            _ = ShutDown();
        };

        _ = InitializeWebViewAsync();
    }

    /// <summary>Native folder picker, always on the UI thread. The only way a new folder becomes a workspace.</summary>
    private Task<string?> PickFolderAsync()
    {
        var result = new TaskCompletionSource<string?>();
        DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
                picker.FileTypeFilter.Add("*");
                WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
                var folder = await picker.PickSingleFolderAsync();
                result.SetResult(folder?.Path);
            }
            catch (Exception ex)
            {
                result.SetException(ex);
            }
        });
        return result.Task;
    }

    private async Task InitializeWebViewAsync()
    {
        // The browser profile lives in the user's local data, not next to the exe: the install folder is replaced on every update
        // and may not be writable.
        var userData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kairo", "WebView2");
        var environment = await CoreWebView2Environment.CreateWithOptionsAsync(null, userData, new CoreWebView2EnvironmentOptions());
        await Web.EnsureCoreWebView2Async(environment);
        var core = Web.CoreWebView2;

        var settings = core.Settings;
        settings.IsWebMessageEnabled = true;
        settings.AreHostObjectsAllowed = false; // the bridge is JSON messages only, never host objects
        settings.IsStatusBarEnabled = false;
        settings.IsZoomControlEnabled = false;
        settings.IsGeneralAutofillEnabled = false;
        settings.IsPasswordAutosaveEnabled = false;
#if !DEBUG
        settings.AreDevToolsEnabled = false;
        settings.AreDefaultContextMenusEnabled = false;
        settings.AreBrowserAcceleratorKeysEnabled = false;
#endif

        // KAIRO_UI_DEV_URL=http://localhost:5173 loads the Vite dev server with hot reload.
        Uri start;
        if (Environment.GetEnvironmentVariable("KAIRO_UI_DEV_URL") is { Length: > 0 } devUrl)
        {
            start = new Uri(devUrl);
        }
        else
        {
            core.SetVirtualHostNameToFolderMapping(
                AppHost, Path.Combine(AppContext.BaseDirectory, "ui"), CoreWebView2HostResourceAccessKind.DenyCors);
            start = new Uri($"https://{AppHost}/index.html");
        }
        _origin = start.GetLeftPart(UriPartial.Authority);

        core.NavigationStarting += (sender, e) =>
        {
            if (IsAppOrigin(e.Uri))
                return;
            e.Cancel = true;
            _ = OpenExternallyAsync(e.Uri);
        };
        core.NewWindowRequested += (sender, e) =>
        {
            e.Handled = true;
            _ = OpenExternallyAsync(e.Uri);
        };
        core.WebMessageReceived += OnWebMessageReceived;

        core.Navigate(start.ToString());
    }

    private async void OnWebMessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!IsAppOrigin(e.Source))
            return;

        string json;
        try
        {
            json = e.WebMessageAsJson;
        }
        catch (ArgumentException)
        {
            return;
        }

        await _rpc.HandleAsync(json);
    }

    private bool IsAppOrigin(string uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var u) &&
        string.Equals(u.GetLeftPart(UriPartial.Authority), _origin, StringComparison.OrdinalIgnoreCase);

    /// <summary>Links leave the app only through a native confirmation, and only as http(s).</summary>
    private async Task OpenExternallyAsync(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var target) || (target.Scheme != Uri.UriSchemeHttps && target.Scheme != Uri.UriSchemeHttp))
            return;

        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "Открыть ссылку в браузере?",
            Content = target.ToString(),
            PrimaryButtonText = "Открыть",
            CloseButtonText = "Отмена",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            await Launcher.LaunchUriAsync(target);
    }
}
