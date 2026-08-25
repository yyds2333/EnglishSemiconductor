using Microsoft.Data.Sqlite;
using WordPin.Application;
using WordPin.Domain;

namespace WordPin.Infrastructure.Dictionary;

/// <summary>
/// Read/write store for the versioned local dictionary database. It is kept
/// separate from the user's learning database so dictionary upgrades can be
/// replaced or rolled back independently.
/// </summary>
public sealed class SqliteDictionaryStore : IDictionaryProvider, IAsyncDisposable
{
    private readonly string databasePath;

    public SqliteDictionaryStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        this.databasePath = Path.GetFullPath(databasePath);
    }

    public string ProviderId => "ecdict";

    public bool IsOnline => false;

    public bool CanCacheNormalizedFields => true;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS dictionary_entries (
                term TEXT NOT NULL,
                language TEXT NOT NULL,
                provider_id TEXT NOT NULL,
                phonetic TEXT NULL,
                definition TEXT NULL,
                translation TEXT NULL,
                part_of_speech TEXT NULL,
                word_forms TEXT NULL,
                audio_url TEXT NULL,
                provider_version TEXT NULL,
                PRIMARY KEY (term COLLATE NOCASE, language, provider_id)
            );
            CREATE INDEX IF NOT EXISTS ix_dictionary_entries_term
                ON dictionary_entries (term COLLATE NOCASE);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ImportAsync(
        IAsyncEnumerable<DictionaryEntry> entries,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO dictionary_entries (
                term, language, provider_id, phonetic, definition, translation,
                part_of_speech, word_forms, audio_url, provider_version)
            VALUES ($term, $language, $provider_id, $phonetic, $definition, $translation,
                    $part_of_speech, $word_forms, $audio_url, $provider_version)
            ON CONFLICT(term, language, provider_id) DO UPDATE SET
                phonetic = excluded.phonetic,
                definition = excluded.definition,
                translation = excluded.translation,
                part_of_speech = excluded.part_of_speech,
                word_forms = excluded.word_forms,
                audio_url = excluded.audio_url,
                provider_version = excluded.provider_version;
            """;

        var parameters = new[]
        {
            command.Parameters.Add("$term", SqliteType.Text),
            command.Parameters.Add("$language", SqliteType.Text),
            command.Parameters.Add("$provider_id", SqliteType.Text),
            command.Parameters.Add("$phonetic", SqliteType.Text),
            command.Parameters.Add("$definition", SqliteType.Text),
            command.Parameters.Add("$translation", SqliteType.Text),
            command.Parameters.Add("$part_of_speech", SqliteType.Text),
            command.Parameters.Add("$word_forms", SqliteType.Text),
            command.Parameters.Add("$audio_url", SqliteType.Text),
            command.Parameters.Add("$provider_version", SqliteType.Text)
        };

        await foreach (var entry in entries.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            parameters[0].Value = entry.Term;
            parameters[1].Value = entry.Language;
            parameters[2].Value = entry.ProviderId;
            parameters[3].Value = (object?)entry.Phonetic ?? DBNull.Value;
            parameters[4].Value = (object?)entry.Definition ?? DBNull.Value;
            parameters[5].Value = (object?)entry.Translation ?? DBNull.Value;
            parameters[6].Value = (object?)entry.PartOfSpeech ?? DBNull.Value;
            parameters[7].Value = (object?)entry.WordForms ?? DBNull.Value;
            parameters[8].Value = (object?)entry.AudioUrl ?? DBNull.Value;
            parameters[9].Value = (object?)entry.ProviderVersion ?? DBNull.Value;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<DictionaryEntry?> LookupAsync(
        string term,
        string language,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(term);
        ArgumentException.ThrowIfNullOrWhiteSpace(language);

        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT term, language, provider_id, phonetic, definition, translation,
                   part_of_speech, word_forms, audio_url, provider_version
            FROM dictionary_entries
            WHERE term = $term COLLATE NOCASE
              AND language = $language
              AND provider_id = $provider_id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$term", term.Trim());
        command.Parameters.AddWithValue("$language", language.Trim());
        command.Parameters.AddWithValue("$provider_id", ProviderId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new DictionaryEntry(
            Term: reader.GetString(0),
            Language: reader.GetString(1),
            ProviderId: reader.GetString(2),
            Phonetic: GetNullableString(reader, 3),
            Definition: GetNullableString(reader, 4),
            Translation: GetNullableString(reader, 5),
            PartOfSpeech: GetNullableString(reader, 6),
            WordForms: GetNullableString(reader, 7),
            AudioUrl: GetNullableString(reader, 8),
            ProviderVersion: GetNullableString(reader, 9));
    }

    public async Task<long> CountAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM dictionary_entries;";
        return (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 0L);
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = false
        }.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static string? GetNullableString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
}
