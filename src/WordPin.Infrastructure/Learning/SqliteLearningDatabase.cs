using Microsoft.Data.Sqlite;

namespace WordPin.Infrastructure.Learning;

/// <summary>
/// Owns the writable learning database and applies schema migrations before a
/// repository can use it.
/// </summary>
public sealed class SqliteLearningDatabase : IAsyncDisposable
{
    private const int CurrentSchemaVersion = 4;
    private readonly SemaphoreSlim migrationLock = new(1, 1);
    private readonly string databasePath;
    private bool initialized;

    public SqliteLearningDatabase(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        this.databasePath = Path.GetFullPath(databasePath);
    }

    public string DatabasePath => databasePath;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (initialized)
        {
            return;
        }

        await migrationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (initialized)
            {
                return;
            }

            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await ExecutePragmasAsync(connection, cancellationToken).ConfigureAwait(false);
            await EnsureMigrationsTableAsync(connection, cancellationToken).ConfigureAwait(false);

            var currentVersion = await GetSchemaVersionAsync(connection, cancellationToken).ConfigureAwait(false);
            if (currentVersion > CurrentSchemaVersion)
            {
                throw new InvalidDataException(
                    $"Database schema version {currentVersion} is newer than this application supports ({CurrentSchemaVersion}).");
            }

            if (currentVersion < 1)
            {
                await ApplyMigrationAsync(connection, 1, MigrationV1, cancellationToken).ConfigureAwait(false);
            }

            if (currentVersion < 2)
            {
                await ApplyMigrationAsync(connection, 2, MigrationV2, cancellationToken).ConfigureAwait(false);
            }

            if (currentVersion < 3)
            {
                await ApplyMigrationAsync(connection, 3, MigrationV3, cancellationToken).ConfigureAwait(false);
            }

            if (currentVersion < 4)
            {
                await ApplyMigrationAsync(connection, 4, MigrationV4, cancellationToken).ConfigureAwait(false);
            }

