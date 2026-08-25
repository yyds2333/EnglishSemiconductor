using Microsoft.Data.Sqlite;
using WordPin.Application;
using WordPin.Domain;

namespace WordPin.Infrastructure.Learning;

public sealed class SqliteStudyQueueService : IStudyQueueService
{
    private readonly SqliteLearningDatabase database;

    public SqliteStudyQueueService(SqliteLearningDatabase database)
    {
        this.database = database ?? throw new ArgumentNullException(nameof(database));
    }

    public async Task<StudySessionSnapshot> GetOrCreateAsync(
        string localDate,
        DateTimeOffset now,
        int dailyLimit = 12,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localDate);
        if (dailyLimit is < 1 or > 500)
        {
            throw new ArgumentOutOfRangeException(nameof(dailyLimit), "Daily limit must be between 1 and 500.");
        }

        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        var existing = await LoadSessionAsync(connection, transaction, localDate, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return existing;
        }

        var candidates = await LoadCandidatesAsync(connection, transaction, now.ToUniversalTime(), cancellationToken).ConfigureAwait(false);
        var selected = candidates
            .OrderBy(item => item.Category)
            .ThenBy(item => item.NextReviewAt ?? DateTimeOffset.MaxValue)
            .ThenBy(item => item.CreatedAt)
            .Take(dailyLimit)
            .Select((item, index) => new StudyQueueItem(
                item.WordId,
                item.Term,
                item.Category,
                item.Reason,
                index))
            .ToList();
        var session = new StudySessionSnapshot(
            Guid.NewGuid(),
            localDate,
            now.ToUniversalTime(),
            selected.Count,
            selected);

        await InsertSessionAsync(connection, transaction, session, cancellationToken).ConfigureAwait(false);
        foreach (var item in selected)
        {
            await InsertSessionItemAsync(connection, transaction, session.Id, item, cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return session;
    }

    private static async Task<StudySessionSnapshot?> LoadSessionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string localDate,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id, local_date, started_at, planned_count
            FROM study_sessions
            WHERE local_date = $local_date
            ORDER BY started_at ASC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$local_date", localDate);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var sessionId = Guid.Parse(reader.GetString(0));
        var sessionLocalDate = reader.GetString(1);
        var sessionStartedAt = ParseTimestamp(reader.GetString(2));
        var plannedCount = reader.GetInt32(3);
        await reader.DisposeAsync().ConfigureAwait(false);
        var items = await LoadSessionItemsAsync(connection, transaction, sessionId, cancellationToken).ConfigureAwait(false);
        return new StudySessionSnapshot(
            sessionId,
            sessionLocalDate,
            sessionStartedAt,
            plannedCount,
            items);
    }

    private static async Task<IReadOnlyList<StudyQueueItem>> LoadSessionItemsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT word_id, queue_category, queue_reason, ordinal, words.term
            FROM study_session_items
            INNER JOIN words ON words.id = study_session_items.word_id
            WHERE session_id = $session_id
            ORDER BY ordinal;
            """;
        command.Parameters.AddWithValue("$session_id", sessionId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var items = new List<StudyQueueItem>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            items.Add(new StudyQueueItem(
                Guid.Parse(reader.GetString(0)),
                reader.GetString(4),
                ParseCategory(reader.GetString(1)),
                reader.GetString(2),
                reader.GetInt32(3)));
        }

        return items;
    }

    private static async Task<IReadOnlyList<QueueCandidate>> LoadCandidatesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id, term, next_review_at, last_review_at, last_feedback, created_at
            FROM words
            WHERE is_suspended = 0;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var candidates = new List<QueueCandidate>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            DateTimeOffset? nextReviewAt = reader.IsDBNull(2) ? null : ParseTimestamp(reader.GetString(2));
            DateTimeOffset? lastReviewAt = reader.IsDBNull(3) ? null : ParseTimestamp(reader.GetString(3));
            var createdAt = ParseTimestamp(reader.GetString(5));
            var category = Classify(nextReviewAt, lastReviewAt, reader.IsDBNull(4) ? null : reader.GetString(4), now);
            if (category is null)
            {
                continue;
            }

            candidates.Add(new QueueCandidate(
                Guid.Parse(reader.GetString(0)),
                reader.GetString(1),
                category.Value.Category,
                category.Value.Reason,
                nextReviewAt,
                createdAt));
        }

        return candidates;
    }

    private static (QueueCategory Category, string Reason)? Classify(
        DateTimeOffset? nextReviewAt,
        DateTimeOffset? lastReviewAt,
        string? lastFeedback,
        DateTimeOffset now)
    {
        if (lastReviewAt is null)
        {
            return (QueueCategory.New, "首次学习");
        }

        if (nextReviewAt is null || nextReviewAt > now)
        {
            return null;
        }

        if (nextReviewAt <= now.AddDays(-30))
        {
            return (QueueCategory.OverdueCheck, "超过计划复习时间30天，待抽查");
        }

        return string.Equals(lastFeedback, "AGAIN", StringComparison.OrdinalIgnoreCase)
            ? (QueueCategory.AgainRetry, "AGAIN后重试")
            : (QueueCategory.NormalDue, "计划复习到期");
    }

    private static async Task InsertSessionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        StudySessionSnapshot session,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO study_sessions(id, local_date, started_at, planned_count, completed_count)
            VALUES ($id, $local_date, $started_at, $planned_count, 0);
            """;
        command.Parameters.AddWithValue("$id", session.Id.ToString("D"));
        command.Parameters.AddWithValue("$local_date", session.LocalDate);
        command.Parameters.AddWithValue("$started_at", session.StartedAt.ToString("O"));
        command.Parameters.AddWithValue("$planned_count", session.PlannedCount);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertSessionItemAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid sessionId,
        StudyQueueItem item,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO study_session_items(
                id, session_id, word_id, queue_category, queue_reason, ordinal)
            VALUES ($id, $session_id, $word_id, $queue_category, $queue_reason, $ordinal);
            """;
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
        command.Parameters.AddWithValue("$session_id", sessionId.ToString("D"));
        command.Parameters.AddWithValue("$word_id", item.WordId.ToString("D"));
        command.Parameters.AddWithValue("$queue_category", ToCategoryValue(item.Category));
        command.Parameters.AddWithValue("$queue_reason", item.Reason);
        command.Parameters.AddWithValue("$ordinal", item.Ordinal);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string ToCategoryValue(QueueCategory category) => category switch
    {
        QueueCategory.OverdueCheck => "overdue_check",
        QueueCategory.AgainRetry => "again_retry",
        QueueCategory.NormalDue => "normal_due",
        QueueCategory.New => "new",
        _ => throw new ArgumentOutOfRangeException(nameof(category), category, "Unsupported queue category.")
    };

    private static QueueCategory ParseCategory(string value) => value switch
    {
        "overdue_check" => QueueCategory.OverdueCheck,
        "again_retry" => QueueCategory.AgainRetry,
        "normal_due" => QueueCategory.NormalDue,
        "new" => QueueCategory.New,
        _ => throw new InvalidDataException($"Unsupported queue category stored in database: {value}")
    };

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.Parse(value, null, System.Globalization.DateTimeStyles.RoundtripKind);

    private sealed record QueueCandidate(
        Guid WordId,
        string Term,
        QueueCategory Category,
        string Reason,
        DateTimeOffset? NextReviewAt,
        DateTimeOffset CreatedAt);
}
