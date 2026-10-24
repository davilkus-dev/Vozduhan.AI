using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Vozduhan.AI;

/// <summary>
/// Turns model output into Telegram HTML that Telegram will accept: only supported tags survive, everything else is
/// escaped, tags are balanced, and nothing can smuggle attributes or links other than http(s)/tg://user.
/// </summary>
public static partial class TelegramHtml
{
    private static readonly Dictionary<string, string> Canonical = new(StringComparer.OrdinalIgnoreCase)
    {
        ["b"] = "b", ["strong"] = "b", ["i"] = "i", ["em"] = "i", ["u"] = "u", ["ins"] = "u",
        ["s"] = "s", ["strike"] = "s", ["del"] = "s", ["tg-spoiler"] = "tg-spoiler", ["spoiler"] = "tg-spoiler",
        ["code"] = "code", ["pre"] = "pre", ["blockquote"] = "blockquote", ["a"] = "a", ["tg-time"] = "tg-time", ["span"] = "span"
    };

    [GeneratedRegex(@"<(/?)([a-zA-Z][a-zA-Z0-9-]*)((?:\s+[a-zA-Z-]+(?:\s*=\s*(?:""[^""]*""|'[^']*'|[^\s>]+))?)*)\s*/?>")]
    private static partial Regex Tag();
    [GeneratedRegex(@"&(?:lt|gt|amp|quot|#\d{1,7}|#x[0-9a-fA-F]{1,6});")]
    private static partial Regex Entity();
    [GeneratedRegex(@"([a-zA-Z-]+)(?:\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s>]+)))?")]
    private static partial Regex Attr();
    [GeneratedRegex(@"^(r|w?[dD]?[tT]?)$")]
    private static partial Regex TimeFormat();

    /// <summary>
    /// Models slip into Markdown despite instructions. Converts the common forms to Telegram HTML before sanitising:
    /// ```block```, `code`, **bold**, __underline__, ~~strike~~, ||spoiler||, *italic* / _italic_, [text](https://url).
    /// </summary>
    public static string FromMarkdown(string text)
    {
        var kept = new List<string>();
        string Keep(string html) { kept.Add(html); return $"\u0001{kept.Count - 1}\u0001"; }
        static string E(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
        // Code first: its content must not be touched by the other rules.
        text = Regex.Replace(text, @"```(?:[a-zA-Z0-9_+#-]{1,20}\n)?([\s\S]*?)```", m => Keep("<pre>" + E(m.Groups[1].Value.Trim('\n')) + "</pre>"));
        text = Regex.Replace(text, @"(?<!`)`([^`\n]+)`(?!`)", m => Keep("<code>" + E(m.Groups[1].Value) + "</code>"));
        text = Regex.Replace(text, @"\[([^\]\n]{1,200})\]\((https?://[^\s)]+)\)", m => $"<a href=\"{m.Groups[2].Value}\">{m.Groups[1].Value}</a>");
        text = Regex.Replace(text, @"\*\*(?=\S)(.+?)(?<=\S)\*\*", "<b>$1</b>");
        text = Regex.Replace(text, @"(?<![\w_])__(?=\S)(.+?)(?<=\S)__(?![\w_])", "<u>$1</u>");
        text = Regex.Replace(text, @"~~(?=\S)(.+?)(?<=\S)~~", "<s>$1</s>");
        text = Regex.Replace(text, @"(?<!\|)\|\|(?=\S)([^|\n]+?)(?<=\S)\|\|(?!\|)", "<tg-spoiler>$1</tg-spoiler>");
        text = Regex.Replace(text, @"(?<![\w*])\*(?=[^\s*])([^*\n]+?)(?<=[^\s*])\*(?![\w*])", "<i>$1</i>");
        text = Regex.Replace(text, @"(?<![\w_])_(?=[^\s_])([^_\n]+?)(?<=[^\s_])_(?![\w_])", "<i>$1</i>");
        text = Regex.Replace(text, @"^#{1,6}\s+(.+)$", "<b>$1</b>", RegexOptions.Multiline);
        return Regex.Replace(text, "\u0001(\\d+)\u0001", m => kept[int.Parse(m.Groups[1].Value)]);
    }

    public static string Sanitize(string input)
    {
        var sb = new StringBuilder();
        var stack = new Stack<string>();
        var position = 0;
        foreach (Match m in Tag().Matches(input))
        {
            sb.Append(EscapeText(input[position..m.Index]));
            position = m.Index + m.Length;
            var closing = m.Groups[1].Value == "/";
            var raw = m.Groups[2].Value.ToLowerInvariant();
            if (!Canonical.TryGetValue(raw, out var name)) { sb.Append(EscapeText(m.Value)); continue; }
            var inCode = stack.Count > 0 && stack.Peek() is "code" or "pre";
            if (closing)
            {
                if (name == "span") name = "tg-spoiler";
                if (!stack.Contains(name) || (inCode && name is not ("code" or "pre"))) { if (inCode) sb.Append(EscapeText(m.Value)); continue; }
                while (stack.Count > 0)
                {
                    var top = stack.Pop();
                    sb.Append("</").Append(top).Append('>');
                    if (top == name) break;
                }
                continue;
            }
            // Inside code only <code> directly inside <pre> is meaningful; any other tag is literal text.
            if (inCode && !(stack.Peek() == "pre" && name == "code")) { sb.Append(EscapeText(m.Value)); continue; }
            var attrs = Attributes(m.Groups[3].Value);
            string? open;
            switch (name)
            {
                case "a":
                    var href = attrs.GetValueOrDefault("href") ?? "";
                    if (!(Uri.TryCreate(href, UriKind.Absolute, out var uri) && (uri.Scheme is "http" or "https") || href.StartsWith("tg://user?id=")))
                        continue; // drop the link, keep its text
                    open = $"<a href=\"{WebUtility.HtmlEncode(href)}\">";
                    break;
                case "span":
                    if (attrs.GetValueOrDefault("class") != "tg-spoiler") continue;
                    name = "tg-spoiler"; open = "<tg-spoiler>";
                    break;
                case "blockquote":
                    if (stack.Contains("blockquote")) continue; // Telegram does not nest quotes
                    open = attrs.ContainsKey("expandable") ? "<blockquote expandable>" : "<blockquote>";
                    break;
                case "tg-time":
                    if (!long.TryParse(attrs.GetValueOrDefault("unix"), out var unix) || unix <= 0) continue;
                    var format = attrs.GetValueOrDefault("format");
                    open = format is { Length: > 0 } f && TimeFormat().IsMatch(f) ? $"<tg-time unix=\"{unix}\" format=\"{f}\">" : $"<tg-time unix=\"{unix}\">";
                    break;
                case "code" when stack.Count > 0 && stack.Peek() == "pre":
                    var lang = attrs.GetValueOrDefault("class");
                    open = lang is not null && Regex.IsMatch(lang, @"^language-[a-zA-Z0-9_+#-]{1,30}$") ? $"<code class=\"{lang}\">" : "<code>";
                    break;
                default:
                    open = $"<{name}>";
                    break;
            }
            stack.Push(name);
            sb.Append(open);
        }
        sb.Append(EscapeText(input[position..]));
        while (stack.Count > 0) sb.Append("</").Append(stack.Pop()).Append('>');
        return Regex.Replace(sb.ToString(), "<(b|i|u|s|tg-spoiler|code)></\\1>", "");
    }

    private static Dictionary<string, string> Attributes(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match a in Attr().Matches(text))
            result[a.Groups[1].Value] = WebUtility.HtmlDecode(a.Groups[2].Success ? a.Groups[2].Value : a.Groups[3].Success ? a.Groups[3].Value : a.Groups[4].Value);
        return result;
    }

