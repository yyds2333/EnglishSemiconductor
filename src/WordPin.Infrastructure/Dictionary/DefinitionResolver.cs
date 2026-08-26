using System.Collections.Concurrent;
using WordPin.Application;
using WordPin.Domain;

namespace WordPin.Infrastructure.Dictionary;

public sealed class DefinitionResolver : IDefinitionResolver
{
    private readonly IDefinitionRepository definitionRepository;
    private readonly IDictionaryProvider dictionaryProvider;
    private readonly ILanguageModelDefinitionProvider languageModelProvider;
    private readonly ILlmUsageStore usageStore;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> termLocks = new(StringComparer.OrdinalIgnoreCase);

    public DefinitionResolver(
        IDefinitionRepository definitionRepository,
        IDictionaryProvider dictionaryProvider,
        ILanguageModelDefinitionProvider languageModelProvider,
        ILlmUsageStore usageStore)
    {
        this.definitionRepository = definitionRepository ?? throw new ArgumentNullException(nameof(definitionRepository));
        this.dictionaryProvider = dictionaryProvider ?? throw new ArgumentNullException(nameof(dictionaryProvider));
        this.languageModelProvider = languageModelProvider ?? throw new ArgumentNullException(nameof(languageModelProvider));
        this.usageStore = usageStore ?? throw new ArgumentNullException(nameof(usageStore));
    }

    public async Task<DefinitionResolution> ResolveAsync(
        WordRecord word,
        string? context = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(word);
        var saved = await definitionRepository.GetForWordAsync(word.Id, cancellationToken).ConfigureAwait(false);
        var accepted = saved.FirstOrDefault(definition => definition.Status == DefinitionStatus.Accepted);
        if (accepted is not null)
        {
            return new DefinitionResolution(new[] { accepted }, null);
        }

        var proposed = saved.FirstOrDefault(definition =>
            definition.Status == DefinitionStatus.Proposed
            && definition.GeneratedAt is not null
            && definition.GeneratedAt.Value >= DateTimeOffset.UtcNow.AddHours(-24));
        if (proposed is not null)
        {
            return new DefinitionResolution(new[] { proposed }, null, IsRemoteCandidate: true);
        }

        var local = await dictionaryProvider.LookupAsync(word.Term, word.Language, cancellationToken).ConfigureAwait(false);
        if (local is not null)
        {
            return new DefinitionResolution(Array.Empty<SavedDefinition>(), local);
        }

        if (!languageModelProvider.IsConfigured)
        {
            return new DefinitionResolution(Array.Empty<SavedDefinition>(), null);
        }

        var lockKey = $"{word.Language}:{word.NormalizedTerm}";
        var termLock = termLocks.GetOrAdd(lockKey, _ => new SemaphoreSlim(1, 1));
        await termLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            saved = await definitionRepository.GetForWordAsync(word.Id, cancellationToken).ConfigureAwait(false);
            accepted = saved.FirstOrDefault(definition => definition.Status == DefinitionStatus.Accepted);
            if (accepted is not null)
            {
                return new DefinitionResolution(new[] { accepted }, null);
            }

            proposed = saved.FirstOrDefault(definition => definition.Status == DefinitionStatus.Proposed);
            if (proposed is not null)
            {
                return new DefinitionResolution(new[] { proposed }, null, IsRemoteCandidate: true);
            }

            local = await dictionaryProvider.LookupAsync(word.Term, word.Language, cancellationToken).ConfigureAwait(false);
            if (local is not null)
            {
                return new DefinitionResolution(Array.Empty<SavedDefinition>(), local);
            }

            var settings = languageModelProvider is OpenAiCompatibleDefinitionProvider openAi
                ? openAi.Settings
                : new LlmSettings(DailyLimit: 30);
            if (!await usageStore.TryConsumeAsync(DateOnly.FromDateTime(DateTime.Now), settings.DailyLimit, cancellationToken)
                    .ConfigureAwait(false))
            {
                return new DefinitionResolution(Array.Empty<SavedDefinition>(), null);
            }

            var candidate = await languageModelProvider.GenerateAsync(
                new DefinitionGenerationRequest(word.Term, word.Language, context),
                cancellationToken).ConfigureAwait(false);
            var savedCandidate = await definitionRepository.SaveAsync(
                new DefinitionDraft(
                    WordId: word.Id,
                    PartOfSpeech: candidate.PartOfSpeech,
                    DefinitionZh: candidate.DefinitionZh,
                    DefinitionEn: candidate.DefinitionEn,
                    Example: candidate.Example,
                    SourceKind: DefinitionSourceKind.LanguageModel,
                    Status: DefinitionStatus.Proposed,
                    SourceDetail: candidate.SourceDetail,
                    ModelName: candidate.ModelName,
                    PromptVersion: candidate.PromptVersion,
                    GeneratedAt: DateTimeOffset.UtcNow),
                cancellationToken).ConfigureAwait(false);
            return new DefinitionResolution(new[] { savedCandidate }, null, IsRemoteCandidate: true);
        }
        finally
        {
            termLock.Release();
        }
    }
}
