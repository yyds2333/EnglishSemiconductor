using System.Net;
using WordPin.Application;
using WordPin.Infrastructure.Dictionary;

namespace WordPin.Infrastructure.Tests;

public sealed class MyMemoryTranslationProviderTests
{
    [Fact]
    public async Task ParsesHumanTranslationAndSendsOnlyTerm()
    {
        var settings = new TestSettingsStore(new LlmSettings(
            OnlineTranslationEnabled: true,
            OnlineMachineTranslationEnabled: false,
            OnlineTranslationBaseUrl: "https://memory.example.test"));
        Uri? requestUri = null;
        using var client = new HttpClient(new StubHandler(message =>
        {
            requestUri = message.RequestUri;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"responseStatus\":200,\"responseData\":{\"translatedText\":\"半导体\"},\"matches\":[{\"translation\":\"半导体\",\"match\":\"1\"}]}")
            };
        }));
        using var provider = new MyMemoryTranslationProvider(settings, client);

        var candidate = await provider.TranslateAsync(
            new DefinitionGenerationRequest("semiconductor", "en", "private context"));

        Assert.NotNull(candidate);
        Assert.Equal("半导体", candidate!.DefinitionZh);
        Assert.Equal("semiconductor", candidate.DefinitionEn);
        Assert.Equal("mymemory", candidate.ProviderId);
        Assert.Contains("mt=0", requestUri!.Query, StringComparison.Ordinal);
        Assert.Contains("q=semiconductor", requestUri.Query, StringComparison.Ordinal);
        Assert.DoesNotContain("private", requestUri.Query, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReturnsNullWhenHumanOnlyLookupHasNoMatch()
    {
        var settings = new TestSettingsStore(new LlmSettings(OnlineTranslationEnabled: true));
        using var client = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"responseStatus\":200,\"responseData\":{\"translatedText\":\"rareterm\"},\"matches\":[]}")
        }));
        using var provider = new MyMemoryTranslationProvider(settings, client);

        var candidate = await provider.TranslateAsync(new DefinitionGenerationRequest("rareterm", "en"));

        Assert.Null(candidate);
    }

    [Fact]
    public async Task RejectsNonHttpsEndpoint()
    {
        var settings = new TestSettingsStore(new LlmSettings(
            OnlineTranslationEnabled: true,
            OnlineTranslationBaseUrl: "http://localhost:5000"));
        using var provider = new MyMemoryTranslationProvider(settings, new HttpClient());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.TranslateAsync(new DefinitionGenerationRequest("hello", "en")));
    }

    private sealed class TestSettingsStore : ILlmSettingsStore
    {
        private readonly LlmSettings settings;

        public TestSettingsStore(LlmSettings settings) => this.settings = settings;

        public LlmSettings Load() => settings;

        public void Save(LlmSettings settings) => throw new NotSupportedException();
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> handler;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) => this.handler = handler;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(handler(request));
    }
}
