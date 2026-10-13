using System.Net;
using System.Text;

namespace Kairo.Tools.Web;

/// <summary>
/// A small HTML-to-text converter for the agent: headings, paragraphs, lists, tables, code and links survive as plain text;
/// scripts, styles and markup do not. Not a browser: pages that build their content with JavaScript come out empty.
/// </summary>
public static class HtmlText
{
    private static readonly HashSet<string> Skipped = new(StringComparer.OrdinalIgnoreCase) { "script", "style", "noscript", "svg", "template", "iframe", "canvas" };

    private static readonly HashSet<string> Blocks = new(StringComparer.OrdinalIgnoreCase)
    {
        "p", "div", "section", "article", "header", "footer", "main", "aside", "nav", "ul", "ol", "table", "tr", "blockquote", "form", "fieldset", "figure", "dl", "dt", "dd", "details", "summary", "address",
    };

    /// <summary>
    /// When the page has a main or article element, only that is converted: navigation, cookie banners and footers are noise
    /// for the agent. If that leaves almost nothing, the whole page is converted instead.
    /// </summary>
    public static (string? Title, string Text) Convert(string html, Uri? baseUri = null)
    {
        var whole = ConvertCore(html, baseUri);
        foreach (var tag in new[] { "main", "article" })
        {
            var start = html.IndexOf("<" + tag, StringComparison.OrdinalIgnoreCase);
            var end = html.LastIndexOf("</" + tag, StringComparison.OrdinalIgnoreCase);
            if (start < 0 || end <= start)
                continue;
            var scoped = ConvertCore(html[start..end], baseUri);
            if (scoped.Text.Length >= 300 && scoped.Text.Length * 20 >= whole.Text.Length)
                return (whole.Title, scoped.Text);
        }
        return whole;
    }

    private static (string? Title, string Text) ConvertCore(string html, Uri? baseUri)
    {
        var text = new StringBuilder();
        string? title = null;
        var pre = 0;
        var hrefs = new Stack<string?>();
        var i = 0;

        while (i < html.Length)
        {
            var lt = html.IndexOf('<', i);
            if (lt < 0)
            {
                AppendText(text, html[i..], pre > 0);
                break;
            }
            if (lt > i)
                AppendText(text, html[i..lt], pre > 0);

            if (string.CompareOrdinal(html, lt, "<!--", 0, 4) == 0)
            {
                var end = html.IndexOf("-->", lt + 4, StringComparison.Ordinal);
                i = end < 0 ? html.Length : end + 3;
                continue;
            }

            var gt = FindTagEnd(html, lt + 1);
            if (gt < 0)
                break;
            var tag = html.AsSpan(lt + 1, gt - lt - 1);
            i = gt + 1;
            if (tag.Length == 0 || tag[0] == '!' || tag[0] == '?')
                continue;

            var closing = tag[0] == '/';
            if (closing)
                tag = tag[1..];
            var nameEnd = 0;
            while (nameEnd < tag.Length && !char.IsWhiteSpace(tag[nameEnd]) && tag[nameEnd] != '/')
                nameEnd++;
            var name = tag[..nameEnd].ToString().ToLowerInvariant();
            var attrs = tag[nameEnd..];

            if (!closing && name == "title" && title is null)
            {
                var close = html.IndexOf("</title", i, StringComparison.OrdinalIgnoreCase);
                var raw = close < 0 ? html[i..] : html[i..close];
                title = WebUtility.HtmlDecode(raw).Trim();
                i = close < 0 ? html.Length : close;
                continue;
            }

            if (!closing && Skipped.Contains(name))
            {
                // The body of these elements is not content. Skip to the matching close tag.
                var close = html.IndexOf("</" + name, i, StringComparison.OrdinalIgnoreCase);
                if (close < 0)
                    break;
                var end = html.IndexOf('>', close);
                i = end < 0 ? html.Length : end + 1;
                continue;
            }
            if (Skipped.Contains(name))
                continue;

            switch (name)
            {
                case "br":
                    text.Append('\n');
                    break;
                case "hr":
                    Break(text, 2);
                    text.Append("---");
                    Break(text, 2);
                    break;
                case "h1" or "h2" or "h3" or "h4" or "h5" or "h6":
                    Break(text, 2);
                    if (!closing)
                        text.Append('#', name[1] - '0').Append(' ');
                    else
                        Break(text, 2);
                    break;
                case "li" or "dt":
                    Break(text, 1);
                    if (!closing)
                        text.Append("- ");
                    break;
                case "td" or "th":
                    if (!closing && text.Length > 0 && text[^1] != '\n')
                        text.Append(" | ");
                    break;
                case "pre":
                    if (!closing)
                    {
                        Break(text, 2);
                        text.Append("```\n");
                        pre++;
                    }
                    else if (pre > 0)
                    {
                        pre--;
                        if (text.Length > 0 && text[^1] != '\n')
                            text.Append('\n');
                        text.Append("```");
                        Break(text, 2);
                    }
                    break;
                case "img" when !closing:
                    if (Attribute(attrs, "alt") is { Length: > 0 } alt)
                        text.Append("[image: ").Append(WebUtility.HtmlDecode(alt)).Append(']');
                    break;
                case "a":
                    if (!closing)
                    {
                        hrefs.Push(Attribute(attrs, "href"));
                    }
                    else if (hrefs.Count > 0 && Resolve(hrefs.Pop(), baseUri) is { } url && text.Length > 0 && text[^1] != '\n')
                    {
                        text.Append(" (").Append(url).Append(')');
                    }
                    break;
                default:
                    if (Blocks.Contains(name))
                        Break(text, closing && name is "p" or "ul" or "ol" or "table" ? 2 : 1);
                    break;
            }
        }

        return (title, Tidy(text.ToString()));
    }

