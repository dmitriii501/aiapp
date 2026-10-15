using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Velopack;

namespace Kairo.Desktop;

/// <summary>
/// Own entry point instead of the XAML-generated one: Velopack has to run first, because the installer starts the
/// app with special arguments (after install, before update, before uninstall) and expects it to exit quickly.
/// </summary>
public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        VelopackApp.Build().Run();

        // The app starts in its own install folder ("current"). Every program it starts inherits that as its working directory, and a folder
        // that is some process's working directory cannot be moved: an update would fail while Paint, a terminal or an MCP server is open.
        try
        {
            Directory.SetCurrentDirectory(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
        }

        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(callback =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new App();
        });
    }
}
