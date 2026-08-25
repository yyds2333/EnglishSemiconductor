using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using WordPin.Application;

namespace WordPin.Infrastructure.Dictionary;

/// <summary>
/// Uses MyMemory's free translation-memory endpoint. It sends only the term,
/// never the captured context, and can be configured to exclude machine
/// translation matches.
/// </summary>
public sealed class MyMemoryTranslationProvider : ITranslationDefinitionProvider, IDisposable
{
    private const string PromptVersion = "mymemory-v1";
    private readonly ILlmSettingsStore settingsStore;
    private readonly HttpClient httpClient;

    public MyMemoryTranslationProvider(ILlmSettingsStore settingsStore, HttpClient? httpClient = null)
    {
        this.settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        this.httpClient = httpClient ?? new HttpClient();
    }

    public LlmSettings Settings => settingsStore.Load();

    public int DailyLimit => Math.Clamp(Settings.OnlineTranslationDailyLimit, 1, 500);

    public bool IsConfigured
    {
        get
        {
            var settings = Settings;
            return settings.OnlineTranslationEnabled
                && !string.IsNullOrWhiteSpace(settings.OnlineTranslationBaseUrl);
        }
    }

    public async Task<GeneratedDefinitionCandidate?> TranslateAsync(
        DefinitionGenerationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Term);
        var settings = Settings;
        if (!settings.OnlineTranslationEnabled)
        {
            return null;
        }

        if (!Uri.TryCreate(settings.OnlineTranslationBaseUrl.TrimEnd('/') + "/get", UriKind.Absolute, out var endpoint)
            || endpoint.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException("MyMemory Base URL 必须是 HTTPS 地址。");
        }

        var builder = new UriBuilder(endpoint)
        {
            Query = string.Join("&", new[]
            {
                $"q={Uri.EscapeDataString(request.Term.Trim())}",
                $"langpair={Uri.EscapeDataString($"{request.Language.Trim()}|zh-CN")}",
                $"mt={(settings.OnlineMachineTranslationEnabled ? "1" : "0")}" 
            })
        };

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(settings.OnlineTranslationTimeoutSeconds, 5, 60)));
        using var response = await httpClient.GetAsync(builder.Uri, timeout.Token).ConfigureAwait(false);
        var responseText = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
        if (responseText.Length > 32 * 1024)
        {
            throw new InvalidDataException("MyMemory 返回内容超过 32 KB 限制。");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"MyMemory 请求失败（HTTP {(int)response.StatusCode}）。");
        }

        try
        {
            using var document = JsonDocument.Parse(responseText);
            var root = document.RootElement;
            if (root.TryGetProperty("responseStatus", out var responseStatus)
                && responseStatus.ValueKind == JsonValueKind.Number
                && responseStatus.GetInt32() != 200)
            {
                return null;
            }

            var translation = FindBestTranslation(root, request.Term.Trim());
            if (string.IsNullOrWhiteSpace(translation)
                || string.Equals(translation.Trim(), request.Term.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return new GeneratedDefinitionCandidate(
                Term: request.Term.Trim(),
                Phonetic: null,
                PartOfSpeech: null,
                DefinitionZh: Limit(translation, 4_000),
                DefinitionEn: request.Term.Trim(),
                Example: null,
                ProviderId: "mymemory",
                ModelName: settings.OnlineMachineTranslationEnabled ? "MyMemory MT" : "MyMemory Human TM",
                PromptVersion: PromptVersion,
                SourceDetail: endpoint.Host);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("MyMemory 返回格式无法识别。", exception);
        }
    }

    public void Dispose() => httpClient.Dispose();

    private static string? FindBestTranslation(JsonElement root, string term)
    {
        if (root.TryGetProperty("matches", out var matches) && matches.ValueKind == JsonValueKind.Array)
        {
            var candidates = matches.EnumerateArray()
                .Select(match => new
                {
                    Translation = ReadString(match, "translation"),
                    Match = ReadDouble(match, "match")
                })
                .Where(match => !string.IsNullOrWhiteSpace(match.Translation)
                    && !string.Equals(match.Translation.Trim(), term, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(match => match.Match)
                .Select(match => match.Translation!.Trim())
                .FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(candidates))
            {
                return candidates;
            }
        }

        if (root.TryGetProperty("responseData", out var responseData))
        {
            var translation = ReadString(responseData, "translatedText");
            return string.IsNullOrWhiteSpace(translation) ? null : translation.Trim();
        }

        return null;
    }

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static double ReadDouble(JsonElement element, string propertyName)
    {
        var value = ReadString(element, propertyName);
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result)
            ? result
            : 0;
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
}
