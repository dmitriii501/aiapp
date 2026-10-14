using Kairo.Core.Config;
using Kairo.Core.Decisions;
using Kairo.Core.Secrets;
using Kairo.Host;
using Kairo.PcControl;

namespace Kairo.Cli;

/// <summary>
/// <c>kairo pc run &lt;app.exe&gt; "&lt;sub-goal&gt;" "&lt;success&gt;"</c>: the fast loop on its own, without a big model, for trying it out and for timing it.
/// Naming the program on the command line is the user's consent for that program for this run only; terminals, password managers
/// and system tools stay out of reach.
/// </summary>
internal static class PcCommand
{
    public static async Task<int> RunAsync(CliOptions options, string userConfigDir, ISecretStore secrets)
    {
        var args = options.Positional;
        if (args.Count < 4 || args[0] != "run")
            return Fail("Usage: kairo pc run <app.exe> \"<sub-goal>\" \"<success criterion>\" [--text name=value]... [--hotkeys \"ctrl+s, enter\"] [--launch] [--steps N]");

        KairoConfig config;
        try
        {
            config = KairoConfig.Load(userConfigDir, userConfigDir, includeProject: false);
        }
        catch (FormatException ex)
        {
            return Fail(ex.Message);
        }

        var app = PcGuard.Normalize(args[1]);
        var guard = new PcGuard([app]);
        if (guard.IsDenied(app))
            return Fail($"{app} cannot be controlled (terminals, password managers and system tools are always out of reach).");
        if (ProviderFactory.DecisionModelOf(config, secrets) is not { } model)
            return Fail("No Jev key. Enter it in Settings (\"Ключ Jev\") or set TYPESAFE_API_KEY.");

        var desktop = new UiaDesktop();
        var window = desktop.Windows().FirstOrDefault(w => w.Process == app);
        var launched = false;
        if (window is null)
        {
            if (!options.PcLaunch)
                return Fail($"No open window of {app}. Start it yourself or add --launch.");
            window = desktop.Launch(app, TimeSpan.FromSeconds(10));
            launched = true;
        }
        if (window is null)
            return Fail($"{app} showed no window.");

        var texts = new Dictionary<string, string>();
        foreach (var t in options.PcTexts)
        {
            var eq = t.IndexOf('=');
            if (eq <= 0)
                return Fail($"--text wants name=value, not '{t}'.");
            texts[t[..eq]] = t[(eq + 1)..];
        }
        var hotkeys = (options.PcHotkeys ?? "").Split([',', ';'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        ConsoleUi.Write(ConsoleColor.Magenta, "Kairo · PC");
        ConsoleUi.Write(ConsoleColor.DarkGray, $"  {window.Process} {window.Id} \"{window.Title}\" · Pause stops it\n\n");

        // Open the connection first, so the first decision is as quick as the rest.
        if (model is IWarmUp warm)
            await warm.WarmUpAsync();
        var pursue = new PursueLoop(desktop, guard, model, new PursueOptions(MaxSteps: options.PcSteps ?? config.PcMaxSteps ?? 25, MinConfidence: config.PcMinConfidence ?? 0.7));
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        var report = await pursue.RunAsync(
            window.Handle,
            new PursuePlan(args[2], args[3], texts, hotkeys),
            cts.Token,
            s => ConsoleUi.Write(s.Executed ? ConsoleColor.Gray : ConsoleColor.Yellow,
                $"  {s.Number,2}. {s.Action}  ({s.Confidence:0.00})  looked {s.ObserveMs} ms · decided {s.DecideMs} ms · acted {s.ActMs} ms  {(s.Executed ? "" : "· " + s.Note)}\n"));

        Console.WriteLine();
        ConsoleUi.Write(report.Status == PursueStatus.Done ? ConsoleColor.Green : ConsoleColor.Yellow, $"{report.Status}: {report.Reason}\n");
        var executed = report.Steps.Count(s => s.Executed);
        ConsoleUi.Write(ConsoleColor.DarkGray, $"  {report.Steps.Count} decisions, {executed} actions, {report.TotalMs} ms in all" +
            (report.Steps.Count > 0 ? $" ({report.TotalMs / report.Steps.Count} ms a step)" : "") + "\n");
        if (report.Pending is not null)
            Console.WriteLine($"  the fast model wanted: {report.Pending}");

        if (launched && options.PcLaunch && !options.PcKeep)
        {
            try
            {
                System.Diagnostics.Process.GetProcessById(window.ProcessId).Kill();
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
            }
        }
        return report.Status == PursueStatus.Done ? 0 : 1;
    }

    private static int Fail(string message)
    {
        ConsoleUi.Write(ConsoleColor.Red, message + "\n");
        return 1;
    }
}

