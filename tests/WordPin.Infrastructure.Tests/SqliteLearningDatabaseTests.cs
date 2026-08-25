using Microsoft.Data.Sqlite;
using WordPin.Domain;
using WordPin.Infrastructure.Learning;

namespace WordPin.Infrastructure.Tests;

public sealed class SqliteLearningDatabaseTests
{
    [Fact]
    public async Task AppliesMigrationAndEnablesWalAndForeignKeys()
    {
        var databasePath = CreateDatabasePath();
        try
        {
            await using (var database = new SqliteLearningDatabase(databasePath))
            {
                await database.InitializeAsync();

                await using (var connection = await database.OpenConnectionAsync())
                await using (var command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT MAX(version) FROM schema_migrations;";
                    Assert.Equal(4L, await command.ExecuteScalarAsync());

                    command.CommandText = "PRAGMA foreign_keys;";
                    Assert.Equal(1L, await command.ExecuteScalarAsync());

                    command.CommandText = "PRAGMA journal_mode;";
                    Assert.Equal("wal", (await command.ExecuteScalarAsync())?.ToString(), ignoreCase: true);
                }
            }
        }
        finally
        {
            DeleteDatabaseFiles(databasePath);
        }
    }

    [Fact]
    public async Task CaptureReusesExactSenseAndDoesNotMergeDifferentSense()
    {
        var databasePath = CreateDatabasePath();
        try
        {
            await using (var database = new SqliteLearningDatabase(databasePath))
            {
                var repository = new SqliteWordRepository(database);

                var first = await repository.CaptureAsync(new NewWordCapture("  Lead  "));
                var repeated = await repository.CaptureAsync(new NewWordCapture("lead"));
                var verb = await repository.CaptureAsync(new NewWordCapture("lead", SenseKey: "verb"));
                var ambiguous = await repository.CaptureAsync(new NewWordCapture("LEAD", SenseKey: "noun"));

                Assert.True(first.IsNew);
                Assert.Equal("Lead", first.Word.Term);
                Assert.False(repeated.IsNew);
                Assert.Equal(first.Word.Id, repeated.Word.Id);
                Assert.Equal(2, repeated.Word.EncounterCount);
                Assert.True(verb.IsNew);
                Assert.NotEqual(first.Word.Id, verb.Word.Id);
                Assert.True(ambiguous.RequiresSenseSelection);
                Assert.False(ambiguous.IsNew);
                Assert.Equal(2, ambiguous.Candidates.Count);
            }
        }
        finally
        {
            DeleteDatabaseFiles(databasePath);
        }
    }

    [Fact]
    public void NormalizerPreservesPhraseStructureAndFoldsCase()
    {
        Assert.Equal("Take care of", TermNormalizer.NormalizeDisplay("  Take\tcare  of "));
        Assert.Equal("take care of", TermNormalizer.NormalizeLookup("  Take\tcare  of "));
    }

    [Fact]
    public async Task UndoRemovesOnlyTheLatestCapture()
    {
        var databasePath = CreateDatabasePath();
        try
        {
            await using (var database = new SqliteLearningDatabase(databasePath))
            {
                var repository = new SqliteWordRepository(database);
                var first = await repository.CaptureAsync(new NewWordCapture("retain"));
                var repeated = await repository.CaptureAsync(new NewWordCapture("retain"));

                Assert.True(await repository.UndoLastCaptureAsync(repeated));
                var candidates = await repository.FindCandidatesAsync("retain");
                Assert.Single(candidates);
                Assert.Equal(1, candidates[0].EncounterCount);

                Assert.True(await repository.UndoLastCaptureAsync(first));
                Assert.Empty(await repository.FindCandidatesAsync("retain"));
            }
        }
        finally
        {
            DeleteDatabaseFiles(databasePath);
        }
    }

    private static string CreateDatabasePath() =>
        Path.Combine(Path.GetTempPath(), $"wordpin-learning-test-{Guid.NewGuid():N}.db");

    private static void DeleteDatabaseFiles(string databasePath)
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
