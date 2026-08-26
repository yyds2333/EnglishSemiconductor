using Microsoft.Data.Sqlite;
using WordPin.Application;
using WordPin.Domain;

namespace WordPin.Infrastructure.Learning;

public sealed class SqliteWordRepository : IWordRepository
{
    private readonly SqliteLearningDatabase database;

    public SqliteWordRepository(SqliteLearningDatabase database)
    {
        this.database = database ?? throw new ArgumentNullException(nameof(database));
    }

    public async Task<WordCaptureResult> CaptureAsync(
        NewWordCapture capture,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(capture);
        var displayTerm = TermNormalizer.NormalizeDisplay(capture.Term);
        var normalizedTerm = TermNormalizer.NormalizeLookup(displayTerm);
        var language = TermNormalizer.NormalizeLanguage(capture.Language);

        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        var candidates = await FindCandidatesAsync(
            connection,
            transaction,
            normalizedTerm,
            language,
            capture.EntryKind,
            cancellationToken).ConfigureAwait(false);

        var exactSense = candidates
            .Where(candidate => string.Equals(candidate.SenseKey, capture.SenseKey, StringComparison.Ordinal))
            .ToList();

        if (exactSense.Count == 1)
        {
            var existing = exactSense[0];
            await AddEncounterAsync(
                connection,
                transaction,
                existing.Id,
                capture,
                cancellationToken).ConfigureAwait(false);
            var updated = existing with
            {
                EncounterCount = existing.EncounterCount + 1,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            await UpdateEncounterCountAsync(connection, transaction, updated, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new WordCaptureResult(updated, false, false, candidates);
        }

        if (candidates.Count > 0 && (capture.SenseKey is null || candidates.Count > 1))
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return new WordCaptureResult(candidates[0], false, true, candidates);
        }

        var now = DateTimeOffset.UtcNow;
        var created = new WordRecord(
            Id: Guid.NewGuid(),
            Term: displayTerm,
            NormalizedTerm: normalizedTerm,
            Language: language,
            EntryKind: capture.EntryKind,
            SenseKey: capture.SenseKey,
            MasteryScore: 0,
            MasteryLevel: 0,
            EncounterCount: 1,
            IsSuspended: false,
            CreatedAt: now,
            UpdatedAt: now);

        await InsertWordAsync(
            connection,
            transaction,
            created,
            cancellationToken).ConfigureAwait(false);
        await AddEncounterAsync(
            connection,
            transaction,
            created.Id,
            capture,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new WordCaptureResult(created, true, false, candidates);
    }

    public async Task<IReadOnlyList<WordRecord>> FindCandidatesAsync(
        string term,
        string language = "en",
        EntryKind entryKind = EntryKind.Word,
        CancellationToken cancellationToken = default)
    {
        var normalizedTerm = TermNormalizer.NormalizeLookup(term);
        var normalizedLanguage = TermNormalizer.NormalizeLanguage(language);
        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await FindCandidatesAsync(
            connection,
            transaction: null,
            normalizedTerm,
            normalizedLanguage,
            entryKind,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> UndoLastCaptureAsync(
        WordCaptureResult capture,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(capture);
        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        var changed = capture.IsNew
            ? await DeleteNewWordAsync(connection, transaction, capture.Word.Id, cancellationToken).ConfigureAwait(false)
            : await DeleteLatestEncounterAsync(connection, transaction, capture.Word.Id, cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return changed;
    }

    public async Task<ReviewResult> ReviewAsync(
        Guid wordId,
        ReviewFeedback feedback,
        DateTimeOffset reviewedAt,
        bool usedHint = false,
        CancellationToken cancellationToken = default)
    {
        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        var current = await LoadWordForReviewAsync(connection, transaction, wordId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Word not found: {wordId}");
        var evaluation = MasteryAlgorithm.Evaluate(current.State, feedback, reviewedAt, usedHint);
        await UpdateMasteryAsync(connection, transaction, wordId, evaluation.After, cancellationToken).ConfigureAwait(false);
        await InsertReviewEventAsync(connection, transaction, wordId, evaluation, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        var updatedWord = current.Word with
        {
            MasteryScore = evaluation.After.Score,
            MasteryLevel = evaluation.After.Level,
            UpdatedAt = evaluation.After.LastReviewedAt ?? DateTimeOffset.UtcNow
        };
        return new ReviewResult(updatedWord, evaluation);
    }

    public async Task<IReadOnlyList<SavedDefinition>> GetForWordAsync(
        Guid wordId,
        CancellationToken cancellationToken = default)
    {
        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, word_id, part_of_speech, definition_zh, definition_en, example,
                   sort_order, source_kind, status, source_detail, model_name,
                   prompt_version, generated_at, confirmed_at, created_at, updated_at
            FROM definitions
            WHERE word_id = $word_id
              AND status <> 'rejected'
            ORDER BY
                CASE source_kind WHEN 'manual' THEN 0 WHEN 'llm' THEN 1 ELSE 2 END,
                CASE status WHEN 'accepted' THEN 0 WHEN 'proposed' THEN 1 ELSE 2 END,
                sort_order ASC,
                created_at ASC;
            """;
        command.Parameters.AddWithValue("$word_id", wordId.ToString("D"));

        var definitions = new List<SavedDefinition>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            definitions.Add(ReadDefinition(reader));
        }

        return definitions;
    }

    public async Task<SavedDefinition> SaveAsync(
        DefinitionDraft draft,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (string.IsNullOrWhiteSpace(draft.DefinitionZh) && string.IsNullOrWhiteSpace(draft.DefinitionEn))
        {
            throw new ArgumentException("A Chinese or English definition is required.", nameof(draft));
        }

        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow;
        var id = draft.ExistingId ?? Guid.NewGuid();
        var existing = draft.ExistingId is null
            ? null
            : await LoadDefinitionAsync(connection, transaction, draft.ExistingId.Value, cancellationToken)
                .ConfigureAwait(false);

        if (draft.ExistingId is not null && existing is null)
        {
            throw new KeyNotFoundException($"Definition not found: {draft.ExistingId.Value}");
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = existing is null
            ? """
              INSERT INTO definitions (
                  id, word_id, part_of_speech, definition_zh, definition_en, example,
                  sort_order, provider, source_kind, status, source_detail, model_name,
                  prompt_version, generated_at, confirmed_at, created_at, updated_at)
              VALUES ($id, $word_id, $part_of_speech, $definition_zh, $definition_en, $example,
                      $sort_order, $provider, $source_kind, $status, $source_detail, $model_name,
                      $prompt_version, $generated_at, $confirmed_at, $created_at, $updated_at);
              """
            : """
              UPDATE definitions SET
                  part_of_speech = $part_of_speech,
                  definition_zh = $definition_zh,
                  definition_en = $definition_en,
                  example = $example,
                  sort_order = $sort_order,
                  provider = $provider,
                  source_kind = $source_kind,
                  status = $status,
                  source_detail = $source_detail,
                  model_name = $model_name,
                  prompt_version = $prompt_version,
                  generated_at = $generated_at,
                  confirmed_at = $confirmed_at,
                  updated_at = $updated_at
              WHERE id = $id AND word_id = $word_id;
              """;
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        command.Parameters.AddWithValue("$word_id", draft.WordId.ToString("D"));
        command.Parameters.AddWithValue("$part_of_speech", DbValue(draft.PartOfSpeech));
        command.Parameters.AddWithValue("$definition_zh", DbValue(draft.DefinitionZh));
        command.Parameters.AddWithValue("$definition_en", DbValue(draft.DefinitionEn));
        command.Parameters.AddWithValue("$example", DbValue(draft.Example));
        command.Parameters.AddWithValue("$sort_order", draft.SortOrder);
        command.Parameters.AddWithValue("$provider", draft.SourceKind == DefinitionSourceKind.LanguageModel ? "llm" : "manual");
        command.Parameters.AddWithValue("$source_kind", ToSourceKindValue(draft.SourceKind));
        command.Parameters.AddWithValue("$status", draft.Status.ToString().ToLowerInvariant());
        command.Parameters.AddWithValue("$source_detail", DbValue(draft.SourceDetail));
        command.Parameters.AddWithValue("$model_name", DbValue(draft.ModelName));
        command.Parameters.AddWithValue("$prompt_version", DbValue(draft.PromptVersion));
        command.Parameters.AddWithValue("$generated_at", DbValue(ToTimestamp(draft.GeneratedAt)));
        command.Parameters.AddWithValue("$confirmed_at", DbValue(ToTimestamp(draft.ConfirmedAt)));
        command.Parameters.AddWithValue("$created_at", ToTimestamp(existing?.CreatedAt ?? now));
        command.Parameters.AddWithValue("$updated_at", ToTimestamp(now));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new SavedDefinition(
            Id: id,
            WordId: draft.WordId,
            PartOfSpeech: draft.PartOfSpeech,
            DefinitionZh: draft.DefinitionZh,
            DefinitionEn: draft.DefinitionEn,
            Example: draft.Example,
            SortOrder: draft.SortOrder,
            SourceKind: draft.SourceKind,
            Status: draft.Status,
            SourceDetail: draft.SourceDetail,
            ModelName: draft.ModelName,
            PromptVersion: draft.PromptVersion,
            GeneratedAt: draft.GeneratedAt,
            ConfirmedAt: draft.ConfirmedAt,
            CreatedAt: existing?.CreatedAt ?? now,
            UpdatedAt: now);
    }

    public async Task<bool> DeleteAsync(
        Guid definitionId,
        CancellationToken cancellationToken = default)
    {
        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM definitions WHERE id = $id;";
        command.Parameters.AddWithValue("$id", definitionId.ToString("D"));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    private static async Task<IReadOnlyList<WordRecord>> FindCandidatesAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string normalizedTerm,
        string language,
        EntryKind entryKind,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id, term, normalized_term, language, entry_kind, sense_key,
                   mastery_score, mastery_level, encounter_count, is_suspended,
                   created_at, updated_at
            FROM words
            WHERE normalized_term = $normalized_term
              AND language = $language
              AND entry_kind = $entry_kind
              AND is_suspended = 0
            ORDER BY created_at ASC;
            """;
        command.Parameters.AddWithValue("$normalized_term", normalizedTerm);
        command.Parameters.AddWithValue("$language", language);
        command.Parameters.AddWithValue("$entry_kind", ToEntryKindValue(entryKind));

        var result = new List<WordRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(ReadWord(reader));
        }

        return result;
    }

    private static async Task<SavedDefinition?> LoadDefinitionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid definitionId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id, word_id, part_of_speech, definition_zh, definition_en, example,
                   sort_order, source_kind, status, source_detail, model_name,
                   prompt_version, generated_at, confirmed_at, created_at, updated_at
            FROM definitions
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", definitionId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadDefinition(reader) : null;
    }

    private static SavedDefinition ReadDefinition(SqliteDataReader reader) =>
        new(
            Id: Guid.Parse(reader.GetString(0)),
            WordId: Guid.Parse(reader.GetString(1)),
            PartOfSpeech: NullableString(reader, 2),
            DefinitionZh: NullableString(reader, 3),
            DefinitionEn: NullableString(reader, 4),
            Example: NullableString(reader, 5),
            SortOrder: reader.GetInt32(6),
            SourceKind: ParseSourceKind(reader.GetString(7)),
            Status: ParseDefinitionStatus(reader.GetString(8)),
            SourceDetail: NullableString(reader, 9),
            ModelName: NullableString(reader, 10),
            PromptVersion: NullableString(reader, 11),
            GeneratedAt: NullableTimestamp(reader, 12),
            ConfirmedAt: NullableTimestamp(reader, 13),
            CreatedAt: NullableTimestamp(reader, 14) ?? DateTimeOffset.UnixEpoch,
            UpdatedAt: NullableTimestamp(reader, 15) ?? DateTimeOffset.UnixEpoch);

    private static object DbValue(string? value) => string.IsNullOrWhiteSpace(value) ? DBNull.Value : value.Trim();

    private static string? NullableString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static DateTimeOffset? NullableTimestamp(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : ParseTimestamp(reader.GetString(ordinal));

    private static DefinitionSourceKind ParseSourceKind(string value) => value.ToLowerInvariant() switch
    {
        "manual" => DefinitionSourceKind.Manual,
        "llm" => DefinitionSourceKind.LanguageModel,
        _ => throw new InvalidDataException($"Unsupported definition source: {value}")
    };

    private static string ToSourceKindValue(DefinitionSourceKind sourceKind) => sourceKind switch
    {
        DefinitionSourceKind.Manual => "manual",
        DefinitionSourceKind.LanguageModel => "llm",
        _ => throw new ArgumentOutOfRangeException(nameof(sourceKind), sourceKind, "Unsupported definition source.")
    };

    private static DefinitionStatus ParseDefinitionStatus(string value) => value.ToLowerInvariant() switch
    {
        "proposed" => DefinitionStatus.Proposed,
        "accepted" => DefinitionStatus.Accepted,
        "rejected" => DefinitionStatus.Rejected,
        _ => throw new InvalidDataException($"Unsupported definition status: {value}")
    };

    private static async Task<ReviewWord?> LoadWordForReviewAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid wordId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id, term, normalized_term, language, entry_kind, sense_key,
                   mastery_score, mastery_level, stability_days, evidence_points_tenths,
                   lapse_count, success_streak, review_interval_days, next_review_at,
                   last_review_at, first_reviewed_at, last_feedback, algorithm_version,
                   encounter_count, is_suspended, created_at, updated_at
            FROM words
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", wordId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var word = new WordRecord(
            Id: Guid.Parse(reader.GetString(0)),
            Term: reader.GetString(1),
            NormalizedTerm: reader.GetString(2),
            Language: reader.GetString(3),
            EntryKind: ParseEntryKind(reader.GetString(4)),
            SenseKey: reader.IsDBNull(5) ? null : reader.GetString(5),
            MasteryScore: reader.GetInt32(6),
            MasteryLevel: reader.GetInt32(7),
            EncounterCount: reader.GetInt32(18),
            IsSuspended: reader.GetInt32(19) != 0,
            CreatedAt: ParseTimestamp(reader.GetString(20)),
            UpdatedAt: ParseTimestamp(reader.GetString(21)));
        var state = new MasteryState(
            Score: reader.GetInt32(6),
            Level: reader.GetInt32(7),
            StabilityDays: reader.GetDouble(8),
            EvidencePointsTenths: reader.GetInt32(9),
            LapseCount: reader.GetInt32(10),
            SuccessStreak: reader.GetInt32(11),
            ReviewIntervalDays: reader.GetDouble(12),
            NextReviewAt: reader.IsDBNull(13) ? null : ParseTimestamp(reader.GetString(13)),
            LastReviewedAt: reader.IsDBNull(14) ? null : ParseTimestamp(reader.GetString(14)),
            FirstReviewedAt: reader.IsDBNull(15) ? null : ParseTimestamp(reader.GetString(15)),
            LastFeedback: reader.IsDBNull(16) ? null : ParseFeedback(reader.GetString(16)),
            AlgorithmVersion: reader.GetString(17));
        return new ReviewWord(word, state);
    }

    private static async Task UpdateMasteryAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid wordId,
        MasteryState state,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE words SET
                mastery_score = $score,
                mastery_level = $level,
                stability_days = $stability,
                evidence_points_tenths = $evidence,
                lapse_count = $lapses,
                success_streak = $streak,
                review_interval_days = $interval,
                next_review_at = $next_review,
                last_review_at = $last_review,
                first_reviewed_at = $first_review,
                last_feedback = $last_feedback,
                algorithm_version = $algorithm_version,
                updated_at = $updated_at
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$score", state.Score);
        command.Parameters.AddWithValue("$level", state.Level);
        command.Parameters.AddWithValue("$stability", state.StabilityDays);
        command.Parameters.AddWithValue("$evidence", state.EvidencePointsTenths);
        command.Parameters.AddWithValue("$lapses", state.LapseCount);
        command.Parameters.AddWithValue("$streak", state.SuccessStreak);
        command.Parameters.AddWithValue("$interval", state.ReviewIntervalDays);
        command.Parameters.AddWithValue("$next_review", (object?)ToTimestamp(state.NextReviewAt) ?? DBNull.Value);
        command.Parameters.AddWithValue("$last_review", (object?)ToTimestamp(state.LastReviewedAt) ?? DBNull.Value);
        command.Parameters.AddWithValue("$first_review", (object?)ToTimestamp(state.FirstReviewedAt) ?? DBNull.Value);
        command.Parameters.AddWithValue("$last_feedback", (object?)state.LastFeedback?.ToString().ToUpperInvariant() ?? DBNull.Value);
        command.Parameters.AddWithValue("$algorithm_version", state.AlgorithmVersion);
        command.Parameters.AddWithValue("$updated_at", ToTimestamp(state.LastReviewedAt ?? DateTimeOffset.UtcNow));
        command.Parameters.AddWithValue("$id", wordId.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertReviewEventAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid wordId,
        MasteryEvaluation evaluation,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO review_events (
                id, word_id, feedback, score_before, score_after, level_before, level_after,
                stability_before, stability_after, evidence_before_tenths, evidence_after_tenths,
                interval_before_days, interval_after_days, scheduled_due_at, actual_elapsed_days,
                reviewed_at, algorithm_version, used_hint)
            VALUES ($id, $word_id, $feedback, $score_before, $score_after, $level_before, $level_after,
                    $stability_before, $stability_after, $evidence_before, $evidence_after,
                    $interval_before, $interval_after, $scheduled_due, $elapsed,
                    $reviewed_at, $algorithm_version, $used_hint);
            """;
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
        command.Parameters.AddWithValue("$word_id", wordId.ToString("D"));
        command.Parameters.AddWithValue("$feedback", evaluation.Feedback.ToString().ToUpperInvariant());
        command.Parameters.AddWithValue("$score_before", evaluation.Before.Score);
        command.Parameters.AddWithValue("$score_after", evaluation.After.Score);
        command.Parameters.AddWithValue("$level_before", evaluation.Before.Level);
        command.Parameters.AddWithValue("$level_after", evaluation.After.Level);
        command.Parameters.AddWithValue("$stability_before", evaluation.Before.StabilityDays);
        command.Parameters.AddWithValue("$stability_after", evaluation.After.StabilityDays);
        command.Parameters.AddWithValue("$evidence_before", evaluation.Before.EvidencePointsTenths);
        command.Parameters.AddWithValue("$evidence_after", evaluation.After.EvidencePointsTenths);
        command.Parameters.AddWithValue("$interval_before", evaluation.Before.ReviewIntervalDays);
        command.Parameters.AddWithValue("$interval_after", evaluation.After.ReviewIntervalDays);
        command.Parameters.AddWithValue("$scheduled_due", (object?)ToTimestamp(evaluation.Before.NextReviewAt) ?? DBNull.Value);
        command.Parameters.AddWithValue("$elapsed", evaluation.ActualElapsedDays);
        command.Parameters.AddWithValue("$reviewed_at", ToTimestamp(evaluation.ReviewedAt));
        command.Parameters.AddWithValue("$algorithm_version", evaluation.After.AlgorithmVersion);
        command.Parameters.AddWithValue("$used_hint", evaluation.UsedHint ? 1 : 0);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertWordAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        WordRecord word,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO words (
                id, term, normalized_term, language, entry_kind, sense_key,
                mastery_score, mastery_level, encounter_count, is_suspended,
                created_at, updated_at)
            VALUES ($id, $term, $normalized_term, $language, $entry_kind, $sense_key,
                    $mastery_score, $mastery_level, $encounter_count, $is_suspended,
                    $created_at, $updated_at);
            """;
        command.Parameters.AddWithValue("$id", word.Id.ToString("D"));
        command.Parameters.AddWithValue("$term", word.Term);
        command.Parameters.AddWithValue("$normalized_term", word.NormalizedTerm);
        command.Parameters.AddWithValue("$language", word.Language);
        command.Parameters.AddWithValue("$entry_kind", ToEntryKindValue(word.EntryKind));
        command.Parameters.AddWithValue("$sense_key", (object?)word.SenseKey ?? DBNull.Value);
        command.Parameters.AddWithValue("$mastery_score", word.MasteryScore);
        command.Parameters.AddWithValue("$mastery_level", word.MasteryLevel);
        command.Parameters.AddWithValue("$encounter_count", word.EncounterCount);
        command.Parameters.AddWithValue("$is_suspended", word.IsSuspended ? 1 : 0);
        command.Parameters.AddWithValue("$created_at", word.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$updated_at", word.UpdatedAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task UpdateEncounterCountAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        WordRecord word,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE words SET encounter_count = $count, updated_at = $updated_at WHERE id = $id;";
        command.Parameters.AddWithValue("$count", word.EncounterCount);
        command.Parameters.AddWithValue("$updated_at", word.UpdatedAt.ToString("O"));
        command.Parameters.AddWithValue("$id", word.Id.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task AddEncounterAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid wordId,
        NewWordCapture capture,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO encounters (
                id, word_id, sentence, source_application, source_window_title, encountered_at)
            VALUES ($id, $word_id, $sentence, $source_application, $source_window_title, $encountered_at);
            """;
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
        command.Parameters.AddWithValue("$word_id", wordId.ToString("D"));
        command.Parameters.AddWithValue("$sentence", (object?)capture.Sentence ?? DBNull.Value);
        command.Parameters.AddWithValue("$source_application", (object?)capture.SourceApplication ?? DBNull.Value);
        command.Parameters.AddWithValue("$source_window_title", (object?)capture.SourceWindowTitle ?? DBNull.Value);
        command.Parameters.AddWithValue("$encountered_at", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> DeleteNewWordAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid wordId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM words
            WHERE id = $id
              AND encounter_count = 1
              AND NOT EXISTS (SELECT 1 FROM review_events WHERE word_id = words.id)
              AND NOT EXISTS (SELECT 1 FROM definitions WHERE word_id = words.id);
            """;
        command.Parameters.AddWithValue("$id", wordId.ToString("D"));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    private static async Task<bool> DeleteLatestEncounterAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid wordId,
        CancellationToken cancellationToken)
    {
        await using var deleteCommand = connection.CreateCommand();
        deleteCommand.Transaction = transaction;
        deleteCommand.CommandText = """
            DELETE FROM encounters
            WHERE id = (
                SELECT id FROM encounters
                WHERE word_id = $word_id
                ORDER BY encountered_at DESC
                LIMIT 1
            );
            """;
        deleteCommand.Parameters.AddWithValue("$word_id", wordId.ToString("D"));
        if (await deleteCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            return false;
        }

        await using var updateCommand = connection.CreateCommand();
        updateCommand.Transaction = transaction;
        updateCommand.CommandText = """
            UPDATE words
            SET encounter_count = MAX(0, encounter_count - 1),
                updated_at = $updated_at
            WHERE id = $id;
            """;
        updateCommand.Parameters.AddWithValue("$updated_at", DateTimeOffset.UtcNow.ToString("O"));
        updateCommand.Parameters.AddWithValue("$id", wordId.ToString("D"));
        await updateCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static WordRecord ReadWord(SqliteDataReader reader)
    {
        return new WordRecord(
            Id: Guid.Parse(reader.GetString(0)),
            Term: reader.GetString(1),
            NormalizedTerm: reader.GetString(2),
            Language: reader.GetString(3),
            EntryKind: ParseEntryKind(reader.GetString(4)),
            SenseKey: reader.IsDBNull(5) ? null : reader.GetString(5),
            MasteryScore: reader.GetInt32(6),
            MasteryLevel: reader.GetInt32(7),
            EncounterCount: reader.GetInt32(8),
            IsSuspended: reader.GetInt32(9) != 0,
            CreatedAt: DateTimeOffset.Parse(reader.GetString(10), null, System.Globalization.DateTimeStyles.RoundtripKind),
            UpdatedAt: DateTimeOffset.Parse(reader.GetString(11), null, System.Globalization.DateTimeStyles.RoundtripKind));
    }

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.Parse(value, null, System.Globalization.DateTimeStyles.RoundtripKind);

    private static ReviewFeedback ParseFeedback(string value) => value.ToUpperInvariant() switch
    {
        "AGAIN" => ReviewFeedback.Again,
        "HARD" => ReviewFeedback.Hard,
        "GOOD" => ReviewFeedback.Good,
        "EASY" => ReviewFeedback.Easy,
        _ => throw new InvalidDataException($"Unsupported review feedback stored in database: {value}")
    };

    private static string? ToTimestamp(DateTimeOffset? value) => value?.ToUniversalTime().ToString("O");

    private static string ToEntryKindValue(EntryKind entryKind) => entryKind switch
    {
        EntryKind.Word => "word",
        EntryKind.Phrase => "phrase",
        _ => throw new ArgumentOutOfRangeException(nameof(entryKind), entryKind, "Unsupported entry kind.")
    };

    private static EntryKind ParseEntryKind(string value) => value switch
    {
        "word" => EntryKind.Word,
        "phrase" => EntryKind.Phrase,
        _ => throw new InvalidDataException($"Unsupported entry kind stored in database: {value}")
    };

    private sealed record ReviewWord(WordRecord Word, MasteryState State);
}