            initialized = true;
        }
        finally
        {
            migrationLock.Release();
        }
    }

    public async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
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
            Pooling = false,
            DefaultTimeout = 5
        }.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecutePragmasAsync(connection, cancellationToken).ConfigureAwait(false);
        return connection;
    }

    public async ValueTask DisposeAsync()
    {
        migrationLock.Dispose();
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private static async Task ExecutePragmasAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        command.CommandText = "PRAGMA journal_mode = WAL;";
        await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsureMigrationsTableAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS schema_migrations (
                version INTEGER PRIMARY KEY,
                applied_at TEXT NOT NULL
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> GetSchemaVersionAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(version), 0) FROM schema_migrations;";
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task ApplyMigrationAsync(
        SqliteConnection connection,
        int version,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        command.CommandText = "INSERT INTO schema_migrations(version, applied_at) VALUES ($version, $applied_at);";
        command.Parameters.AddWithValue("$version", version);
        command.Parameters.AddWithValue("$applied_at", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private const string MigrationV1 = """
        CREATE TABLE words (
            id TEXT PRIMARY KEY,
            term TEXT NOT NULL,
            normalized_term TEXT NOT NULL,
            language TEXT NOT NULL,
            entry_kind TEXT NOT NULL CHECK (entry_kind IN ('word', 'phrase')),
            sense_key TEXT NULL,
            lemma TEXT NULL,
            phonetic_uk TEXT NULL,
            phonetic_us TEXT NULL,
            audio_uk_url TEXT NULL,
            audio_us_url TEXT NULL,
            mastery_score INTEGER NOT NULL DEFAULT 0 CHECK (mastery_score BETWEEN 0 AND 100),
            mastery_level INTEGER NOT NULL DEFAULT 0 CHECK (mastery_level BETWEEN 0 AND 5),
            user_self_rating TEXT NULL,
            stability_days REAL NOT NULL DEFAULT 0,
            evidence_points_tenths INTEGER NOT NULL DEFAULT 0,
            lapse_count INTEGER NOT NULL DEFAULT 0,
            algorithm_version TEXT NOT NULL DEFAULT 'mvp-1',
            encounter_count INTEGER NOT NULL DEFAULT 1,
            success_streak INTEGER NOT NULL DEFAULT 0,
            review_interval_days REAL NOT NULL DEFAULT 0,
            next_review_at TEXT NULL,
            last_review_at TEXT NULL,
            is_suspended INTEGER NOT NULL DEFAULT 0 CHECK (is_suspended IN (0, 1)),
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL
        );

        CREATE INDEX ix_words_lookup ON words(normalized_term, language, entry_kind);
        CREATE INDEX ix_words_next_review ON words(next_review_at);
        CREATE INDEX ix_words_mastery ON words(mastery_level);

        CREATE TABLE word_forms (
            id TEXT PRIMARY KEY,
            word_id TEXT NOT NULL REFERENCES words(id) ON DELETE CASCADE,
            form TEXT NOT NULL,
            normalized_form TEXT NOT NULL,
            form_kind TEXT NOT NULL CHECK (form_kind IN ('surface', 'lemma', 'inflection', 'alias')),
            is_primary INTEGER NOT NULL DEFAULT 0 CHECK (is_primary IN (0, 1))
        );

        CREATE INDEX ix_word_forms_lookup ON word_forms(normalized_form);

        CREATE TABLE definitions (
            id TEXT PRIMARY KEY,
            word_id TEXT NOT NULL REFERENCES words(id) ON DELETE CASCADE,
            part_of_speech TEXT NULL,
            definition_zh TEXT NULL,
            definition_en TEXT NULL,
            example TEXT NULL,
            sort_order INTEGER NOT NULL DEFAULT 0,
            provider TEXT NULL
        );

        CREATE TABLE encounters (
            id TEXT PRIMARY KEY,
            word_id TEXT NOT NULL REFERENCES words(id) ON DELETE CASCADE,
            sentence TEXT NULL,
            source_application TEXT NULL,
            source_window_title TEXT NULL,
            encountered_at TEXT NOT NULL
        );

        CREATE INDEX ix_encounters_word_time ON encounters(word_id, encountered_at);

        CREATE TABLE review_events (
            id TEXT PRIMARY KEY,
            word_id TEXT NOT NULL REFERENCES words(id) ON DELETE CASCADE,
            session_id TEXT NULL,
            feedback TEXT NOT NULL CHECK (feedback IN ('AGAIN', 'HARD', 'GOOD', 'EASY')),
            score_before INTEGER NOT NULL,
            score_after INTEGER NOT NULL,
            level_before INTEGER NOT NULL,
            level_after INTEGER NOT NULL,
            stability_before REAL NOT NULL,
            stability_after REAL NOT NULL,
            evidence_before_tenths INTEGER NOT NULL,
            evidence_after_tenths INTEGER NOT NULL,
            interval_before_days REAL NOT NULL,
            interval_after_days REAL NOT NULL,
            scheduled_due_at TEXT NULL,
            actual_elapsed_days REAL NULL,
            reviewed_at TEXT NOT NULL,
            queue_reason TEXT NULL,
            algorithm_version TEXT NOT NULL,
            used_hint INTEGER NOT NULL DEFAULT 0 CHECK (used_hint IN (0, 1))
        );

        CREATE INDEX ix_review_events_word_time ON review_events(word_id, reviewed_at);

        CREATE TABLE study_sessions (
            id TEXT PRIMARY KEY,
            local_date TEXT NOT NULL,
            started_at TEXT NOT NULL,
            completed_at TEXT NULL,
            planned_count INTEGER NOT NULL,
            completed_count INTEGER NOT NULL DEFAULT 0
        );

        CREATE TABLE study_session_items (
            id TEXT PRIMARY KEY,
            session_id TEXT NOT NULL REFERENCES study_sessions(id) ON DELETE CASCADE,
            word_id TEXT NOT NULL REFERENCES words(id) ON DELETE CASCADE,
            queue_category TEXT NOT NULL,
            queue_reason TEXT NOT NULL,
            ordinal INTEGER NOT NULL,
            completed_at TEXT NULL,
            UNIQUE(session_id, word_id)
        );

        CREATE TABLE tags (
            id TEXT PRIMARY KEY,
            name TEXT NOT NULL COLLATE NOCASE UNIQUE
        );

        CREATE TABLE word_tags (
            word_id TEXT NOT NULL REFERENCES words(id) ON DELETE CASCADE,
            tag_id TEXT NOT NULL REFERENCES tags(id) ON DELETE CASCADE,
            PRIMARY KEY(word_id, tag_id)
        );

        CREATE TABLE settings (
            key TEXT PRIMARY KEY,
            value TEXT NULL,
            updated_at TEXT NOT NULL
        );

        CREATE TABLE llm_usage (
            local_date TEXT PRIMARY KEY,
            request_count INTEGER NOT NULL,
            updated_at TEXT NOT NULL
        );
        """;

    private const string MigrationV2 = """
        ALTER TABLE words ADD COLUMN first_reviewed_at TEXT NULL;
        ALTER TABLE words ADD COLUMN last_feedback TEXT NULL;
        """;

    private const string MigrationV3 = """
        ALTER TABLE definitions ADD COLUMN source_kind TEXT NOT NULL DEFAULT 'manual';
        ALTER TABLE definitions ADD COLUMN source_detail TEXT NULL;
        ALTER TABLE definitions ADD COLUMN model_name TEXT NULL;
        ALTER TABLE definitions ADD COLUMN prompt_version TEXT NULL;
        ALTER TABLE definitions ADD COLUMN status TEXT NOT NULL DEFAULT 'accepted';
        ALTER TABLE definitions ADD COLUMN generated_at TEXT NULL;
        ALTER TABLE definitions ADD COLUMN confirmed_at TEXT NULL;
        ALTER TABLE definitions ADD COLUMN created_at TEXT NULL;
        ALTER TABLE definitions ADD COLUMN updated_at TEXT NULL;

        CREATE INDEX IF NOT EXISTS ix_definitions_word_status
            ON definitions(word_id, status, sort_order);

        CREATE TABLE IF NOT EXISTS llm_usage (
            local_date TEXT PRIMARY KEY,
            request_count INTEGER NOT NULL,
            updated_at TEXT NOT NULL
        );
        """;

    private const string MigrationV4 = """
        CREATE TABLE IF NOT EXISTS remote_usage (
            local_date TEXT NOT NULL,
            provider TEXT NOT NULL,
            request_count INTEGER NOT NULL,
            updated_at TEXT NOT NULL,
            PRIMARY KEY(local_date, provider)
        );
        """;
}
