namespace WordPin.Domain;

/// <summary>
/// A normalized dictionary record. The record deliberately keeps the provider
/// identity so that local data can be rebuilt or replaced without mixing
/// definitions from different sources.
/// </summary>
public sealed record DictionaryEntry(
    string Term,
    string Language,
    string ProviderId,
    string? Phonetic,
    string? Definition,
    string? Translation,
    string? PartOfSpeech,
    string? WordForms,
    string? AudioUrl,
    string? ProviderVersion);
