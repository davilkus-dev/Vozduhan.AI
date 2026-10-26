using System.Text.RegularExpressions;

namespace Vozduhan.AI;

public sealed class AiTextRejectedException(string reason) : Exception("AI text rejected: " + reason)
{
    public string Reason { get; } = reason;
}

/// <summary>Code-only checks for model text before it reaches people. Nothing here rewrites the text beyond Normalize.</summary>
public static class TextGuard
{
    public const int MinLettersForLanguage = 40;
    public const double MinCyrillicShare = 0.5;
    public const int MinRepeatLength = 12;
    public const int RepeatCount = 3;
    public const double MinFitShare = 0.3;

    public static string Normalize(string text) => Regex.Replace(Json.Unescape(text.Replace("\r\n", "\n").Replace("\r", "\n")).Trim(), @"(?:\r?\n){3,}", "\n\n");

    public static string? Check(string text, string source, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(text)) return "empty";
        if (Garbled(text)) return "garbled";
        foreach (Match m in Regex.Matches(text, @"[\u3000-\u303F\u3040-\u30FF\u3400-\u4DBF\u4E00-\u9FFF\uAC00-\uD7AF\uF900-\uFAFF\uFF00-\uFFEF]+"))
            if (!source.Contains(m.Value, StringComparison.Ordinal)) return "cjk";
        var letters = Regex.Replace(text, @"<[^>]*>|&\w+;|https?://\S+|@\w+", " ").Where(char.IsLetter).ToList();
        if (letters.Count >= MinLettersForLanguage && letters.Count(c => c is >= '\u0400' and <= '\u04FF') < letters.Count * MinCyrillicShare) return "not_russian";
        if (Regex.Split(text, @"(?<=[.!?…])\s+|\n").Select(s => s.Trim().ToLowerInvariant()).Where(s => s.Length >= MinRepeatLength)
            .GroupBy(s => s).Any(g => g.Count() >= RepeatCount)) return "repeat";
        return text.Length > maxChars ? "too_long" : null;
    }

    private static bool Garbled(string text)
    {
        if (Regex.IsMatch(text, @"\\u[0-9a-fA-F]{4}|</?think>", RegexOptions.IgnoreCase)) return true;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\uFFFD' || (char.IsControl(c) && c is not ('\n' or '\t'))) return true;
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) { i++; continue; }
            if (char.IsSurrogate(c)) return true;
        }
        return false;
    }

    /// <summary>Russian wording of a rejection code, for the retry note and for editors.</summary>
    public static string Explain(string reason) => reason switch
    {
        "empty" => "пустой ответ",
        "cjk" => "иероглифы в тексте",
        "garbled" => "битые символы",
        "not_russian" => "ответ не по-русски",
        "repeat" => "зацикленные повторы",
        "too_long" => "слишком длинный текст",
        _ => reason
    };

    /// <summary>Cuts at the last paragraph, sentence or word boundary within maxChars. Adds nothing.</summary>
    public static string Fit(string text, int maxChars)
    {
        if (text.Length <= maxChars) return text;
        var window = text[..maxChars];
        if (char.IsHighSurrogate(window[^1])) window = window[..^1];
        var min = maxChars * MinFitShare;
        Func<int, int>[] levels =
        [
            i => window[i] == '\n' && i + 1 < window.Length && window[i + 1] == '\n' ? i : -1,
            i => window[i] == '\n' ? i : window[i] is '.' or '!' or '?' or '…' && (i + 1 == window.Length || char.IsWhiteSpace(window[i + 1])) ? i + 1 : -1,
            i => char.IsWhiteSpace(window[i]) ? i : -1
        ];
        foreach (var level in levels)
            for (var i = window.Length - 1; i >= 0; i--)
                if (level(i) is var end and >= 0)
                {
                    var cut = window[..end].TrimEnd();
                    if (cut.Length >= min) return cut;
                    break;
                }
        return window;
    }
}
