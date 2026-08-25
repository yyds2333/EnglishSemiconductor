using System.Runtime.CompilerServices;
using WordPin.Domain;

namespace WordPin.Infrastructure.Dictionary;

/// <summary>
/// Reads the ECDICT CSV export without loading the complete data pack into
/// memory. ECDICT's current export uses a stable header and RFC-4180-style
/// quoting; this reader also handles escaped quotes and commas in fields.
/// </summary>
public sealed class EcdictCsvReader
{
    private const int MaxTermLength = 200;
    private const int MaxPhoneticLength = 200;
    private const int MaxTextLength = 4_000;
    private const int MaxWordFormsLength = 1_000;

    public static async IAsyncEnumerable<DictionaryEntry> ReadAsync(
        Stream csvStream,
        string providerVersion,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(csvStream);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerVersion);

        using var reader = new StreamReader(csvStream, leaveOpen: true);
        var headerLine = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        if (headerLine is null)
        {
            yield break;
        }

        var headers = ParseCsvLine(headerLine)
            .Select((name, index) => new { Name = name.Trim(), Index = index })
            .ToDictionary(item => item.Name, item => item.Index, StringComparer.OrdinalIgnoreCase);

        if (!headers.ContainsKey("word"))
        {
            throw new InvalidDataException("ECDICT CSV is missing the required 'word' column.");
        }

        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var fields = ParseCsvLine(line);
            var term = Limit(GetField(fields, headers, "word"), MaxTermLength);
            if (string.IsNullOrWhiteSpace(term))
            {
                continue;
            }

            yield return new DictionaryEntry(
                Term: term,
                Language: "en",
                ProviderId: "ecdict",
                Phonetic: Limit(GetField(fields, headers, "phonetic"), MaxPhoneticLength),
                Definition: Limit(GetField(fields, headers, "definition"), MaxTextLength),
                Translation: Limit(GetField(fields, headers, "translation"), MaxTextLength),
                PartOfSpeech: Limit(GetField(fields, headers, "pos"), 100),
                WordForms: Limit(GetField(fields, headers, "exchange"), MaxWordFormsLength),
                AudioUrl: Limit(GetField(fields, headers, "audio"), 2_000),
                ProviderVersion: providerVersion);
        }
    }

    internal static IReadOnlyList<string> ParseCsvLine(string line)
    {
        ArgumentNullException.ThrowIfNull(line);

        var fields = new List<string>();
        var builder = new System.Text.StringBuilder();
        var quoted = false;

        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];
            if (character == '"')
            {
                if (quoted && index + 1 < line.Length && line[index + 1] == '"')
                {
                    builder.Append('"');
                    index++;
                }
                else
                {
                    quoted = !quoted;
                }

                continue;
            }

            if (character == ',' && !quoted)
            {
                fields.Add(builder.ToString());
                builder.Clear();
                continue;
            }

            builder.Append(character);
        }

        if (quoted)
        {
            throw new InvalidDataException("ECDICT CSV contains an unterminated quoted field.");
        }

        fields.Add(builder.ToString());
        return fields;
    }

    private static string? GetField(
        IReadOnlyList<string> fields,
        Dictionary<string, int> headers,
        string name)
    {
        return headers.TryGetValue(name, out var index) && index < fields.Count
            ? NullIfWhiteSpace(fields[index])
            : null;
    }

    private static string? Limit(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim();
        return normalized.Length <= maxLength ? normalized : normalized[..maxLength];
    }

    private static string? NullIfWhiteSpace(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