    private static void AppendText(StringBuilder sb, string raw, bool pre)
    {
        var decoded = WebUtility.HtmlDecode(raw);
        if (pre)
        {
            sb.Append(decoded);
            return;
        }
        foreach (var ch in decoded)
        {
            if (char.IsWhiteSpace(ch) || ch == ' ')
            {
                if (sb.Length > 0 && sb[^1] is not (' ' or '\n'))
                    sb.Append(' ');
            }
            else if (!char.IsControl(ch))
            {
                sb.Append(ch);
            }
        }
    }

    /// <summary>Makes sure the output ends with at least <paramref name="newlines"/> line breaks (none at the very start).</summary>
    private static void Break(StringBuilder sb, int newlines)
    {
        if (sb.Length == 0)
            return;
        while (sb.Length > 0 && sb[^1] == ' ')
            sb.Length--;
        var have = 0;
        for (var k = sb.Length - 1; k >= 0 && sb[k] == '\n'; k--)
            have++;
        for (; have < newlines; have++)
            sb.Append('\n');
    }

    private static int FindTagEnd(string html, int from)
    {
        char? quote = null;
        for (var k = from; k < html.Length; k++)
        {
            var c = html[k];
            if (quote is { } q)
            {
                if (c == q)
                    quote = null;
            }
            else if (c is '"' or '\'')
            {
                quote = c;
            }
            else if (c == '>')
            {
                return k;
            }
        }
        return -1;
    }

    private static string? Attribute(ReadOnlySpan<char> attrs, string name)
    {
        var s = attrs.ToString();
        var at = 0;
        while (at < s.Length)
        {
            var found = s.IndexOf(name, at, StringComparison.OrdinalIgnoreCase);
            if (found < 0)
                return null;
            at = found + name.Length;
            if (found > 0 && !char.IsWhiteSpace(s[found - 1]))
                continue;
            var k = at;
            while (k < s.Length && char.IsWhiteSpace(s[k]))
                k++;
            if (k >= s.Length || s[k] != '=')
                continue;
            k++;
            while (k < s.Length && char.IsWhiteSpace(s[k]))
                k++;
            if (k >= s.Length)
                return "";
            if (s[k] is '"' or '\'')
            {
                var end = s.IndexOf(s[k], k + 1);
                return end < 0 ? s[(k + 1)..] : s[(k + 1)..end];
            }
            var stop = k;
            while (stop < s.Length && !char.IsWhiteSpace(s[stop]))
                stop++;
            return s[k..stop];
        }
        return null;
    }

    private static string? Resolve(string? href, Uri? baseUri)
    {
        if (string.IsNullOrWhiteSpace(href))
            return null;
        href = WebUtility.HtmlDecode(href.Trim());
        if (href.StartsWith('#') || href.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) || href.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
            return null;
        if (Uri.TryCreate(href, UriKind.Absolute, out var absolute))
            return absolute.Scheme is "http" or "https" ? absolute.ToString() : null;
        return baseUri is not null && Uri.TryCreate(baseUri, href, out var combined) ? combined.ToString() : null;
    }

    private static string Tidy(string text)
    {
        var lines = text.Replace("\r", "", StringComparison.Ordinal).Split('\n').Select(l => l.TrimEnd());
        var sb = new StringBuilder();
        var blank = 0;
        var inCode = false;
        foreach (var line in lines)
        {
            if (line.StartsWith("```", StringComparison.Ordinal))
                inCode = !inCode;
            if (line.Length == 0)
            {
                if ((++blank <= 1 || inCode) && sb.Length > 0)
                    sb.Append('\n');
                continue;
            }
            blank = 0;
            sb.Append(inCode ? line : line.TrimStart(' ')).Append('\n');
        }
        return sb.ToString().Trim();
    }
}
