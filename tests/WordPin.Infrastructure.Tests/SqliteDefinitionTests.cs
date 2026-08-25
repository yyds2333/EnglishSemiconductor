using WordPin.Application;
using WordPin.Domain;
using WordPin.Infrastructure.Dictionary;
using WordPin.Infrastructure.Learning;

namespace WordPin.Infrastructure.Tests;

public sealed class SqliteDefinitionTests
{
    [Fact]
    public async Task ManualDefinitionCanBeSavedReadAndDeleted()
    {
        var databasePath = CreateDatabasePath("definition");
        try
        {
            await using var database = new SqliteLearningDatabase(databasePath);
            var repository = new SqliteWordRepository(database);
            var captured = await repository.CaptureAsync(new NewWordCapture("harvest"));

            var saved = await repository.SaveAsync(new DefinitionDraft(
                WordId: captured.Word.Id,
                PartOfSpeech: null,
                DefinitionZh: "收获；收集",
                DefinitionEn: "to gather a crop",
                Example: "They harvest rice in autumn."));

            Assert.Equal(DefinitionSourceKind.Manual, saved.SourceKind);
            Assert.Equal(DefinitionStatus.Accepted, saved.Status);
            var definitions = await repository.GetForWordAsync(captured.Word.Id);
            Assert.Single(definitions);
            Assert.Equal("收获；收集", definitions[0].DefinitionZh);

            Assert.True(await repository.DeleteAsync(saved.Id));
            Assert.Empty(await repository.GetForWordAsync(captured.Word.Id));
        }
        finally
        {
            DeleteDatabaseFiles(databasePath);
        }
    }

    [Fact]
    public async Task ResolverGeneratesOneCandidateAndReusesItForTwentyFourHours()
    {
        var databasePath = CreateDatabasePath("resolver");
        var dictionaryPath = Path.Combine(Path.GetTempPath(), $"wordpin-dictionary-{Guid.NewGuid():N}.db");
        try
        {
            await using var database = new SqliteLearningDatabase(databasePath);
            await using var dictionary = new SqliteDictionaryStore(dictionaryPath);
            var repository = new SqliteWordRepository(database);
            var captured = await repository.CaptureAsync(new NewWordCapture("sonder"));
            var provider = new FakeLanguageModelProvider();
            var resolver = new DefinitionResolver(
                repository,
                dictionary,
                provider,
                new SqliteLlmUsageStore(database));

            var first = await resolver.ResolveAsync(captured.Word);
            var second = await resolver.ResolveAsync(captured.Word);

            Assert.True(first.IsRemoteCandidate);
            Assert.Single(first.Definitions);
            Assert.Equal(DefinitionStatus.Proposed, first.Definitions[0].Status);
            Assert.Equal(first.Definitions[0].Id, second.Definitions[0].Id);
            Assert.Equal(1, provider.CallCount);
        }
        finally
        {
            DeleteDatabaseFiles(databasePath);
            DeleteDatabaseFiles(dictionaryPath);
        }
    }

    [Fact]
    public async Task ResolverUsesFreeTranslationBeforeLanguageModel()
    {
        var databasePath = CreateDatabasePath("translation-resolver");
        var dictionaryPath = Path.Combine(Path.GetTempPath(), $"wordpin-dictionary-{Guid.NewGuid():N}.db");
        try
        {
            await using var database = new SqliteLearningDatabase(databasePath);
            await using var dictionary = new SqliteDictionaryStore(dictionaryPath);
            var repository = new SqliteWordRepository(database);
            var captured = await repository.CaptureAsync(new NewWordCapture("microchip"));
            var translation = new FakeTranslationProvider();
            var model = new FakeLanguageModelProvider { Enabled = false };
            var resolver = new DefinitionResolver(
                repository,
                dictionary,
                translation,
                model,
                new SqliteLlmUsageStore(database));

            var result = await resolver.ResolveAsync(captured.Word);

            Assert.True(result.IsRemoteCandidate);
            Assert.Equal(DefinitionSourceKind.TranslationApi, result.Definitions[0].SourceKind);
            Assert.Equal(1, translation.CallCount);
            Assert.Equal(0, model.CallCount);
        }
        finally
        {
            DeleteDatabaseFiles(databasePath);
            DeleteDatabaseFiles(dictionaryPath);
        }
    }

    private static string CreateDatabasePath(string suffix) =>
        Path.Combine(Path.GetTempPath(), $"wordpin-{suffix}-{Guid.NewGuid():N}.db");

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

    private sealed class FakeLanguageModelProvider : ILanguageModelDefinitionProvider
    {
        public int CallCount { get; private set; }

        public bool Enabled { get; init; } = true;

        public bool IsConfigured => Enabled;

        public Task<GeneratedDefinitionCandidate> GenerateAsync(
            DefinitionGenerationRequest request,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(new GeneratedDefinitionCandidate(
                Term: request.Term,
                Phonetic: null,
                PartOfSpeech: "noun",
                DefinitionZh: "一种独特的内在体验",
                DefinitionEn: "a unique inner experience",
                Example: null,
                ProviderId: "fake",
                ModelName: "fake-model",
                PromptVersion: "test-v1"));
        }
    }

    private sealed class FakeTranslationProvider : ITranslationDefinitionProvider
    {
        public int CallCount { get; private set; }

        public bool IsConfigured => true;

        public Task<GeneratedDefinitionCandidate?> TranslateAsync(
            DefinitionGenerationRequest request,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult<GeneratedDefinitionCandidate?>(new GeneratedDefinitionCandidate(
                Term: request.Term,
                Phonetic: null,
                PartOfSpeech: null,
                DefinitionZh: "微型芯片",
                DefinitionEn: request.Term,
                Example: null,
                ProviderId: "fake-translation",
                ModelName: "fake-memory",
                PromptVersion: "test-v1"));
        }
    }
}
