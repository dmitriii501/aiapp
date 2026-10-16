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
        if (args.Count == 1 && args[0] == "stats")
            return Stats(userConfigDir);
        if (args.Count >= 2 && args[0] == "play")
            return await PlayAsync(options, userConfigDir, secrets);
        if (args.Count >= 2 && args[0] == "author")
            return await AuthorAsync(options, userConfigDir, secrets);
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
        var pursue = new PursueLoop(desktop, guard, model, PcTools.OptionsFor(config, PcTools.SharedHistory(config)) with { MaxSteps = options.PcSteps ?? config.PcMaxSteps ?? 0 });
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

    /// <summary><c>kairo pc stats</c>: what the decision log says about how the fast model does in each program.</summary>
    private static int Stats(string userConfigDir)
    {
        KairoConfig config;
        try
        {
            config = KairoConfig.Load(userConfigDir, userConfigDir, includeProject: false);
        }
        catch (FormatException ex)
        {
            return Fail(ex.Message);
        }
        if (PcTools.SharedHistory(config) is not { } history)
            return Fail("Calibration is off ([pc] calibrate = false): nothing is logged.");
        var baseline = config.PcMinConfidence ?? 0.7;
        var stats = history.Stats(baseline);
        if (stats.Count == 0)
        {
            Console.WriteLine("No decisions logged yet. Run something with PcPursue or PcRunPolicy first.");
            return 0;
        }
        ConsoleUi.Write(ConsoleColor.Magenta, "Kairo · PC decisions");
        ConsoleUi.Write(ConsoleColor.DarkGray, $"  {history.Count} logged · your bar {baseline:0.00} · pc-decisions.jsonl in the config folder\n\n");
        foreach (var app in stats)
        {
            var raised = app.Threshold > baseline + 1e-9;
            ConsoleUi.Write(ConsoleColor.Gray, $"{app.App}: {app.Executed} steps taken, {app.SuccessRate:P0} changed the screen; bar ");
            ConsoleUi.Write(raised ? ConsoleColor.Yellow : ConsoleColor.Green, $"{app.Threshold:0.00}{(raised ? " (raised: steps kept failing)" : "")}\n");
            foreach (var (band, count, rate) in app.Bands)
                Console.WriteLine($"    confidence {band}: {count} steps, {rate:P0} worked");
            if (app.Handovers.Count > 0)
                Console.WriteLine("    handed to the big model: " + string.Join(", ", app.Handovers.OrderByDescending(h => h.Value).Select(h => $"{h.Key} {h.Value}")));
        }
        return 0;
    }

    /// <summary>
    /// <c>kairo pc author "&lt;the game, in words&gt;" [--model id] [--save policy.json]</c>: asks a model to write the policy for a game from a
    /// description, with the same text the agent gets (the PcPlay description and the /game guide), and checks what comes back. A way to
    /// find out whether a model can write policies, and what it gets wrong.
    /// </summary>
    private static async Task<int> AuthorAsync(CliOptions options, string userConfigDir, ISecretStore secrets)
    {
        KairoConfig config;
        try
        {
            config = KairoConfig.Load(userConfigDir, userConfigDir, includeProject: false);
        }
        catch (FormatException ex)
        {
            return Fail(ex.Message);
        }
        var provider = ProviderFactory.Create(config, secrets);
        var model = options.Model ?? ProviderFactory.ModelOf(config);
        var system = "You drive a Windows PC for the user with tools. The user asks you to play a game in a window. You will call the PcPlay tool with a policy. " +
                     "Write ONLY that policy as one JSON object, nothing else (no explanation, no code fence).\n\nThe PcPlay tool description:\n" + GamePolicyParser.ToolDescription +
                     "\n\nThe /game guide:\n" + Kairo.Core.Context.BuiltInGuides.Find("game")!.Body;
        var request = new Kairo.Core.Models.ModelRequest(model, [Kairo.Core.Models.ChatMessage.UserText(args(options)[1])], system, MaxTokens: 6000);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        Kairo.Core.Models.ModelResponse? response = null;
        await foreach (var ev in provider.StreamAsync(request, default))
        {
            if (ev is Kairo.Core.Models.ModelResponse r)
                response = r;
        }
        var text = response?.Message.Text ?? "";
        ConsoleUi.Write(ConsoleColor.Magenta, "Kairo · pc author");
        ConsoleUi.Write(ConsoleColor.DarkGray, $"  {model} · {clock.Elapsed.TotalSeconds:0.0} s\n\n");
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start)
            return Fail("The model wrote no JSON object:\n" + text);
        var json = text[start..(end + 1)];
        System.Text.Json.JsonDocument document;
        try
        {
            document = System.Text.Json.JsonDocument.Parse(json);
        }
        catch (System.Text.Json.JsonException ex)
        {
            return Fail($"The model's JSON does not parse ({ex.Message}):\n{json}");
        }
        using (document)
        {
            var parsed = GamePolicyParser.Parse(document.RootElement, new PcGuard([]));
            Console.WriteLine(json);
            Console.WriteLine();
            if (parsed.Policy is not { } policy)
            {
                ConsoleUi.Write(ConsoleColor.Red, "REFUSED by the policy checker:\n- " + string.Join("\n- ", parsed.Errors) + "\n");
                return 1;
            }
            ConsoleUi.Write(ConsoleColor.Green, "ACCEPTED\n");
            Console.WriteLine(policy.Describe());
            if (options.PcSave is { } path)
            {
                await File.WriteAllTextAsync(path, json);
                Console.WriteLine($"Saved to {path}");
            }
        }
        return 0;
    }

    private static IReadOnlyList<string> args(CliOptions options) => options.Positional;

    /// <summary><c>kairo pc play &lt;policy.json&gt; [title fragment] [--dry]</c>: a game policy on the open window of its program, with the timings of every tick.</summary>
    private static async Task<int> PlayAsync(CliOptions options, string userConfigDir, ISecretStore secrets)
    {
        var args = options.Positional;
        if (!File.Exists(args[1]))
            return Fail($"No such file: {args[1]}");
        KairoConfig config;
        try
        {
            config = KairoConfig.Load(userConfigDir, userConfigDir, includeProject: false);
        }
        catch (FormatException ex)
        {
            return Fail(ex.Message);
        }
        if (ProviderFactory.DecisionModelOf(config, secrets) is not { } model)
            return Fail("No Jev key. Enter it in Settings (\"Ключ Jev\") or set TYPESAFE_API_KEY.");

        using var document = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(args[1]));
        var probe = GamePolicyParser.Parse(document.RootElement, new PcGuard([]));
        if (probe.Policy is not { } policy)
            return Fail("The policy has problems:\n- " + string.Join("\n- ", probe.Errors));
        var guard = new PcGuard([policy.App]);
        var desktop = new UiaDesktop();
        var title = args.Count > 2 ? args[2] : null;
        var window = desktop.Windows().FirstOrDefault(w => w.Process == policy.App && (title is null || w.Title.Contains(title, StringComparison.OrdinalIgnoreCase)));
        if (window is null)
            return Fail($"No open window of {policy.App}{(title is null ? "" : $" with '{title}' in its title")}. Open the game first.");

        ConsoleUi.Write(ConsoleColor.Magenta, "Kairo · pc play");
        ConsoleUi.Write(ConsoleColor.DarkGray, $"  {policy.Goal} · {window.Process} \"{window.Title}\" · Pause stops\n\n");
        using var cancel = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cancel.Cancel();
        };
        var result = await GameRunner.RunAsync(desktop, guard, model, policy, window.Handle, new GameRunOptions(options.PcDry, line => Console.WriteLine("  " + line)), cancel.Token);
        Console.WriteLine();
        Console.WriteLine(GameRunner.Describe(result));
        Console.WriteLine($"Window title now: {desktop.Window(window.Handle)?.Title}");
        return result.Status == GameStatus.Failed ? 1 : 0;
    }

    private static int Fail(string message)
    {
        ConsoleUi.Write(ConsoleColor.Red, message + "\n");
        return 1;
    }
}

