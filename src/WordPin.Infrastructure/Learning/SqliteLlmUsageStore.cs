using WordPin.Application;

namespace WordPin.Infrastructure.Learning;

public sealed class SqliteLlmUsageStore : ILlmUsageStore
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
    {
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
            INSERT INTO llm_usage(local_date, request_count, updated_at)
            VALUES ($local_date, 1, $updated_at)
            ON CONFLICT(local_date) DO UPDATE SET
                request_count = request_count + 1,
                updated_at = excluded.updated_at
            WHERE request_count < $limit;
            SELECT changes();
            """;
        command.Parameters.AddWithValue(
            "$local_date",
            localDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$updated_at", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$limit", dailyLimit);
        var changed = Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return changed == 1;
    }
}
