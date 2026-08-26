namespace WordPin.Domain;

public enum DefinitionSourceKind
{
    Manual,
    LanguageModel
}

public enum DefinitionStatus
{
    Proposed,
    Accepted,
    Rejected
}

/// <summary>
/// A user-owned definition snapshot. It is separate from the replaceable
/// read-only dictionary database so edits and accepted AI suggestions survive
/// dictionary upgrades.
/// </summary>
public sealed record SavedDefinition(
    Guid Id,
    Guid WordId,
    string? PartOfSpeech,
    string? DefinitionZh,
    string? DefinitionEn,
    string? Example,
    int SortOrder,
    DefinitionSourceKind SourceKind,
    DefinitionStatus Status,
    string? SourceDetail,
    string? ModelName,
    string? PromptVersion,
    DateTimeOffset? GeneratedAt,
    DateTimeOffset? ConfirmedAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record DefinitionDraft(
    Guid WordId,
    string? PartOfSpeech,
    string? DefinitionZh,
    string? DefinitionEn,
    string? Example,
    int SortOrder = 0,
    DefinitionSourceKind SourceKind = DefinitionSourceKind.Manual,
    DefinitionStatus Status = DefinitionStatus.Accepted,
    string? SourceDetail = null,
    string? ModelName = null,
    string? PromptVersion = null,
    DateTimeOffset? GeneratedAt = null,
    DateTimeOffset? ConfirmedAt = null,
    Guid? ExistingId = null);

public sealed record DefinitionResolution(
    IReadOnlyList<SavedDefinition> Definitions,
    DictionaryEntry? DictionaryEntry,
    bool IsPendingRemoteLookup = false,
    bool IsRemoteCandidate = false)
{
    public bool HasDefinition => Definitions.Count > 0 || DictionaryEntry is not null;
}
