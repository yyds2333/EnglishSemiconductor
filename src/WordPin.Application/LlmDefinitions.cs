using WordPin.Domain;

namespace WordPin.Application;

public sealed record LlmSettings(
    bool Enabled = false,
    string BaseUrl = "https://api.openai.com/v1",
    string Model = "",
    string? ApiKey = null,
    int DailyLimit = 30,
    bool SendContext = false,
    int TimeoutSeconds = 15,
    bool OnlineTranslationEnabled = true,
    bool OnlineMachineTranslationEnabled = false,
    string OnlineTranslationBaseUrl = "https://api.mymemory.translated.net",
    int OnlineTranslationDailyLimit = 60,
    int OnlineTranslationTimeoutSeconds = 10);

public sealed record DefinitionGenerationRequest(
    string Term,
    string Language,
    string? Context = null);

public sealed record GeneratedDefinitionCandidate(
    string Term,
    string? Phonetic,
    string? PartOfSpeech,
    string? DefinitionZh,
    string? DefinitionEn,
    string? Example,
    string ProviderId,
    string ModelName,
    string PromptVersion,
    string? SourceDetail = null);

public interface ILanguageModelDefinitionProvider
{
    bool IsConfigured { get; }

    int DailyLimit => 30;

    Task<GeneratedDefinitionCandidate> GenerateAsync(
        DefinitionGenerationRequest request,
        CancellationToken cancellationToken = default);
}

public interface ITranslationDefinitionProvider
{
    bool IsConfigured { get; }

    int DailyLimit => 60;

    Task<GeneratedDefinitionCandidate?> TranslateAsync(
        DefinitionGenerationRequest request,
        CancellationToken cancellationToken = default);
}

public interface IDefinitionResolver
{
    Task<DefinitionResolution> ResolveAsync(
        WordRecord word,
        string? context = null,
        CancellationToken cancellationToken = default);
}

public interface ILlmSettingsStore
{
    LlmSettings Load();

    void Save(LlmSettings settings);
}

public interface ILlmUsageStore
{
    Task<bool> TryConsumeAsync(
        DateOnly localDate,
        int dailyLimit,
        CancellationToken cancellationToken = default);
}

public interface IRemoteUsageStore
{
    Task<bool> TryConsumeAsync(
        string provider,
        DateOnly localDate,
        int dailyLimit,
        CancellationToken cancellationToken = default);
}
