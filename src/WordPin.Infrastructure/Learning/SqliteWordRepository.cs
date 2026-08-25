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
}
