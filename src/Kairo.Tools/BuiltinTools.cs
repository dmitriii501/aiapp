using Kairo.Core.Tools;

namespace Kairo.Tools;

public static class BuiltinTools
{
    public static IReadOnlyList<ITool> All() =>
        [new ReadTool(), new WriteTool(), new EditTool(), new GlobTool(), new GrepTool(), new ShellTool(), new TodoWriteTool(), new ExitPlanModeTool()];
}
