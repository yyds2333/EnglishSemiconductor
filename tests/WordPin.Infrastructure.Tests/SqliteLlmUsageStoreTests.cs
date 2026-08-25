using WordPin.Infrastructure.Learning;

namespace WordPin.Infrastructure.Tests;

public sealed class SqliteLlmUsageStoreTests
{
    [Fact]
    public async Task DailyLimitAllowsOnlyConfiguredNumberOfRequests()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"wordpin-llm-usage-{Guid.NewGuid():N}.db");
        try
        {
            await using var database = new SqliteLearningDatabase(databasePath);
            var store = new SqliteLlmUsageStore(database);
            var date = new DateOnly(2026, 8, 25);

            Assert.True(await store.TryConsumeAsync(date, 1));
            Assert.False(await store.TryConsumeAsync(date, 1));
            Assert.True(await store.TryConsumeAsync(date.AddDays(1), 1));
        }
        finally
        {
            foreach (var path in new[] { databasePath, databasePath + "-shm", databasePath + "-wal" })
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }
    }
}
