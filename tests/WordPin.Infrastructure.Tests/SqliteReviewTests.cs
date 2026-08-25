using WordPin.Domain;
using WordPin.Infrastructure.Learning;

namespace WordPin.Infrastructure.Tests;

public sealed class SqliteReviewTests
{
    [Fact]
    public async Task ReviewPersistsAggregateStateAndImmutableEvent()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"wordpin-review-test-{Guid.NewGuid():N}.db");
        try
        {
            await using (var database = new SqliteLearningDatabase(databasePath))
            {
                var repository = new SqliteWordRepository(database);
                var captured = await repository.CaptureAsync(new NewWordCapture("review"));
                var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

                var first = await repository.ReviewAsync(captured.Word.Id, ReviewFeedback.Good, start);
                var second = await repository.ReviewAsync(captured.Word.Id, ReviewFeedback.Easy, start.AddDays(2));

                Assert.Equal(1, first.Word.MasteryLevel);
                Assert.True(second.Word.MasteryScore > first.Word.MasteryScore);
                Assert.Equal(25, second.Evaluation.After.EvidencePointsTenths);
                Assert.Equal(2, second.Evaluation.After.Level);

                await using var connection = await database.OpenConnectionAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM review_events WHERE word_id = $word_id;";
                command.Parameters.AddWithValue("$word_id", captured.Word.Id.ToString("D"));
                Assert.Equal(2L, await command.ExecuteScalarAsync());
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
