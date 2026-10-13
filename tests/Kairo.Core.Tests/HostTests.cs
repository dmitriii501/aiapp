using System.Collections.Concurrent;
using Kairo.Core.Config;
using Kairo.Core.Models;
using Kairo.Core.Rpc;
using Kairo.Core.Secrets;
using Kairo.Core.Sessions;
using Kairo.Core.Tests.Fakes;
using Kairo.Host;
using Kairo.Protocol;
using static Kairo.Core.Tests.Fakes.ScriptedProvider;

namespace Kairo.Core.Tests;

public class HostTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly string _project;
    private readonly SessionStore _store;
    private readonly SettingsService _settings;
    private readonly ConcurrentQueue<AgentEventDto> _events = new();
    private ScriptedProvider _model = new();

    public HostTests()
    {
        _project = Path.Combine(_dir.Path, "project");
        Directory.CreateDirectory(_project);
        _store = new SessionStore(Path.Combine(_dir.Path, "data.db"));
        _settings = new SettingsService(Path.Combine(_dir.Path, "user"), new InMemorySecretStore());
    }

    public void Dispose()
    {
        _store.Dispose();
        _dir.Dispose();
    }

    private AgentHost CreateHost(Func<KairoConfig, IModelProvider>? provider = null) =>
        new(_store, _settings,
            notify: ev => { _events.Enqueue(ev); return Task.CompletedTask; },
            sessionsChanged: () => Task.CompletedTask,
            createProvider: provider ?? (_ => _model));

    private string NewSession(AgentHost host)
    {
        _store.TouchWorkspace(_project);
        return host.Create(_project).Id;
    }

    private async Task<AgentEventDto> WaitFor(string type, int timeoutMs = 10_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (_events.FirstOrDefault(e => e.Type == type) is { } found)
                return found;
            await Task.Delay(10);
        }
        throw new TimeoutException($"No '{type}' event. Got: {string.Join(", ", _events.Select(e => e.Type))}");
    }

    [Fact]
    public void Sessions_only_start_in_folders_the_user_picked()
    {
        var host = CreateHost();

        var ex = Assert.Throws<RpcException>(() => host.Create(@"C:\"));
        Assert.Contains("недоступна", ex.Message);

        _store.TouchWorkspace(_project);
        Assert.Equal(AgentHost.NewSessionTitle, host.Create(_project).Title);
    }

    [Fact]
    public async Task A_turn_streams_events_and_titles_the_session()
    {
        _model = new ScriptedProvider(Say("Готово."));
        var host = CreateHost();
        var id = NewSession(host);

        host.Send(id, "Проверь сборку проекта");
        var finished = await WaitFor("turn_finished");

        Assert.Equal("end_turn", finished.Reason);
        Assert.Equal(["turn_started", "text", "turn_finished"], _events.Select(e => e.Type));
        var detail = host.Open(id);
        Assert.Equal("Проверь сборку проекта", detail.Session.Title);
        Assert.Equal(["user", "assistant"], detail.Items.Select(i => i.Kind));
        Assert.False(detail.Session.Running);
    }

    [Fact]
    public async Task Permission_requests_wait_for_the_user()
    {
        _model = new ScriptedProvider(Call("c1", "Write", """{"file_path":"out.txt","content":"hi"}"""), Say("Записал."));
        var host = CreateHost();
        var id = NewSession(host);

        host.Send(id, "создай файл");
        var ask = (await WaitFor("permission")).Permission!;

        Assert.Equal("write", ask.Risk);
        Assert.Equal("out.txt", ask.Subject);
        Assert.Equal("@@ -1,0 +1,1 @@\n+hi", ask.Diff);
        Assert.Single(host.Open(id).PendingPermissions);
        Assert.Throws<RpcException>(() => host.Send(id, "ещё"));

        host.RespondPermission(ask.RequestId, "allow");
        await WaitFor("turn_finished");

        Assert.Equal("hi", File.ReadAllText(Path.Combine(_project, "out.txt")));
        Assert.Contains(_events, e => e.Type == "permission_resolved" && e.RequestId == ask.RequestId);
        var tool = host.Open(id).Items.Single(i => i.Kind == "tool");
        Assert.Equal(("Write", "done"), (tool.Tool, tool.Status));
        Assert.Equal(ask.Diff, tool.Diff);
        Assert.Throws<RpcException>(() => host.RespondPermission(ask.RequestId, "allow"));
    }

    [Fact]
    public async Task Cancelling_while_waiting_for_permission_ends_the_turn_cleanly()
    {
        _model = new ScriptedProvider(Call("c1", "Shell", """{"command":"echo hi"}"""));
        var host = CreateHost();
        var id = NewSession(host);

        host.Send(id, "run");
        await WaitFor("permission");
        host.Cancel(id);
        var finished = await WaitFor("turn_finished");

        Assert.Equal("cancelled", finished.Reason);
        Assert.Empty(host.Open(id).PendingPermissions);
        var stored = _store.LoadMessages(id);
        Assert.Contains("Interrupted", stored[^1].Parts.OfType<ToolResultPart>().Single().Content);
    }

    [Fact]
    public async Task Plan_mode_set_from_the_ui_applies_to_the_next_call()
    {
        _model = new ScriptedProvider(Call("c1", "Write", """{"file_path":"x.txt","content":"x"}"""), Say("ok"));
        var host = CreateHost();
        var id = NewSession(host);

        host.SetMode(id, "plan");
        host.Send(id, "write");
        await WaitFor("turn_finished");

        Assert.DoesNotContain(_events, e => e.Type == "permission");
        Assert.Equal("denied", _events.Single(e => e.Type == "tool_finished").Status);
        Assert.Equal("plan", host.Open(id).Mode);
    }

    [Fact]
    public void Missing_key_is_reported_in_plain_words()
    {
        var host = CreateHost(provider: c => ProviderFactory.Create(c, _settings.Secrets));
        var id = NewSession(host);

        using var _ = new EnvVar("ANTHROPIC_API_KEY", null);
        var ex = Assert.Throws<RpcException>(() => host.Send(id, "hi"));

        Assert.Equal("Не задан API-ключ. Добавьте его в настройках.", ex.Message);
    }

    [Fact]
    public void Settings_round_trip_through_config_and_secret_store()
    {
        using var _ = new EnvVar("KAIRO_API_KEY", null);

        var initial = _settings.Get();
        var updated = _settings.Update(new SettingsUpdate(Provider: "openai-compatible", Model: "qwen3", BaseUrl: "http://localhost:11434/v1", Mode: "acceptEdits", Effort: "high"));
        var withKey = _settings.SetSecret(new SecretSetParams("openai-compatible", "sk-test"));
        var cleared = _settings.Update(new SettingsUpdate(Effort: "default"));

        Assert.Equal(("anthropic", "claude-opus-5-5"), (initial.Provider, initial.Model));
        Assert.Equal(("openai-compatible", "qwen3", "http://localhost:11434/v1", "acceptEdits", "high"),
            (updated.Provider, updated.Model, updated.BaseUrl, updated.Mode, updated.Effort));
        Assert.True(updated.Ready); // local endpoints work without a key
        Assert.True(withKey.Providers.Single(p => p.Id == "openai-compatible").HasKey);
        Assert.Null(cleared.Effort);
        Assert.Throws<RpcException>(() => _settings.Update(new SettingsUpdate(Provider: "skynet")));
        Assert.Throws<RpcException>(() => _settings.Update(new SettingsUpdate(BaseUrl: "not a url")));
    }

    [Fact]
    public void Each_provider_keeps_its_own_base_url()
    {
        _settings.Update(new SettingsUpdate(Provider: "anthropic", BaseUrl: "https://gateway.example.com"));
        _settings.Update(new SettingsUpdate(Provider: "openai-compatible", BaseUrl: "http://localhost:11434/v1", Model: "qwen3"));

        var config = _settings.LoadUser();
        Assert.Equal("https://gateway.example.com", config.AnthropicBaseUrl);
        Assert.Equal("http://localhost:11434/v1", config.BaseUrl);
        Assert.Equal("http://localhost:11434/v1", _settings.Get().BaseUrl);

        _settings.Update(new SettingsUpdate(Provider: "anthropic"));
        Assert.Equal("https://gateway.example.com", _settings.Get().BaseUrl);
    }

    [Theory]
    [InlineData(401, "anthropic", "Сервер отклонил API-ключ (401)")]
    [InlineData(404, "openai-compatible", "заканчивается на /v1")]
    [InlineData(502, "openai-compatible", "ответил ошибкой 502")]
    [InlineData(429, "anthropic", "лимит запросов")]
    public void Provider_errors_become_actionable_messages(int status, string provider, string expected)
    {
        var text = ErrorText.Describe(new ModelProviderException("boom", status), provider);

        Assert.Contains(expected, text);
    }

    private sealed class EnvVar : IDisposable
    {
        private readonly string _name;
        private readonly string? _old;

        public EnvVar(string name, string? value)
        {
            _name = name;
            _old = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose() => Environment.SetEnvironmentVariable(_name, _old);
    }
}
