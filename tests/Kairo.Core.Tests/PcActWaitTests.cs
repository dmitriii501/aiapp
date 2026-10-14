using System.Text.Json;
using Kairo.Core.Config;
using Kairo.Core.Tests.Fakes;
using Kairo.Core.Tools;
using Kairo.PcControl;
using static Kairo.Core.Tests.Fakes.FakeDesktop;

namespace Kairo.Core.Tests;

public sealed class PcActWaitTests
{
    private readonly FakeDesktop _desktop = new();

    private IReadOnlyList<ITool> Tools()
    {
        var dir = Directory.CreateTempSubdirectory("kairo-wait-").FullName;
        File.WriteAllText(Path.Combine(dir, "config.toml"), "[pc]\nenabled = true\napps = [\"notepad.exe\"]");
        return PcTools.For(KairoConfig.Load(dir, dir, includeProject: false), () => null, _desktop);
    }

    private static ITool Tool(IReadOnlyList<ITool> tools, string name) => tools.Single(t => t.Spec.Name == name);

    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);

    private ToolContext Ctx => new(Path.GetTempPath());

    [Fact]
    public async Task Typing_without_an_element_goes_to_the_focused_one()
    {
        _desktop.Elements = [El("e1", "edit", "Message", UiCapability.SetValue)];

        var result = await Tool(Tools(), "PcAct").InvokeAsync(Json(new { action = "type_text", text = "hi" }), Ctx, default);

        Assert.False(result.IsError, result.Content);
        var action = Assert.Single(_desktop.Performed);
        Assert.Equal((DesktopActionKind.TypeFocused, "hi"), (action.Kind, action.Text));
        Assert.Empty(result.Images ?? []); // no picture unless the user turned vision on
    }

    [Fact]
    public async Task An_action_that_closes_the_window_is_a_success_not_a_failure()
    {
        _desktop.Elements = [El("e1", "button", "Close", UiCapability.Invoke)];
        var tools = Tools();
        await Tool(tools, "PcObserve").InvokeAsync(Json(new { }), Ctx, default);
        var closed = false;
        _desktop.OnPerform = _ =>
        {
            closed = true;
            return ActionOutcome.Success("invoked");
        };
        _desktop.OnObserve = _ =>
        {
            if (closed)
                throw new InvalidOperationException("The window is gone.");
        };

        var result = await Tool(tools, "PcAct").InvokeAsync(Json(new { action = "invoke", element = "e1" }), Ctx, default);

        Assert.False(result.IsError, result.Content);
        Assert.Contains("invoked", result.Content);
        Assert.Contains("closed it", result.Content);
    }

    [Fact]
    public void Typing_without_an_element_and_without_text_is_explained()
    {
        var ex = Assert.Throws<ToolInputException>(() => Tool(Tools(), "PcAct").Assess(Json(new { action = "type_text" }), Ctx));

        Assert.Contains("needs text", ex.Message);
    }

    [Fact]
    public async Task A_page_that_loads_late_is_waited_for_instead_of_returning_the_old_screen()
    {
        _desktop.Elements = [El("e1", "edit", "Address", UiCapability.SetValue)];
        var tools = Tools();
        await Tool(tools, "PcObserve").InvokeAsync(Json(new { }), Ctx, default); // the look the action is chosen from
        var acted = -1;
        _desktop.OnPerform = _ =>
        {
            acted = _desktop.Observations;
            return ActionOutcome.Success();
        };
        // The result shows up on the third look after the action.
        _desktop.OnObserve = looks =>
        {
            if (acted >= 0 && looks >= acted + 3)
                _desktop.Elements = [El("e1", "edit", "Address", UiCapability.SetValue), El("e2", "link", "Results")];
        };

        var result = await Tool(tools, "PcAct").InvokeAsync(Json(new { action = "hotkey", text = "enter", wait = 5 }), Ctx, default);

        Assert.Contains("Results", result.Content);
        Assert.DoesNotContain("looks the same", result.Content);
    }

    [Fact]
    public async Task A_screen_that_never_changes_says_so_after_the_wait()
    {
        _desktop.Elements = [El("e1", "edit", "Address", UiCapability.SetValue)];
        var tools = Tools();
        await Tool(tools, "PcObserve").InvokeAsync(Json(new { }), Ctx, default);

        var result = await Tool(tools, "PcAct").InvokeAsync(Json(new { action = "hotkey", text = "enter", wait = 0.3 }), Ctx, default);

        Assert.Contains("looks the same as before, even after waiting", result.Content);
    }

    [Fact]
    public async Task An_action_that_changes_the_screen_does_not_wait()
    {
        _desktop.Elements = [El("e1", "button", "Open", UiCapability.Invoke)];
        var tools = Tools();
        await Tool(tools, "PcObserve").InvokeAsync(Json(new { }), Ctx, default);
        _desktop.OnPerform = _ =>
        {
            _desktop.Elements = [El("e1", "button", "Close", UiCapability.Invoke)];
            return ActionOutcome.Success();
        };
        var clock = System.Diagnostics.Stopwatch.StartNew();

        var result = await Tool(tools, "PcAct").InvokeAsync(Json(new { action = "invoke", element = "e1", look = 1, wait = 10 }), Ctx, default);

        Assert.True(clock.ElapsedMilliseconds < 1000);
        Assert.Contains("Close", result.Content);
    }

    [Fact]
    public async Task A_query_lists_only_the_matching_elements_and_counts_them()
    {
        _desktop.Elements = [El("e1", "edit", "Message input", UiCapability.SetValue), El("e2", "text", "Hello there"), El("e3", "button", "Send", UiCapability.Invoke),
            El("e4", "list item", "Dima: yes")];

        var result = await Tool(Tools(), "PcObserve").InvokeAsync(Json(new { query = "input|send" }), Ctx, default);

        Assert.Contains("[e1] edit \"Message input\"", result.Content);
        Assert.Contains("[e3] button \"Send\"", result.Content);
        Assert.DoesNotContain("Hello there", result.Content);
        Assert.DoesNotContain("Dima", result.Content);
        Assert.Contains("2 of 4 elements match", result.Content);
    }
}
