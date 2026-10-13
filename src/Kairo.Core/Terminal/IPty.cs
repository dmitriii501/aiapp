namespace Kairo.Core.Terminal;

/// <summary>What to start in a pseudo-terminal. The host decides the program; the UI never supplies a command.</summary>
public sealed record PtyStartInfo(string CommandLine, string WorkingDirectory, int Columns, int Rows);

/// <summary>A running process attached to a pseudo-terminal.</summary>
public interface IPty : IDisposable
{
    /// <summary>Sends keystrokes (UTF-8) to the process.</summary>
    void Write(ReadOnlySpan<byte> data);

    void Resize(int columns, int rows);
}

/// <summary>
/// Starts pseudo-terminals. The callbacks are passed in up front so no output is lost between start and subscribe.
/// <paramref name="onOutput"/> runs on a background thread; <paramref name="onExit"/> runs once, after the last output.
/// </summary>
public interface IPtyFactory
{
    IPty Start(PtyStartInfo info, Action<ReadOnlyMemory<byte>> onOutput, Action<int> onExit);
}
