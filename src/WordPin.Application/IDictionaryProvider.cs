using WordPin.Domain;

namespace WordPin.Application;

/// <summary>
/// Dictionary lookup boundary. The UI and learning workflow must not depend on
/// a particular online service or local data format.
/// </summary>
public interface IDictionaryProvider
{
    string ProviderId { get; }

    bool IsOnline { get; }

    bool CanCacheNormalizedFields { get; }

    Task<DictionaryEntry?> LookupAsync(
        string term,
        string language,
        CancellationToken cancellationToken = default);
}
