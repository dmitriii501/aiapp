using Kairo.Core.Config;
using Kairo.Core.Models;
using Kairo.Core.Permissions;
using Kairo.Core.Secrets;
using Kairo.Core.Sessions;
using Kairo.Host;
using Kairo.Swarm;
using Kairo.Tools;

namespace Kairo.Cli;

/// <summary><c>kairo swarm</c>: run, resume and inspect swarms of agents (docs/specs/swarm.md).</summary>
internal static class SwarmCommand
{
    public static string DatabasePath => Environment.GetEnvironmentVariable("KAIRO_SWARM_DB") ?? SwarmStore.DefaultPath;

    /// <summary>Commands that only read the swarm database.</summary>
    public static int Inspect(CliOptions options)
    {
        using var store = new SwarmStore(DatabasePath);
        var sub = options.Positional.FirstOrDefault();
        if (sub == "list" || sub is null)
        {
            foreach (var s in store.List())
                Console.WriteLine($"{s.Id}  {s.UpdatedAt.LocalDateTime:yyyy-MM-dd HH:mm}  {s.Status,-8}  {s.TokensUsed,10:N0} tokens  {Clip(s.Goal, 70)}");
            return 0;
        }

        if (sub == "status" && options.Positional.Count > 1)
        {
            var info = store.Get(options.Positional[1]);
            if (info is null)
                return Fail($"No swarm '{options.Positional[1]}'. Run 'kairo swarm list'.");
            PrintStatus(store, info);
            return 0;
        }
        if (sub == "clean" && options.Positional.Count > 1)
        {
            try
            {
                foreach (var line in SwarmCleaner.CleanAsync(store, options.Positional[1]).GetAwaiter().GetResult())
                    Console.WriteLine(line);
                return 0;
            }
            catch (SwarmException ex)
            {
                return Fail(ex.Message);
            }
        }
        return Fail("Usage: kairo swarm \"<goal>\" | list | status <id> | resume <id> | clean <id>");
    }

    public static async Task<int> RunAsync(
        CliOptions options, string cwd, KairoConfig config, PermissionRules rules, PermissionMode mode,
        IModelProvider provider, string model, SessionStore sessions, string userConfigDir, ISecretStore secrets)
    {
        using var store = new SwarmStore(DatabasePath);
        SwarmInfo info;

        if (options.Positional.FirstOrDefault() == "resume")
        {
            if (options.Positional.Count < 2 || store.Get(options.Positional[1]) is not { } existing)
                return Fail("Usage: kairo swarm resume <id>. Run 'kairo swarm list'.");
            info = existing;
            if (info.Status == SwarmStatus.Done)
                return Fail($"Swarm {info.Id} is finished.");
            var limits = Limits(options, info.Limits);
            if (limits != info.Limits)
            {
                store.SetLimits(info.Id, limits);
                info = store.Get(info.Id)!;
            }
        }
        else
        {
            var goal = string.Join(' ', options.Positional).Trim();
            if (goal.Length == 0)
                return Fail("Usage: kairo swarm \"<goal>\" [--workers N] [--max-tokens N] [--max-minutes N] [--concurrency N]");
            info = store.Create(cwd, goal, Limits(options, new SwarmLimits()));
        }

        var web = () => ProviderFactory.WebSearchOf(config, secrets);
        var ui = new ConsoleUi(interactive: true);
        var setup = new SwarmSetup(
            model,
            Path.Combine(userConfigDir, "swarms"),
            ui,
            rules,
            () => BuiltinTools.All(web, userConfigDirectory: userConfigDir),
            options.Effort ?? config.Effort,
            config.EffectiveContextWindow,
            config.EffectiveCompactAt,
            WorkerMode: mode == PermissionMode.Bypass ? PermissionMode.Bypass : PermissionMode.AcceptEdits,
            UserConfigDirectory: userConfigDir,
            Hooks: config.Hooks.Count > 0 ? new ProcessHooks(config.Hooks) : null,
            Log: Print);

        ConsoleUi.Write(ConsoleColor.Magenta, "Kairo swarm");
        ConsoleUi.Write(ConsoleColor.DarkGray, $"  {info.Id} · {model} · up to {info.Limits.MaxAgents} agents · {info.Limits.MaxTokens:N0} tokens · {cwd}\n");
        ConsoleUi.Write(ConsoleColor.DarkGray, $"  goal: {info.Goal}\n  Ctrl+C pauses the swarm; 'kairo swarm resume {info.Id}' continues it.\n\n");

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        var runner = new SwarmRunner(store, sessions, provider, setup);
        SwarmOutcome outcome;
        try
        {
            outcome = await runner.RunAsync(info.Id, cts.Token);
        }
        catch (SwarmException ex)
        {
            return Fail(ex.Message);
        }

        Console.WriteLine();
        ConsoleUi.Write(outcome.Status == SwarmStatus.Done ? ConsoleColor.Green : ConsoleColor.Yellow, $"Swarm {info.Id}: {outcome.Status}");
        ConsoleUi.Write(ConsoleColor.DarkGray, $"  ({outcome.TokensUsed:N0} tokens, {outcome.ModelCalls} model calls)\n");
        if (outcome.Summary is { Length: > 0 })
            Console.WriteLine("\n" + outcome.Summary);
        PrintStatus(store, store.Get(info.Id)!);
        if (outcome.Status != SwarmStatus.Done)
            Console.WriteLine($"\nContinue with: kairo swarm resume {info.Id}");
        return outcome.Status == SwarmStatus.Done ? 0 : 1;
    }

    private static SwarmLimits Limits(CliOptions o, SwarmLimits current) => current with
    {
        MaxAgents = o.Workers is { } w ? w + 1 : current.MaxAgents,
        MaxTokens = o.MaxTokens ?? current.MaxTokens,
        MaxMinutes = o.MaxMinutes ?? current.MaxMinutes,
        MaxConcurrency = o.Concurrency ?? current.MaxConcurrency,
    };

    private static void PrintStatus(SwarmStore store, SwarmInfo info)
    {
        Console.WriteLine($"\nSwarm {info.Id} [{info.Status}] — {info.Goal}");
        Console.WriteLine($"  {info.TokensUsed:N0} tokens over {info.ModelCalls} model calls (limit {info.Limits.MaxTokens:N0}); {info.CachedTokens:N0} of them read from the prompt cache");
        foreach (var a in store.Agents(info.Id))
            Console.WriteLine($"  {a.Name,-10} {a.Status,-8} {(a.Branch is null ? a.WorkingDirectory : a.Branch)}");
        Console.WriteLine("Board:");
        Console.WriteLine(SwarmTool.FormatBoard(store.Tasks(info.Id)).Replace("\n", "\n  ").Insert(0, "  "));
    }

    private static void Print(SwarmEvent e)
    {
        var color = e.Kind switch
        {
            "tool" => ConsoleColor.DarkGray,
            "said" => ConsoleColor.Gray,
            "spawn" => ConsoleColor.Cyan,
            "limit" => ConsoleColor.Red,
            "end" => ConsoleColor.Green,
            _ => ConsoleColor.Yellow,
        };
        var prefix = e.Kind switch { "tool" => "  ● ", "said" => "  » ", _ => "  ★ " };
        ConsoleUi.Write(ConsoleColor.Magenta, $"[{e.Agent}]");
        ConsoleUi.Write(color, $"{prefix}{Clip(e.Text.ReplaceLineEndings(" "), 220)}\n");
    }

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..max] + "…";

    private static int Fail(string message)
    {
        ConsoleUi.Write(ConsoleColor.Red, message + "\n");
        return 1;
    }
}
