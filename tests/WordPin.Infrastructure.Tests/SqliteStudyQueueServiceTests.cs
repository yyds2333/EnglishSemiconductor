using WordPin.Domain;
using WordPin.Infrastructure.Learning;

namespace WordPin.Infrastructure.Tests;

public sealed class SqliteStudyQueueServiceTests
{
    [Fact]
    public async Task CreatesMutuallyExclusiveDailySnapshotAndReusesIt()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"wordpin-queue-test-{Guid.NewGuid():N}.db");
        try
        {
            await using (var database = new SqliteLearningDatabase(databasePath))
            {
                var repository = new SqliteWordRepository(database);
                var queue = new SqliteStudyQueueService(database);
                var now = new DateTimeOffset(2026, 2, 1, 12, 0, 0, TimeSpan.Zero);

                var overdue = await repository.CaptureAsync(new NewWordCapture("overdue"));
                await repository.ReviewAsync(overdue.Word.Id, ReviewFeedback.Good, now.AddDays(-40));
                var again = await repository.CaptureAsync(new NewWordCapture("again"));
                await repository.ReviewAsync(again.Word.Id, ReviewFeedback.Again, now.AddHours(-1));
                var normal = await repository.CaptureAsync(new NewWordCapture("normal"));
                await repository.ReviewAsync(normal.Word.Id, ReviewFeedback.Good, now.AddDays(-3));
                await repository.CaptureAsync(new NewWordCapture("new"));

                var snapshot = await queue.GetOrCreateAsync("2026-02-01", now, dailyLimit: 10);
                Assert.Equal(4, snapshot.Items.Count);
                Assert.Equal(QueueCategory.OverdueCheck, snapshot.Items[0].Category);
                Assert.Equal(QueueCategory.AgainRetry, snapshot.Items[1].Category);
                Assert.Equal(QueueCategory.NormalDue, snapshot.Items[2].Category);
                Assert.Equal(QueueCategory.New, snapshot.Items[3].Category);
                Assert.Equal(4, snapshot.Items.Select(item => item.Category).Distinct().Count());

                var repeated = await queue.GetOrCreateAsync("2026-02-01", now.AddDays(1), dailyLimit: 1);
                Assert.Equal(snapshot.Id, repeated.Id);
                Assert.Equal(snapshot.Items.Select(item => item.WordId), repeated.Items.Select(item => item.WordId));
            }
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
