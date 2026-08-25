using System.Text;

namespace WordPin.Domain;

public static class TermNormalizer
{
    public static string NormalizeDisplay(string term)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(term);

        var normalized = term.Normalize(NormalizationForm.FormKC).Trim();
        var builder = new StringBuilder(normalized.Length);
        var previousWasWhitespace = false;
        foreach (var character in normalized)
        {
            if (char.IsWhiteSpace(character))
            {
                if (!previousWasWhitespace)
                {
                    builder.Append(' ');
                }

                previousWasWhitespace = true;
                continue;
            }

            builder.Append(character);
            previousWasWhitespace = false;
        }

        var result = builder.ToString();
        return string.IsNullOrWhiteSpace(result)
            ? throw new ArgumentException("Term must contain a non-whitespace character.", nameof(term))
            : result;
    }

    public static string NormalizeLookup(string term) =>
        NormalizeDisplay(term).ToLowerInvariant();

    public static string NormalizeLanguage(string language)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(language);
        return language.Trim().ToLowerInvariant();
    }
}
