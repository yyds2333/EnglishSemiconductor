using System.Net;
using System.Net.Http.Headers;
using WordPin.Application;
using WordPin.Infrastructure.Dictionary;

namespace WordPin.Infrastructure.Tests;

public sealed class OpenAiCompatibleDefinitionProviderTests
{
    [Fact]
    public async Task ParsesStructuredCandidateAndSendsBearerToken()
    {
        var settings = new TestSettingsStore(new LlmSettings(
            Enabled: true,
            BaseUrl: "https://llm.example.test/v1",
            Model: "test-model",
            ApiKey: "secret-key"));
        HttpRequestMessage? request = null;
        using var client = new HttpClient(new StubHandler(message =>
        {
            request = message;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"{\\\"term\\\":\\\"learn\\\",\\\"phonetic\\\":null,\\\"part_of_speech\\\":\\\"verb\\\",\\\"definition_zh\\\":\\\"学习\\\",\\\"definition_en\\\":\\\"to acquire knowledge\\\",\\\"example\\\":\\\"I learn every day.\\\"}\"}}]}")
            };
        }));
        using var provider = new OpenAiCompatibleDefinitionProvider(settings, client);

        var candidate = await provider.GenerateAsync(new DefinitionGenerationRequest("learn", "en"));

        Assert.Equal("学习", candidate.DefinitionZh);
        Assert.Equal("verb", candidate.PartOfSpeech);
        Assert.Equal("test-model", candidate.ModelName);
        Assert.NotNull(request);
        Assert.Equal(HttpMethod.Post, request!.Method);
        Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
        Assert.Equal("secret-key", request.Headers.Authorization?.Parameter);
    }

    [Fact]
    public async Task RejectsNonHttpsEndpoint()
    {
        var settings = new TestSettingsStore(new LlmSettings(
            Enabled: true,
            BaseUrl: "http://localhost:1234/v1",
            Model: "test-model",
            ApiKey: "secret-key"));
        using var provider = new OpenAiCompatibleDefinitionProvider(settings, new HttpClient());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.GenerateAsync(new DefinitionGenerationRequest("learn", "en")));
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
