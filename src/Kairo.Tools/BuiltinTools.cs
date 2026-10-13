using Kairo.Core.Tools;
using Kairo.Tools.Web;

namespace Kairo.Tools;

public static class BuiltinTools
{
    /// <param name="web">Where WebSearch gets results, read at each call so changes in settings apply at once. Without it, WebSearch reports that it is not set up.</param>
    /// <param name="webHandler">Replaces the HTTP stack of both web tools (tests).</param>
    public static IReadOnlyList<ITool> All(Func<WebSearchSettings>? web = null, HttpMessageHandler? webHandler = null) =>
    [
        new ReadTool(), new WriteTool(), new EditTool(), new GlobTool(), new GrepTool(), new ShellTool(), new TodoWriteTool(), new ExitPlanModeTool(), new TaskTool(),
        new WebFetchTool(webHandler), new WebSearchTool(web ?? (() => new WebSearchSettings()), webHandler),
    ];
}
