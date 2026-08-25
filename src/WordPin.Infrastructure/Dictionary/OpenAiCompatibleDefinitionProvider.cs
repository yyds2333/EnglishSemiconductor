using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using WordPin.Application;

namespace WordPin.Infrastructure.Dictionary;

public sealed class OpenAiCompatibleDefinitionProvider : ILanguageModelDefinitionProvider, IDisposable
{
    private const string PromptVersion = "llm-json-v1";
    private readonly ILlmSettingsStore settingsStore;
    private readonly HttpClient httpClient;

    public OpenAiCompatibleDefinitionProvider(ILlmSettingsStore settingsStore, HttpClient? httpClient = null)
    {
        this.settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        this.httpClient = httpClient ?? new HttpClient();
    }

    public LlmSettings Settings => settingsStore.Load();

    public bool IsConfigured
    {
        get
        {
            var settings = Settings;
            return settings.Enabled
                && !string.IsNullOrWhiteSpace(settings.ApiKey)
                && !string.IsNullOrWhiteSpace(settings.BaseUrl)
                && !string.IsNullOrWhiteSpace(settings.Model);
        }
    }

    public async Task<GeneratedDefinitionCandidate> GenerateAsync(
        DefinitionGenerationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Term);
        var settings = Settings;
        if (!settings.Enabled || string.IsNullOrWhiteSpace(settings.ApiKey)
            || string.IsNullOrWhiteSpace(settings.BaseUrl) || string.IsNullOrWhiteSpace(settings.Model))
        {
            throw new InvalidOperationException("AI 释义补全尚未配置完整。");
        }

        if (!Uri.TryCreate(settings.BaseUrl.TrimEnd('/') + "/chat/completions", UriKind.Absolute, out var endpoint)
            || endpoint.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException("AI Base URL 必须是 HTTPS 地址。");
        }

        var userPayload = JsonSerializer.Serialize(new
        {
            term = request.Term.Trim(),
            language = request.Language.Trim(),
            output_language = "zh-CN",
            context = settings.SendContext ? request.Context?.Trim() : null
        });
        var body = JsonSerializer.Serialize(new
        {
            model = settings.Model.Trim(),
            temperature = 0.2,
            response_format = new { type = "json_object" },
            messages = new[]
            {
                new { role = "system", content = "你是严谨的英汉词典助手。只返回 JSON，不要 Markdown、HTML 或额外解释。字段必须是 term、phonetic、part_of_speech、definition_zh、definition_en、example。释义要符合给定词语和词性；不知道时将对应字段设为 null。" },
                new { role = "user", content = userPayload }
            }
        });

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(settings.TimeoutSeconds, 5, 60)));
        using var message = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);
        using var response = await httpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
            .ConfigureAwait(false);
        if (response.Content.Headers.ContentLength is > 64 * 1024)
        {
            throw new InvalidDataException("AI 返回内容超过 64 KB 限制。");
        }

        var responseText = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
        if (responseText.Length > 64 * 1024)
        {
            throw new InvalidDataException("AI 返回内容超过 64 KB 限制。");
        }
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"AI 请求失败（HTTP {(int)response.StatusCode}）。");
        }

        try
        {
            using var document = JsonDocument.Parse(responseText);
            var content = document.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString();
            if (string.IsNullOrWhiteSpace(content))
            {
                throw new InvalidDataException("AI 返回内容为空。");
            }

            var normalized = StripCodeFence(content);
            var result = JsonSerializer.Deserialize<LlmResponse>(normalized)
                ?? throw new InvalidDataException("AI 返回 JSON 为空。");
            if (string.IsNullOrWhiteSpace(result.DefinitionZh) && string.IsNullOrWhiteSpace(result.DefinitionEn))
            {
                throw new InvalidDataException("AI 未返回可用释义。");
            }

            return new GeneratedDefinitionCandidate(
                Term: request.Term.Trim(),
                Phonetic: Limit(result.Phonetic, 200),
                PartOfSpeech: Limit(result.PartOfSpeech, 100),
                DefinitionZh: Limit(result.DefinitionZh, 4_000),
                DefinitionEn: Limit(result.DefinitionEn, 4_000),
                Example: Limit(result.Example, 2_000),
                ProviderId: "openai-compatible",
                ModelName: settings.Model.Trim(),
                PromptVersion: PromptVersion,
                SourceDetail: endpoint.Host);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("AI 返回格式无法识别。", exception);
        }
    }

    public void Dispose() => httpClient.Dispose();

    private static string StripCodeFence(string content)
    {
        var trimmed = content.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            return trimmed;
        }

        var firstLineEnd = trimmed.IndexOf('\n');
        var lastFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
        return firstLineEnd >= 0 && lastFence > firstLineEnd
            ? trimmed[(firstLineEnd + 1)..lastFence].Trim()
            : trimmed;
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

    private sealed record LlmResponse(
        string? Term,
        string? Phonetic,
        [property: System.Text.Json.Serialization.JsonPropertyName("part_of_speech")] string? PartOfSpeech,
        [property: System.Text.Json.Serialization.JsonPropertyName("definition_zh")] string? DefinitionZh,
        [property: System.Text.Json.Serialization.JsonPropertyName("definition_en")] string? DefinitionEn,
        string? Example);
}
