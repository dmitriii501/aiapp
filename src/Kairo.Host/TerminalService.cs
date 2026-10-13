using System.Collections.Concurrent;
using System.Text;
using Kairo.Core.Rpc;
using Kairo.Core.Sessions;
using Kairo.Core.Terminal;
using Kairo.Protocol;

namespace Kairo.Host;

/// <summary>
/// The user's terminals. A terminal is a shell the user types into, separate from the agent's Shell tool and its
/// permission rules. The host chooses the program and checks the folder; the UI only sends keystrokes and sizes.
/// </summary>
public sealed class TerminalService(
    IPtyFactory ptys,
    SessionStore store,
    string shellCommandLine,
    string shellTitle,
    Func<TerminalOutput, Task> notifyOutput,
    Func<TerminalExited, Task> notifyExited) : IDisposable
{
    public const int MaxTerminals = 8;
    public const int MaxInputBytes = 64 * 1024;

    /// <summary>Output is sent in batches this far apart, so a command that prints a lot does not flood the bridge.</summary>
    private static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(8);

    private readonly ConcurrentDictionary<string, Terminal> _terminals = new();

    private sealed class Terminal(IPty? pty = null)
    {
        public IPty? Pty { get; set; } = pty;
        public Decoder Decoder { get; } = new UTF8Encoding(false).GetDecoder();
        public StringBuilder Pending { get; } = new();
        public bool FlushScheduled { get; set; }
        public Lock Gate { get; } = new();

        /// <summary>Batches are sent one at a time, so output reaches the UI in order.</summary>
        public SemaphoreSlim Send { get; } = new(1, 1);
    }

    public TerminalInfo Open(string workingDirectory, int columns, int rows)
    {
        if (_terminals.Count >= MaxTerminals)
            throw new RpcException(RpcErrorCodes.InvalidRequest, $"Открыто слишком много терминалов (максимум {MaxTerminals}). Закройте ненужные.");
        if (!Directory.Exists(workingDirectory) || !store.IsKnownWorkspace(workingDirectory))
            throw new RpcException(RpcErrorCodes.InvalidParams, "Эта папка недоступна. Выберите её через «Новая сессия» → «Другая папка».");

        var id = Guid.NewGuid().ToString("N");
        var terminal = new Terminal();
        _terminals[id] = terminal;
        try
        {
            terminal.Pty = ptys.Start(
                new PtyStartInfo(shellCommandLine, workingDirectory, Clamp(columns, 2, 500), Clamp(rows, 1, 200)),
                bytes => OnOutput(id, terminal, bytes.Span),
                code => OnExit(id, terminal, code));
        }
        catch (Exception ex)
        {
            _terminals.TryRemove(id, out _);
            throw new RpcException(RpcErrorCodes.InternalError, $"Не удалось запустить терминал: {ex.Message}");
        }

        var folder = Path.GetFileName(workingDirectory.TrimEnd(Path.DirectorySeparatorChar));
        return new TerminalInfo(id, folder.Length > 0 ? $"{shellTitle} · {folder}" : shellTitle);
    }

    public void Input(string terminalId, string data)
    {
        var bytes = Encoding.UTF8.GetBytes(data);
        if (bytes.Length > MaxInputBytes)
            throw new RpcException(RpcErrorCodes.InvalidParams, "Слишком большой фрагмент для вставки в терминал.");
        Get(terminalId).Pty?.Write(bytes);
    }

    public void Resize(string terminalId, int columns, int rows) =>
        Get(terminalId).Pty?.Resize(Clamp(columns, 2, 500), Clamp(rows, 1, 200));

    public void Close(string terminalId)
    {
        if (_terminals.TryRemove(terminalId, out var terminal))
            terminal.Pty?.Dispose();
    }

    public void Dispose()
    {
        foreach (var id in _terminals.Keys.ToList())
            Close(id);
    }

    private Terminal Get(string terminalId) =>
        _terminals.TryGetValue(terminalId, out var terminal) ? terminal : throw new RpcException(RpcErrorCodes.InvalidParams, "Терминал уже закрыт.");

    private void OnOutput(string id, Terminal terminal, ReadOnlySpan<byte> bytes)
    {
        lock (terminal.Gate)
        {
            var chars = new char[Encoding.UTF8.GetMaxCharCount(bytes.Length)];
            var count = terminal.Decoder.GetChars(bytes, chars, flush: false);
            terminal.Pending.Append(chars, 0, count);
            if (terminal.FlushScheduled)
                return;
            terminal.FlushScheduled = true;
        }
        _ = Task.Delay(FlushInterval).ContinueWith(_ => Flush(id, terminal), TaskScheduler.Default);
    }

    private async Task Flush(string id, Terminal terminal)
    {
        await terminal.Send.WaitAsync().ConfigureAwait(false);
        try
        {
            string text;
            lock (terminal.Gate)
            {
                text = terminal.Pending.ToString();
                terminal.Pending.Clear();
                terminal.FlushScheduled = false;
            }
            if (text.Length > 0)
                await notifyOutput(new TerminalOutput(id, text)).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The window is gone; there is nobody to show the output to.
        }
        finally
        {
            terminal.Send.Release();
        }
    }

    private async void OnExit(string id, Terminal terminal, int code)
    {
        await Flush(id, terminal).ConfigureAwait(false);
        _terminals.TryRemove(id, out _);
        try
        {
            await notifyExited(new TerminalExited(id, code)).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The window is gone.
        }
    }

    private static int Clamp(int value, int min, int max) => Math.Min(max, Math.Max(min, value));
}
