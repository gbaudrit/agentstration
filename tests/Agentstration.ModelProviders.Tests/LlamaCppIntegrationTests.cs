using Agentstration.Aep.Abstractions;
using Agentstration.Extensions.LlamaCpp;

namespace Agentstration.ModelProviders.Tests;

[TestClass]
[TestCategory("Integration")]
public sealed class LlamaCppIntegrationTests
{
    [TestMethod]
    public async Task ExistingServerSupportsHealthDiscoveryAndChat()
    {
        var endpointText = Environment.GetEnvironmentVariable("AGENTSTRATION_LLAMA_CPP_ENDPOINT");
        var model = Environment.GetEnvironmentVariable("AGENTSTRATION_LLAMA_CPP_MODEL");
        if (!Uri.TryCreate(endpointText, UriKind.Absolute, out var endpoint) || string.IsNullOrWhiteSpace(model))
        {
            Assert.Inconclusive("Set AGENTSTRATION_LLAMA_CPP_ENDPOINT and AGENTSTRATION_LLAMA_CPP_MODEL to run the optional llama.cpp integration test.");
        }

        using var httpClient = new HttpClient { BaseAddress = new Uri(endpoint.AbsoluteUri.TrimEnd('/') + '/'), Timeout = TimeSpan.FromMinutes(2) };
        var provider = new LlamaCppAepModelProvider(httpClient);

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
