using System.Net.Http.Headers;
using Agentstration.Aep.Abstractions;
using Agentstration.Extensions.LocalAI;

namespace Agentstration.ModelProviders.Tests;

[TestClass]
[TestCategory("Integration")]
public sealed class LocalAiIntegrationTests
{
    [TestMethod]
    public async Task ExistingServerSupportsHealthDiscoveryAndChat()
    {
        var endpointText = Environment.GetEnvironmentVariable("AGENTSTRATION_LOCALAI_ENDPOINT");
        var model = Environment.GetEnvironmentVariable("AGENTSTRATION_LOCALAI_MODEL");
        if (!Uri.TryCreate(endpointText, UriKind.Absolute, out var endpoint) || string.IsNullOrWhiteSpace(model))
        {
            Assert.Inconclusive("Set AGENTSTRATION_LOCALAI_ENDPOINT and AGENTSTRATION_LOCALAI_MODEL to run the optional LocalAI integration test.");
        }

        using var httpClient = new HttpClient { BaseAddress = new Uri(endpoint.AbsoluteUri.TrimEnd('/') + '/'), Timeout = TimeSpan.FromMinutes(2) };
        var apiKey = Environment.GetEnvironmentVariable("AGENTSTRATION_LOCALAI_API_KEY");
        if (!string.IsNullOrWhiteSpace(apiKey)) httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        var provider = new LocalAiAepModelProvider(httpClient);

        var health = await provider.GetHealthAsync();
        var models = await provider.ListModelsAsync();
        var response = await provider.ChatAsync(
            new AepChatRequest(model, [new AepMessage(AepRole.User, [AepContent.FromText("Reply with OK.")])]),
            default);
        var streamedText = new List<string>();
        await foreach (var update in provider.ChatStreamingAsync(
                           new AepChatRequest(model, [new AepMessage(AepRole.User, [AepContent.FromText("Reply with STREAM OK.")])]),
                           default))
        {
            streamedText.AddRange(update.Contents.Where(value => value.Text is not null).Select(value => value.Text!));
        }

        Assert.AreEqual("available", health.Status);
        Assert.IsTrue(models.Any(value => string.Equals(value.Id, model, StringComparison.Ordinal)));
        Assert.IsNotEmpty(response.Messages.SelectMany(value => value.Contents).Where(value => value.Text is not null).Select(value => value.Text).ToArray());
        Assert.IsNotEmpty(streamedText);
    }
}
