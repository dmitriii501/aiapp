using System.Text;

namespace Kairo.Core.Tools;

/// <summary>
/// Line diff in unified format without file headers: <c>@@ -a,b +c,d @@</c> hunks of ' ', '-' and '+' lines.
/// Used to show the user what Write and Edit change.
/// </summary>
public static class UnifiedDiff
{
    public const int MaxLines = 400;

    /// <summary>Above this many line pairs the LCS table is skipped and the changed region is shown as replaced.</summary>
    private const long MaxTableCells = 4_000_000;

    /// <summary>Empty when the texts have the same lines. Line endings are ignored.</summary>
    public static string Create(string oldText, string newText, int context = 3, int maxLines = MaxLines)
    {
        var ops = Diff(SplitLines(oldText), SplitLines(newText));

        var changed = new List<int>();
        for (var n = 0; n < ops.Count; n++)
            if (ops[n].Kind != ' ')
                changed.Add(n);
        if (changed.Count == 0)
            return "";

        var sb = new StringBuilder();
        var emitted = 0;
        var c = 0;
        while (c < changed.Count)
        {
            // A hunk spans changes separated by at most 2*context unchanged lines.
            var last = c;
            while (last + 1 < changed.Count && changed[last + 1] - changed[last] - 1 <= 2 * context)
                last++;

            var start = Math.Max(0, changed[c] - context);
            var stop = Math.Min(ops.Count, changed[last] + context + 1);
            c = last + 1;

            int oldCount = 0, newCount = 0;
            for (var k = start; k < stop; k++)
            {
                if (ops[k].Kind != '+') oldCount++;
                if (ops[k].Kind != '-') newCount++;
            }

            if (emitted + 1 >= maxLines)
                return sb.Append("…").ToString();
            sb.Append($"@@ -{ops[start].OldLine},{oldCount} +{ops[start].NewLine},{newCount} @@").Append('\n');
            emitted++;
            for (var k = start; k < stop; k++)
            {
                if (emitted >= maxLines)
                    return sb.Append("…").ToString();
                sb.Append(ops[k].Kind).Append(ops[k].Text).Append('\n');
                emitted++;
            }
        }
        return sb.ToString().TrimEnd('\n');
    }

    private readonly record struct Op(char Kind, string Text, int OldLine, int NewLine);

    private static string[] SplitLines(string text)
    {
        if (text.Length == 0)
            return [];
        var lines = text.Replace("\r\n", "\n").Split('\n');
        // A trailing newline ends the last line; it does not start another one.
        return lines[^1].Length == 0 ? lines[..^1] : lines;
    }

    private static List<Op> Diff(string[] a, string[] b)
    {
        var prefix = 0;
        while (prefix < a.Length && prefix < b.Length && a[prefix] == b[prefix])
            prefix++;
        var suffix = 0;
        while (suffix < a.Length - prefix && suffix < b.Length - prefix && a[^(suffix + 1)] == b[^(suffix + 1)])
            suffix++;

        var ops = new List<Op>(a.Length + b.Length);
        for (var i = 0; i < prefix; i++)
            ops.Add(new Op(' ', a[i], i + 1, i + 1));

        var midA = a[prefix..(a.Length - suffix)];
        var midB = b[prefix..(b.Length - suffix)];
        var oldLine = prefix + 1;
        var newLine = prefix + 1;

        void Emit(char kind, string text)
        {
            ops.Add(new Op(kind, text, oldLine, newLine));
            if (kind != '+') oldLine++;
            if (kind != '-') newLine++;
        }

        if ((long)midA.Length * midB.Length > MaxTableCells)
        {
            foreach (var line in midA) Emit('-', line);
            foreach (var line in midB) Emit('+', line);
        }
        else
        {
            // Longest common subsequence of the changed region.
            var table = new int[midA.Length + 1, midB.Length + 1];
            for (var i = midA.Length - 1; i >= 0; i--)
                for (var j = midB.Length - 1; j >= 0; j--)
                    table[i, j] = midA[i] == midB[j] ? table[i + 1, j + 1] + 1 : Math.Max(table[i + 1, j], table[i, j + 1]);

            int x = 0, y = 0;
            while (x < midA.Length && y < midB.Length)
            {
                if (midA[x] == midB[y]) { Emit(' ', midA[x]); x++; y++; }
                else if (table[x + 1, y] >= table[x, y + 1]) { Emit('-', midA[x]); x++; }
                else { Emit('+', midB[y]); y++; }
            }
            for (; x < midA.Length; x++) Emit('-', midA[x]);
            for (; y < midB.Length; y++) Emit('+', midB[y]);
        }

        for (var i = 0; i < suffix; i++)
            Emit(' ', a[a.Length - suffix + i]);
        return ops;
    }
}
