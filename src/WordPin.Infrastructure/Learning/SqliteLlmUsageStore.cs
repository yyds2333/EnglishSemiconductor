using WordPin.Application;

namespace WordPin.Infrastructure.Learning;

public sealed class SqliteLlmUsageStore : ILlmUsageStore, IRemoteUsageStore
{
    private readonly SqliteLearningDatabase database;

    public SqliteLlmUsageStore(SqliteLearningDatabase database)
    {
        this.database = database ?? throw new ArgumentNullException(nameof(database));
    }

    public async Task<bool> TryConsumeAsync(
        DateOnly localDate,
        int dailyLimit,
        CancellationToken cancellationToken = default)
        => await TryConsumeAsync("llm", localDate, dailyLimit, cancellationToken).ConfigureAwait(false);

    public async Task<bool> TryConsumeAsync(
        string provider,
        DateOnly localDate,
        int dailyLimit,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        if (dailyLimit <= 0)
        {
            return false;
        }

        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (Microsoft.Data.Sqlite.SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO remote_usage(local_date, provider, request_count, updated_at)
            VALUES ($local_date, $provider, 1, $updated_at)
            ON CONFLICT(local_date, provider) DO UPDATE SET
                request_count = request_count + 1,
                updated_at = excluded.updated_at
            WHERE request_count < $limit;
            SELECT changes();
            """;
        command.Parameters.AddWithValue(
            "$local_date",
            localDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$provider", provider.Trim().ToLowerInvariant());
        command.Parameters.AddWithValue("$updated_at", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$limit", dailyLimit);
        var changed = Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return changed == 1;
    }
}
