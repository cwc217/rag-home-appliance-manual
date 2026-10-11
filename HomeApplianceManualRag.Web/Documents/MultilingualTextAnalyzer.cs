using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace HomeApplianceManualRag.Web.Documents;

public sealed record AnalyzedChunkText(
    string SearchText,
    string LanguageCode,
    int EstimatedTokenCount);

public interface IChunkTextAnalyzer
{
    AnalyzedChunkText Analyze(string text, string? documentLanguage = null);
    string BuildTsQuery(string text);
}

public sealed class MultilingualTextAnalyzer : IChunkTextAnalyzer
{
    private static readonly Regex Identifiers = new(
        """[\p{L}\p{N}]+(?::[\p{L}\p{N}]+)+|[\p{L}\p{N}]+(?:[-_][\p{L}\p{N}]+)+""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public AnalyzedChunkText Analyze(string text, string? documentLanguage = null)
    {
        var normalized = (text ?? string.Empty).Normalize(NormalizationForm.FormKC);
        var tokens = new List<string>();
        var cjkRun = new List<string>();
        var word = new StringBuilder();
        var scripts = new HashSet<string>(StringComparer.Ordinal);
        var tokenEstimate = 0;
        var latinRunLength = 0;

        void FlushWord()
        {
            if (word.Length == 0)
                return;
            var value = word.ToString().ToLowerInvariant();
            tokens.Add(value);
            tokenEstimate += Math.Max(1, (latinRunLength + 3) / 4);
            word.Clear();
            latinRunLength = 0;
        }

        void FlushCjk()
        {
            if (cjkRun.Count == 0)
                return;
            if (cjkRun.Count == 1)
            {
                tokens.Add(cjkRun[0]);
            }
            else
            {
                for (var index = 0; index < cjkRun.Count - 1; index++)
                    tokens.Add(cjkRun[index] + cjkRun[index + 1]);
            }
            tokenEstimate += cjkRun.Count;
            cjkRun.Clear();
        }

        foreach (var rune in normalized.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            var value = rune.ToString();
            if (IsCjk(rune))
            {
                FlushWord();
                cjkRun.Add(value);
                scripts.Add(GetScript(rune));
                continue;
            }

            FlushCjk();
            if (Rune.IsLetterOrDigit(rune))
            {
                word.Append(value);
                latinRunLength++;
                scripts.Add(GetScript(rune));
                continue;
            }

            FlushWord();
            if (!Rune.IsWhiteSpace(rune) && category is not UnicodeCategory.Format and not UnicodeCategory.Control)
                tokenEstimate++;
        }
        FlushCjk();
        FlushWord();

        foreach (Match match in Identifiers.Matches(normalized))
            tokens.Add(Regex.Replace(match.Value, "[:_-]", string.Empty).ToLowerInvariant());

        var distinctTokens = tokens
            .Where(token => !string.IsNullOrWhiteSpace(token))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return new AnalyzedChunkText(
            string.Join(' ', distinctTokens),
            ResolveLanguage(scripts, documentLanguage),
            Math.Max(tokenEstimate, distinctTokens.Length));
    }

    public string BuildTsQuery(string text)
    {
        var tokens = Analyze(text).SearchText
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return string.Join(" | ", tokens.Select(token => $"'{token.Replace("'", "''", StringComparison.Ordinal)}'"));
    }

    private static bool IsCjk(Rune rune) =>
        rune.Value is >= 0x3400 and <= 0x9FFF or
            >= 0xF900 and <= 0xFAFF or
            >= 0x3040 and <= 0x30FF or
            >= 0x31F0 and <= 0x31FF or
            >= 0xAC00 and <= 0xD7AF or
            >= 0x3100 and <= 0x312F;

    private static string GetScript(Rune rune) => rune.Value switch
    {
        >= 0x3400 and <= 0x9FFF or >= 0xF900 and <= 0xFAFF => "Hani",
        >= 0x3040 and <= 0x30FF or >= 0x31F0 and <= 0x31FF => "Jpan",
        >= 0xAC00 and <= 0xD7AF => "Kore",
        >= 0x3100 and <= 0x312F => "Bopo",
        >= 0x0600 and <= 0x06FF => "Arab",
        >= 0x0400 and <= 0x04FF => "Cyrl",
        >= 0x0E00 and <= 0x0E7F => "Thai",
        _ => "Latn"
    };

    private static string ResolveLanguage(HashSet<string> scripts, string? documentLanguage)
    {
        if (scripts.Contains("Jpan"))
            return "ja";
        if (scripts.Contains("Kore"))
            return "ko";
        if (scripts.Contains("Hani") || scripts.Contains("Bopo"))
        {
            var normalizedLanguage = documentLanguage?.Trim().ToLowerInvariant();
            if (scripts.Count > 1)
                return "mul";
            return scripts.Contains("Bopo") ||
                   normalizedLanguage is not null &&
                   (normalizedLanguage.Contains("traditional chinese", StringComparison.Ordinal) ||
                    normalizedLanguage.StartsWith("zh-tw", StringComparison.Ordinal) ||
                    normalizedLanguage.StartsWith("zh-hk", StringComparison.Ordinal))
                ? "zh-TW"
                : "zh";
        }
        if (scripts.Count > 1)
            return "mul";
        if (scripts.Contains("Arab"))
            return "und-Arab";
        if (scripts.Contains("Cyrl"))
            return "und-Cyrl";
        if (scripts.Contains("Thai"))
            return "th";

        var language = documentLanguage?.Trim();
        if (string.IsNullOrWhiteSpace(language))
            return scripts.Contains("Latn") ? "und-Latn" : "und";
        var normalized = language.ToLowerInvariant();
        if (normalized.Contains("traditional chinese", StringComparison.Ordinal) ||
            normalized.Contains("zh-tw", StringComparison.Ordinal) ||
            normalized.Contains("zh-hk", StringComparison.Ordinal))
            return "zh-TW";
        if (normalized.Contains("chinese", StringComparison.Ordinal) ||
            normalized.StartsWith("zh", StringComparison.Ordinal))
            return "zh";
        return language.Length > 16 ? language[..16] : language;
    }
}
