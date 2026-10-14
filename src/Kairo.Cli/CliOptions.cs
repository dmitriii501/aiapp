namespace Kairo.Cli;

internal sealed class CliOptions
{
    public string? Command { get; private set; }
    public string? Prompt { get; private set; }
    public bool Continue { get; private set; }
    public string? Resume { get; private set; }
    public string? Provider { get; private set; }
    public string? Model { get; private set; }
    public string? BaseUrl { get; private set; }
    public string? Effort { get; private set; }
    public string? Mode { get; private set; }
    public string? WorkingDirectory { get; private set; }
    public List<string> Allow { get; } = [];
    public bool NoMcp { get; private set; }
    public List<string> Positional { get; } = [];
    public int? Workers { get; private set; }
    public long? MaxTokens { get; private set; }
    public int? MaxMinutes { get; private set; }
    public int? Concurrency { get; private set; }
    public List<string> PcTexts { get; } = [];
    public string? PcHotkeys { get; private set; }
    public bool PcLaunch { get; private set; }
    public bool PcKeep { get; private set; }
    public int? PcSteps { get; private set; }
    public bool Help { get; private set; }

    public const string Usage = """
        Kairo — AI agent for Windows

        Usage:
          kairo                      interactive session in the current directory
          kairo -p "<task>"          run one task non-interactively and print the result
          kairo -c                   continue the latest session in this directory
          kairo -r <id>              resume a session by id
          kairo sessions             list recent sessions
          kairo swarm "<goal>"       run a swarm: a lead and workers in git worktrees (see docs/specs/swarm.md)
          kairo swarm list | status <id> | resume <id> | clean <id>   (clean removes the workers' worktrees and merged branches)
          kairo pc run <app.exe> "<sub-goal>" "<success>"   the fast loop (Jev) on one program, with timings: --text name=value, --hotkeys "ctrl+s", --launch, --steps N
          kairo jev bench [n]        measure the Jev decision model: latency (cold, warm, parallel) and accuracy on English and Russian UI

        Options:
          --provider <id>            anthropic (default) | openai-compatible
          --model <id>               model id (anthropic default: claude-opus-5-5)
          --base-url <url>           API endpoint: required for openai-compatible (e.g. http://localhost:11434/v1),
                                     optional for anthropic (Anthropic-format gateways)
          --effort <level>           low | medium | high | xhigh | max
          --mode <mode>              default | acceptEdits | plan | bypass
          --allow "<rule>"           extra allow rule, e.g. "Shell(dotnet test:*)" (repeatable)
          --cwd <dir>                working directory
          --workers <n>              swarm: most workers (default 4)
          --max-tokens <n>           swarm: token budget over all agents (default 3000000)
          --max-minutes <n>          swarm: time limit (default 120)
          --concurrency <n>          swarm: model requests at once (default 3)
          --no-mcp                   do not connect MCP servers from %APPDATA%\Kairo\mcp.json

        Keys: ANTHROPIC_API_KEY for anthropic, KAIRO_API_KEY for openai-compatible (optional), TYPESAFE_API_KEY for Jev.
        Settings: %APPDATA%\Kairo\config.toml and .kairo\config.toml in the project.
        """;

    public static CliOptions Parse(string[] args)
    {
        var o = new CliOptions();
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{a} needs a value.");

            switch (a)
            {
                case "-h" or "--help": o.Help = true; break;
                case "-p" or "--print": o.Prompt = Next(); break;
                case "-c" or "--continue": o.Continue = true; break;
                case "-r" or "--resume": o.Resume = Next(); break;
                case "--provider": o.Provider = Next(); break;
                case "--model": o.Model = Next(); break;
                case "--base-url": o.BaseUrl = Next(); break;
                case "--effort": o.Effort = Next(); break;
                case "--mode": o.Mode = Next(); break;
                case "--allow": o.Allow.Add(Next()); break;
                case "--cwd": o.WorkingDirectory = Next(); break;
                case "--no-mcp": o.NoMcp = true; break;
                case "sessions" when o.Command is null: o.Command = "sessions"; break;
                case "swarm" when o.Command is null: o.Command = "swarm"; break;
                case "jev" when o.Command is null: o.Command = "jev"; break;
                case "pc" when o.Command is null: o.Command = "pc"; break;
                case "sandbox" when o.Command is null: o.Command = "sandbox"; break;
                case "--text": o.PcTexts.Add(Next()); break;
                case "--hotkeys": o.PcHotkeys = Next(); break;
                case "--launch": o.PcLaunch = true; break;
                case "--keep": o.PcKeep = true; break;
                case "--steps": o.PcSteps = int.Parse(Next(), System.Globalization.CultureInfo.InvariantCulture); break;
                case "--workers": o.Workers = int.Parse(Next(), System.Globalization.CultureInfo.InvariantCulture); break;
                case "--max-tokens": o.MaxTokens = long.Parse(Next(), System.Globalization.CultureInfo.InvariantCulture); break;
                case "--max-minutes": o.MaxMinutes = int.Parse(Next(), System.Globalization.CultureInfo.InvariantCulture); break;
                case "--concurrency": o.Concurrency = int.Parse(Next(), System.Globalization.CultureInfo.InvariantCulture); break;
                case not null when o.Command is "swarm" or "jev" or "pc" or "sandbox" && !a.StartsWith('-'): o.Positional.Add(a); break;
                default: throw new ArgumentException($"Unknown argument '{a}'. Run 'kairo --help'.");
            }
        }
        return o;
    }
}
