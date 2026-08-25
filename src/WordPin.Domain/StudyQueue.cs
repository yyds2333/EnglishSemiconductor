namespace WordPin.Domain;

public enum QueueCategory
{
    OverdueCheck = 0,
    AgainRetry = 1,
    NormalDue = 2,
    New = 3
}

public sealed record StudyQueueItem(
    Guid WordId,
    string Term,
    QueueCategory Category,
    string Reason,
    int Ordinal);

public sealed record StudySessionSnapshot(
    Guid Id,
    string LocalDate,
    DateTimeOffset StartedAt,
    int PlannedCount,
    IReadOnlyList<StudyQueueItem> Items);
