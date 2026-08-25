namespace WordPin.Domain;

public sealed record NewWordCapture(
    string Term,
    string Language = "en",
    EntryKind EntryKind = EntryKind.Word,
    string? SenseKey = null,
    string? Sentence = null,
    string? SourceApplication = null,
    string? SourceWindowTitle = null);

public sealed record WordRecord(
    Guid Id,
    string Term,
    string NormalizedTerm,
    string Language,
    EntryKind EntryKind,
    string? SenseKey,
    int MasteryScore,
    int MasteryLevel,
    int EncounterCount,
    bool IsSuspended,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record WordCaptureResult(
    WordRecord Word,
    bool IsNew,
    bool RequiresSenseSelection,
    IReadOnlyList<WordRecord> Candidates);

public sealed record ReviewResult(
    WordRecord Word,
    MasteryEvaluation Evaluation);
