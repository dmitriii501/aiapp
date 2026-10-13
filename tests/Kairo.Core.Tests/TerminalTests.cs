using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Kairo.Core.Rpc;
using Kairo.Core.Sessions;
using Kairo.Core.Terminal;
using Kairo.Core.Tests.Fakes;
using Kairo.Host;
using Kairo.Protocol;
using Kairo.Windows;

namespace Kairo.Core.Tests;

public class TerminalServiceTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly SessionStore _store;
    private readonly string _project;
    private readonly FakePtyFactory _ptys = new();
    private readonly ConcurrentQueue<TerminalOutput> _output = new();
    private readonly ConcurrentQueue<TerminalExited> _exited = new();
    private readonly TerminalService _service;

    public TerminalServiceTests()
    {
        _project = Path.Combine(_dir.Path, "project");
        Directory.CreateDirectory(_project);
        _store = new SessionStore(Path.Combine(_dir.Path, "data.db"));
        _store.TouchWorkspace(_project);
        _service = new TerminalService(_ptys, _store, "pwsh -NoLogo", "PowerShell",
            o => { _output.Enqueue(o); return Task.CompletedTask; },
            e => { _exited.Enqueue(e); return Task.CompletedTask; });
    }

    public void Dispose()
    {
        _service.Dispose();
        _store.Dispose();
        _dir.Dispose();
    }

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the terminal.");
            await Task.Delay(5);
        }
    }

    [Fact]
    public void Terminals_start_only_in_known_folders_with_the_hosts_shell()
    {
        var info = _service.Open(_project, 120, 30);

        Assert.Equal("PowerShell · project", info.Title);
        var started = Assert.Single(_ptys.Started);
        Assert.Equal(("pwsh -NoLogo", _project, 120, 30), (started.Info.CommandLine, started.Info.WorkingDirectory, started.Info.Columns, started.Info.Rows));

        var ex = Assert.Throws<RpcException>(() => _service.Open(@"C:\", 80, 24));
        Assert.Contains("недоступна", ex.Message);
        Assert.Throws<RpcException>(() => _service.Open(Path.Combine(_dir.Path, "missing"), 80, 24));
    }

    [Fact]
    public async Task Output_is_decoded_batched_and_kept_in_order_even_when_a_character_is_split()
    {
        var info = _service.Open(_project, 80, 24);
        var pty = _ptys.Started[0];
        var bytes = Encoding.UTF8.GetBytes("привет ");

        pty.Emit(bytes[..3]); // splits the second Cyrillic letter in half
        pty.Emit(bytes[3..]);
        pty.Emit(Encoding.UTF8.GetBytes("мир"));

        await Until(() => string.Concat(_output.Select(o => o.Data)) == "привет мир");
        Assert.All(_output, o => Assert.Equal(info.TerminalId, o.TerminalId));
    }

    [Fact]
    public void Input_and_resize_reach_the_shell_and_are_bounded()
    {
        var info = _service.Open(_project, 80, 24);
        var pty = _ptys.Started[0];

        _service.Input(info.TerminalId, "dir\r");
        _service.Resize(info.TerminalId, 100, 40);
        _service.Resize(info.TerminalId, 100000, 0);

        Assert.Equal("dir\r", Encoding.UTF8.GetString(pty.Received.ToArray()));
        Assert.Equal([(100, 40), (500, 1)], pty.Sizes);
        Assert.Throws<RpcException>(() => _service.Input(info.TerminalId, new string('x', TerminalService.MaxInputBytes + 1)));
        Assert.Throws<RpcException>(() => _service.Input("nope", "x"));
    }

    [Fact]
    public async Task Exit_flushes_remaining_output_then_reports_the_code()
    {
        var info = _service.Open(_project, 80, 24);
        var pty = _ptys.Started[0];

        pty.Emit("bye"u8.ToArray());
        pty.Exit(3);

        await Until(() => !_exited.IsEmpty);
        Assert.Equal("bye", string.Concat(_output.Select(o => o.Data)));
        Assert.Equal(new TerminalExited(info.TerminalId, 3), _exited.Single());
        Assert.Throws<RpcException>(() => _service.Input(info.TerminalId, "x")); // already gone
    }

    [Fact]
    public void Closing_stops_the_shell_and_the_number_of_terminals_is_limited()
    {
        var first = _service.Open(_project, 80, 24);
        for (var i = 1; i < TerminalService.MaxTerminals; i++)
            _service.Open(_project, 80, 24);

        Assert.Throws<RpcException>(() => _service.Open(_project, 80, 24));

        _service.Close(first.TerminalId);

        Assert.True(_ptys.Started[0].Disposed);
        _service.Open(_project, 80, 24); // there is room again
    }

    [Fact]
    public void A_shell_that_cannot_start_is_reported_in_plain_words()
    {
        _ptys.FailWith = new InvalidOperationException("no shell");

        var ex = Assert.Throws<RpcException>(() => _service.Open(_project, 80, 24));

        Assert.Contains("Не удалось запустить терминал", ex.Message);
    }

    private sealed class FakePtyFactory : IPtyFactory
    {
        public List<FakePty> Started { get; } = [];
        public Exception? FailWith { get; set; }

        public IPty Start(PtyStartInfo info, Action<ReadOnlyMemory<byte>> onOutput, Action<int> onExit)
        {
            if (FailWith is not null)
                throw FailWith;
            var pty = new FakePty(info, onOutput, onExit);
            Started.Add(pty);
            return pty;
        }
    }

    private sealed class FakePty(PtyStartInfo info, Action<ReadOnlyMemory<byte>> onOutput, Action<int> onExit) : IPty
    {
        public PtyStartInfo Info { get; } = info;
        public List<byte> Received { get; } = [];
        public List<(int, int)> Sizes { get; } = [];
        public bool Disposed { get; private set; }

        public void Emit(byte[] bytes) => onOutput(bytes);

        public void Exit(int code) => onExit(code);

        public void Write(ReadOnlySpan<byte> data) => Received.AddRange(data.ToArray());

        public void Resize(int columns, int rows) => Sizes.Add((columns, rows));

        public void Dispose() => Disposed = true;
    }
}

/// <summary>Runs a real shell in a real pseudo-console. Windows only.</summary>
public class ConPtyTests
{
    private sealed class Capture
    {
        public StringBuilder Text { get; } = new();
        public TaskCompletionSource<int> Exit { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IPty Start(PtyStartInfo info) => new ConPtyFactory().Start(info,
            bytes => { lock (Text) Text.Append(Encoding.UTF8.GetString(bytes.Span)); },
            code => Exit.TrySetResult(code));

        public string Snapshot()
        {
            lock (Text)
                return Text.ToString();
        }

        public async Task WaitForText(string expected)
        {
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (!Snapshot().Contains(expected, StringComparison.Ordinal))
            {
                Assert.True(DateTime.UtcNow < deadline, $"Did not see '{expected}'. Got: {Snapshot()}");
                await Task.Delay(20);
            }
        }
    }

    private static string Cmd => Path.Combine(Environment.SystemDirectory, "cmd.exe");

    [Fact]
    public async Task A_command_runs_and_its_output_and_exit_code_come_back()
    {
        var capture = new Capture();

        using var pty = capture.Start(new PtyStartInfo($"\"{Cmd}\" /c \"echo kairo-ok & exit 7\"", Environment.CurrentDirectory, 80, 24));

        Assert.Equal(7, await capture.Exit.Task.WaitAsync(TimeSpan.FromSeconds(15)));
        Assert.Contains("kairo-ok", capture.Snapshot());
    }

    [Fact]
    public async Task Typed_input_reaches_an_interactive_shell()
    {
        var capture = new Capture();
        using var pty = capture.Start(new PtyStartInfo($"\"{Cmd}\"", Environment.CurrentDirectory, 100, 30));

        pty.Write("echo from-input-123\r"u8);
        await capture.WaitForText("from-input-123");
        pty.Resize(60, 20);
        pty.Write("exit\r"u8);

        Assert.Equal(0, await capture.Exit.Task.WaitAsync(TimeSpan.FromSeconds(15)));
    }

    [Fact]
    public async Task Disposing_kills_the_shell_and_what_it_started()
    {
        var capture = new Capture();
        var pingsBefore = Process.GetProcessesByName("PING").Length;
        // The shell starts a child that would run for a minute.
        var pty = capture.Start(new PtyStartInfo($"\"{Cmd}\" /k \"ping -n 60 127.0.0.1\"", Environment.CurrentDirectory, 80, 24));
        await capture.WaitForText("TTL=");
        Assert.True(Process.GetProcessesByName("PING").Length > pingsBefore, "The child did not start.");

        pty.Dispose();

        await capture.Exit.Task.WaitAsync(TimeSpan.FromSeconds(15)); // throws if the shell did not end after Dispose
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (Process.GetProcessesByName("PING").Length > pingsBefore && DateTime.UtcNow < deadline)
            await Task.Delay(100);
        Assert.True(Process.GetProcessesByName("PING").Length <= pingsBefore, "The child outlived the terminal.");
    }

    [Fact]
    public void An_unknown_program_fails_to_start_with_an_error()
    {
        var capture = new Capture();

        Assert.ThrowsAny<Exception>(() => capture.Start(new PtyStartInfo(@"C:\definitely\missing\program.exe", Environment.CurrentDirectory, 80, 24)));
    }
}
