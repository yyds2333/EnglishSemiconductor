using WordPin.Infrastructure.Dictionary;

namespace WordPin.Infrastructure.Tests;

public sealed class SqliteDictionaryImportServiceTests
{
    [Fact]
    public async Task ImportsCsvIntoActiveDatabaseAndReplacesExistingEntry()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"wordpin-import-{Guid.NewGuid():N}");
        var csvPath = Path.Combine(directory, "dictionary.csv");
        var databasePath = Path.Combine(directory, "dictionary.db");
        Directory.CreateDirectory(directory);
        try
        {
            await File.WriteAllTextAsync(
                csvPath,
                "word,translation,definition,pos\nlearn,学习,acquire knowledge,v\n");
            var service = new SqliteDictionaryImportService(databasePath);

            var result = await service.ImportCsvAsync(csvPath, "test-v1");
            await using var store = new SqliteDictionaryStore(databasePath);
            var entry = await store.LookupAsync("LEARN", "en");

            Assert.Equal(1, result.ImportedEntries);
            Assert.NotNull(entry);
            Assert.Equal("学习", entry!.Translation);
            Assert.Equal("test-v1", entry.ProviderVersion);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
