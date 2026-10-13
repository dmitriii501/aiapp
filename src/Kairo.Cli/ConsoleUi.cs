using Kairo.Core.Agents;
using Kairo.Core.Models;

namespace Kairo.Cli;

/// <summary>Renders agent events in the terminal and asks for permissions.</summary>
internal sealed class ConsoleUi(bool interactive) : IPermissionPrompt
{
    private readonly Lock _gate = new();
    private bool _midLine;
    private long _inputTokens, _outputTokens, _cacheRead;

    public void Render(AgentEvent ev)
    {
        lock (_gate)
        {
            switch (ev)
            {
                case AgentTextDelta d:
                    Console.Write(d.Text);
                    _midLine = !d.Text.EndsWith('\n');
                    break;

                case ToolCallStarted s:
                    EndLine();
                    Write(ConsoleColor.Cyan, "● ");
                    Console.WriteLine(s.Summary);
                    break;

                case ToolCallFinished f:
                    var first = FirstLine(f.Result.Content);
                    Write(ConsoleColor.DarkGray, "  ⎿ ");
                    Write(f.Denied ? ConsoleColor.Yellow : f.Result.IsError ? ConsoleColor.Red : ConsoleColor.DarkGray, first);
                    Console.WriteLine();
                    break;

                case ModelCallCompleted m:
                    _inputTokens += m.Usage.InputTokens ?? 0;
                    _outputTokens += m.Usage.OutputTokens ?? 0;
                    _cacheRead += m.Usage.CacheReadTokens ?? 0;
                    break;

                case AgentFinished f:
                    EndLine();
                    if (f.Reason is not ("end_turn" or "stop" or "tool_use"))
                        Write(ConsoleColor.Yellow, $"[stopped: {f.Reason}]\n");
                    break;
            }
        }
    }

    public void PrintUsage(string model)
    {
        var cache = _cacheRead > 0 ? $" · cache read {_cacheRead:N0}" : "";
        Write(ConsoleColor.DarkGray, $"[{model} · in {_inputTokens:N0} · out {_outputTokens:N0}{cache}]\n");
    }

    public Task<PermissionAnswer> AskAsync(PermissionRequest request, CancellationToken ct)
    {
        lock (_gate)
        {
            if (!interactive)
            {
                Write(ConsoleColor.Yellow, $"  ⎿ needs approval ({request.Reason}); denied in non-interactive mode. Use --allow \"{request.Tool}(...)\".\n");
                return Task.FromResult(PermissionAnswer.Deny);
            }

            Write(ConsoleColor.Yellow, $"  Allow {request.Tool}? ({request.Reason})  [y] yes  [a] always this session  [n] no: ");
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var key = Console.ReadKey(intercept: true);
                var answer = char.ToLowerInvariant(key.KeyChar) switch
                {
                    'y' or 'д' or '\r' => PermissionAnswer.Allow,
                    'a' or 'ф' => PermissionAnswer.AllowAlways,
                    'n' or 'т' or '\u001b' => PermissionAnswer.Deny,
                    _ => (PermissionAnswer?)null,
                };
                if (answer is { } a)
                {
                    Console.WriteLine(a switch { PermissionAnswer.Allow => "yes", PermissionAnswer.AllowAlways => "always", _ => "no" });
                    return Task.FromResult(a);
                }
            }
        }
    }

    public static void Write(ConsoleColor color, string text)
    {
        var previous = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.Write(text);
        Console.ForegroundColor = previous;
    }

    private void EndLine()
    {
        if (_midLine)
            Console.WriteLine();
        _midLine = false;
    }

    private static string FirstLine(string text)
    {
        var line = text.Split('\n', 2)[0].TrimEnd('\r');
        var lines = text.Count(c => c == '\n') + 1;
        if (line.Length > 140)
            line = line[..140] + "…";
        return lines > 1 ? $"{line}  (+{lines - 1} lines)" : line;
    }
}