    /// <summary>Escapes &lt;, &gt; and &amp; but keeps the entities Telegram understands.</summary>
    private static string EscapeText(string text)
    {
        var sb = new StringBuilder();
        var last = 0;
        foreach (Match e in Entity().Matches(text))
        {
            sb.Append(EscapeAll(text[last..e.Index])).Append(e.Value);
            last = e.Index + e.Length;
        }
        return sb.Append(EscapeAll(text[last..])).ToString();
    }
    private static string EscapeAll(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    /// <summary>Rebuilds Telegram HTML from a message's text and entities (offsets are UTF-16, like .NET strings).</summary>
    public static string FromEntities(string text, System.Text.Json.JsonElement entities)
    {
        var list = entities.ValueKind == System.Text.Json.JsonValueKind.Array ? entities.EnumerateArray()
            .Select(e => (Start: (int)e.Number("offset"), End: (int)(e.Number("offset") + e.Number("length")), E: e))
            .Where(x => x.Start >= 0 && x.End <= text.Length && x.End > x.Start).OrderBy(x => x.Start).ThenByDescending(x => x.End).ToList() : [];
        string Open(System.Text.Json.JsonElement e) => e.Text("type") switch
        {
            "bold" => "<b>", "italic" => "<i>", "underline" => "<u>", "strikethrough" => "<s>", "spoiler" => "<tg-spoiler>",
            "code" => "<code>", "pre" => e.Text("language") is { Length: > 0 } l ? $"<pre><code class=\"language-{WebUtility.HtmlEncode(l)}\">" : "<pre>",
            "text_link" => $"<a href=\"{WebUtility.HtmlEncode(e.Text("url"))}\">",
            "text_mention" => $"<a href=\"tg://user?id={e.Field("user").Number("id")}\">",
            "blockquote" => "<blockquote>", "expandable_blockquote" => "<blockquote expandable>",
            "date_time" => $"<tg-time unix=\"{e.Number("unix_time")}\"" + (e.Text("date_time_format") is { Length: > 0 } f ? $" format=\"{f}\"" : "") + ">",
            _ => ""
        };
        string Close(System.Text.Json.JsonElement e) => e.Text("type") switch
        {
            "bold" => "</b>", "italic" => "</i>", "underline" => "</u>", "strikethrough" => "</s>", "spoiler" => "</tg-spoiler>", "code" => "</code>",
            "pre" => e.Text("language") is { Length: > 0 } ? "</code></pre>" : "</pre>", "text_link" or "text_mention" => "</a>",
            "blockquote" or "expandable_blockquote" => "</blockquote>", "date_time" => "</tg-time>", _ => ""
        };
        var sb = new StringBuilder();
        var open = new Stack<(int End, System.Text.Json.JsonElement E)>();
        var next = 0;
        for (var i = 0; i <= text.Length; i++)
        {
            while (open.Count > 0 && open.Peek().End == i) sb.Append(Close(open.Pop().E));
            while (next < list.Count && list[next].Start == i) { sb.Append(Open(list[next].E)); open.Push((list[next].End, list[next].E)); next++; }
            if (i < text.Length) sb.Append(EscapeAll(text[i].ToString()));
        }
        return sb.ToString();
    }

    /// <summary>Plain-text fallback when Telegram still rejects the markup.</summary>
    public static string Plain(string html) => WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]+>", ""));
}
