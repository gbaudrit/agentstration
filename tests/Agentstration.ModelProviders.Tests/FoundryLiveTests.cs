using System.Text.Json;
using Agentstration.Aep.Abstractions;
using Agentstration.Extensions.Foundry;

namespace Agentstration.ModelProviders.Tests;

[TestClass]
[DoNotParallelize]
public sealed class FoundryLiveTests
{
    [TestMethod]
    [DataRow("ApiKeyEnvironment")]
    [DataRow("Development")]
    [DataRow("ManagedIdentity")]
    [DataRow("WorkloadIdentity")]
    public async Task ExistingDeploymentSupportsDiscoveryChatAndStream(string authenticationMode)
    {
        using var live = Open(authenticationMode);
        var models = await live.Provider.ListModelsAsync();
        Assert.IsTrue(models.Any(model => model.Id == live.Deployment), "The selected deployment was not discovered.");

        var request = new AepChatRequest(live.Deployment,
            [new AepMessage(AepRole.User, [AepContent.FromText("Reply with one short word.")])],
            new AepModelOptions { MaxOutputTokens = 64 });
        var response = await live.Provider.ChatAsync(request, default);
        Assert.IsNotNull(response.FinishReason);

        var completed = false;
        await foreach (var update in live.Provider.ChatStreamingAsync(request, default))
            completed |= update.FinishReason is not null;
        Assert.IsTrue(completed, "The Foundry stream did not finish.");
    }

    [TestMethod]
    [DataRow("ApiKeyEnvironment")]
    [DataRow("Development")]
    [DataRow("ManagedIdentity")]
    [DataRow("WorkloadIdentity")]
    public async Task ExistingToolCapableDeploymentAcceptsGovernedToolRoundTrip(string authenticationMode)
    {
        using var live = Open(authenticationMode);
        var model = (await live.Provider.ListModelsAsync()).SingleOrDefault(value => value.Id == live.Deployment);
        if (model?.Capabilities?.Contains("tools") != true)
            Assert.Inconclusive("The selected deployment does not affirmatively advertise Tools.");

        var request = new AepChatRequest(live.Deployment,
        [
            new AepMessage(AepRole.User, [AepContent.FromText("Use the provided result and reply briefly.")]),
            new AepMessage(AepRole.Assistant, [new AepContent { Kind = AepContentKind.ToolCall,
                ToolCall = new("live-echo-1", "echo", JsonSerializer.SerializeToElement(new { value = "ping" })) }]),
            new AepMessage(AepRole.Tool, [new AepContent { Kind = AepContentKind.ToolResult,
                ToolResult = new("live-echo-1", JsonSerializer.SerializeToElement("pong")) }])
        ],
            new AepModelOptions { MaxOutputTokens = 64 },
            [new AepToolDefinition("echo", "Return the supplied text", JsonSerializer.SerializeToElement(new
            {
                type = "object", properties = new { value = new { type = "string" } }, required = new[] { "value" }
            }))]);
        var completed = false;
        await foreach (var update in live.Provider.ChatStreamingAsync(request, default))
            completed |= update.FinishReason is not null;
        Assert.IsTrue(completed, "The tool-result continuation did not finish.");
    }

    private static LiveProvider Open(string authenticationMode)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("AGENTSTRATION_FOUNDRY_LIVE"), "true", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Environment.GetEnvironmentVariable("AGENTSTRATION_FOUNDRY_LIVE_AUTHENTICATION_MODE"), authenticationMode, StringComparison.Ordinal))
            Assert.Inconclusive("Set AGENTSTRATION_FOUNDRY_LIVE=true and select one authentication mode for the optional live test.");
        var project = Environment.GetEnvironmentVariable("AGENTSTRATION_FOUNDRY_LIVE_PROJECT_ENDPOINT");
        var inference = Environment.GetEnvironmentVariable("AGENTSTRATION_FOUNDRY_LIVE_INFERENCE_ENDPOINT");
        var deployment = Environment.GetEnvironmentVariable("AGENTSTRATION_FOUNDRY_LIVE_DEPLOYMENT");
        if (string.IsNullOrWhiteSpace(project) || string.IsNullOrWhiteSpace(inference) || string.IsNullOrWhiteSpace(deployment))
            Assert.Inconclusive("Provide existing Foundry project, inference and deployment values for the optional live test.");
        var options = new FoundryExtensionOptions
        {
            ProjectEndpoint = new Uri(project, UriKind.Absolute),
            InferenceEndpoint = new Uri(inference, UriKind.Absolute),
            AuthenticationMode = Enum.Parse<FoundryAuthenticationMode>(authenticationMode),
            AllowedPrivateHosts = new HashSet<string>((Environment.GetEnvironmentVariable("AGENTSTRATION_FOUNDRY_LIVE_ALLOWED_PRIVATE_HOSTS") ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), StringComparer.OrdinalIgnoreCase),
            RequestTimeout = TimeSpan.FromSeconds(60)
        };
        options.Validate();
        var client = new HttpClient(FoundrySecureTransport.Create(options)) { Timeout = Timeout.InfiniteTimeSpan };
        return new LiveProvider(new FoundryAepModelProvider(client, options, new FoundryRequestAuthenticator(options)), client, deployment);
    }

    private sealed record LiveProvider(FoundryAepModelProvider Provider, HttpClient Client, string Deployment) : IDisposable
    {
        public void Dispose() => Client.Dispose();
    }
}
