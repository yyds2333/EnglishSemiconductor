using WordPin.Domain;
using WordPin.Infrastructure.Dictionary;

namespace WordPin.Infrastructure.Tests;

public sealed class SqliteDictionaryStoreTests
{
    [Fact]
    public async Task ImportsAndLooksUpCaseInsensitively()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"wordpin-test-{Guid.NewGuid():N}.db");
        try
        {
            await using var store = new SqliteDictionaryStore(databasePath);
            var entry = new DictionaryEntry(
                Term: "learn",
                Language: "en",
                ProviderId: "ecdict",
                Phonetic: "/lɜːrn/",
                Definition: "to acquire knowledge",
                Translation: "学习",
                PartOfSpeech: "v",
                WordForms: "learned;learning",
                AudioUrl: null,
                ProviderVersion: "test-1");

            await store.ImportAsync(Single(entry));

            Assert.Equal(1L, await store.CountAsync());
            var result = await store.LookupAsync("LEARN", "en");
            Assert.NotNull(result);
            Assert.Equal("学习", result.Translation);
            Assert.Equal("test-1", result.ProviderVersion);
            Assert.Null(await store.LookupAsync("unknown", "en"));
        }
        finally
        {
            DeleteIfExists(databasePath);
            DeleteIfExists(databasePath + "-shm");
            DeleteIfExists(databasePath + "-wal");
        }
    }

    private static async IAsyncEnumerable<DictionaryEntry> Single(DictionaryEntry entry)
    {
        yield return entry;
        await Task.CompletedTask;
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
