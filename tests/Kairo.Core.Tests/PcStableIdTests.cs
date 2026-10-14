using System.Text.Json;
using Kairo.Core.Config;
using Kairo.Core.Tests.Fakes;
using Kairo.Core.Tools;
using Kairo.PcControl;
using static Kairo.Core.Tests.Fakes.FakeDesktop;

namespace Kairo.Core.Tests;

public sealed class ObservationChangesTests
{
    private static Observation Look(string title, params UiElement[] elements) => new(1, new WindowInfo(1, 1, "telegram.exe", title, true, false), elements, false);

    [Fact]
    public void New_changed_and_gone_elements_are_listed_by_id()
    {
        var before = Look("t", El("e1", "edit", "Message", UiCapability.SetValue, value: ""), El("e2", "button", "Send", UiCapability.Invoke), El("e3", "text", "old"));
        var after = Look("t", El("e1", "edit", "Message", UiCapability.SetValue, value: "hi"), El("e2", "button", "Send", UiCapability.Invoke), El("e7", "text", "new"));

        var changes = Observation.Changes(before, after)!;

        Assert.Contains("+ [e7] text \"new\"", changes);
        Assert.Contains("~ [e1] edit \"Message\"", changes);
        Assert.Contains("= \"hi\"", changes);
        Assert.Contains("- [e3] text \"old\"", changes);
        Assert.DoesNotContain("e2", changes);
    }

    [Fact]
    public void The_same_screen_has_no_changes_and_a_new_title_alone_is_one()
    {
        var a = Look("t", El("e1", "button", "Send", UiCapability.Invoke));

        Assert.Null(Observation.Changes(a, Look("t", El("e1", "button", "Send", UiCapability.Invoke))));
        Assert.Contains("window title: \"u\"", Observation.Changes(a, Look("u", El("e1", "button", "Send", UiCapability.Invoke))));
    }
}

public sealed class PcBigWindowTests
{
    private readonly FakeDesktop _desktop = new();

    private IReadOnlyList<ITool> Tools()
    {
        var dir = Directory.CreateTempSubdirectory("kairo-big-").FullName;
        File.WriteAllText(Path.Combine(dir, "config.toml"), "[pc]\nenabled = true\napps = [\"notepad.exe\"]");
        return PcTools.For(KairoConfig.Load(dir, dir, includeProject: false), () => null, _desktop);
    }

    private static ITool Tool(IReadOnlyList<ITool> tools, string name) => tools.Single(t => t.Spec.Name == name);

    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);

    private ToolContext Ctx => new(Path.GetTempPath());

    private void BigWindow(int count)
    {
        _desktop.Elements = [.. Enumerable.Range(1, count).Select(i => El($"e{i}", "list item", $"Message {i}"))];
        _desktop.Elements.Add(El("e500", "edit", "Message input", UiCapability.SetValue));
        _desktop.Elements.Add(El("e501", "button", "Send", UiCapability.Invoke));
    }

    [Fact]
    public async Task After_an_action_a_big_window_comes_back_as_changes_only()
    {
        BigWindow(200);
        var tools = Tools();
        var first = await Tool(tools, "PcObserve").InvokeAsync(Json(new { }), Ctx, default);
        Assert.Contains("Message 150", first.Content); // the first look is whole
        _desktop.OnPerform = _ =>
        {
            _desktop.Elements.Add(El("e600", "list item", "Message sent by me"));
            return ActionOutcome.Success();
        };

        var result = await Tool(tools, "PcAct").InvokeAsync(Json(new { action = "invoke", element = "e501" }), Ctx, default);

        Assert.False(result.IsError, result.Content);
        Assert.Contains("+ [e600] list item \"Message sent by me\"", result.Content);
        Assert.DoesNotContain("Message 150", result.Content);
        Assert.Contains("ids from your last look still work", result.Content);
    }

    [Fact]
    public async Task Without_an_earlier_look_the_first_result_is_whole()
    {
        BigWindow(200);

        var result = await Tool(Tools(), "PcAct").InvokeAsync(Json(new { action = "invoke", element = "e501" }), Ctx, default);

        Assert.Contains("Message 150", result.Content);
    }

    [Fact]
    public async Task A_small_window_always_comes_back_whole()
    {
        BigWindow(10);
        var tools = Tools();
        await Tool(tools, "PcObserve").InvokeAsync(Json(new { }), Ctx, default);

        var result = await Tool(tools, "PcAct").InvokeAsync(Json(new { action = "invoke", element = "e501" }), Ctx, default);

        Assert.Contains("Message 5", result.Content);
    }

    [Fact]
    public async Task A_window_that_changed_a_lot_comes_back_whole()
    {
        BigWindow(200);
        var tools = Tools();
        await Tool(tools, "PcObserve").InvokeAsync(Json(new { }), Ctx, default);
        _desktop.OnPerform = _ =>
        {
            _desktop.Elements = [.. Enumerable.Range(1000, 150).Select(i => El($"e{i}", "list item", $"Other {i}")), El("e501", "button", "Send", UiCapability.Invoke)];
            return ActionOutcome.Success();
        };

        var result = await Tool(tools, "PcAct").InvokeAsync(Json(new { action = "invoke", element = "e501" }), Ctx, default);

        Assert.Contains("Other 1100", result.Content);
        Assert.DoesNotContain("What changed since that look", result.Content);
    }

    [Fact]
    public async Task Several_actions_in_a_row_need_no_look_in_between()
    {
        _desktop.Elements = [El("e1", "edit", "Message", UiCapability.SetValue), El("e2", "button", "Send", UiCapability.Invoke)];
        var tools = Tools();
        await Tool(tools, "PcObserve").InvokeAsync(Json(new { }), Ctx, default);

        var typed = await Tool(tools, "PcAct").InvokeAsync(Json(new { action = "set_value", element = "e1", text = "hi" }), Ctx, default);
        var sent = await Tool(tools, "PcAct").InvokeAsync(Json(new { action = "invoke", element = "e2" }), Ctx, default);

        Assert.False(typed.IsError, typed.Content);
        Assert.False(sent.IsError, sent.Content);
        Assert.Equal([DesktopActionKind.SetValue, DesktopActionKind.Invoke], _desktop.Performed.Select(a => a.Kind));
    }
}
