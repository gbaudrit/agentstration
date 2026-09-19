using System.Net.Http.Json;
using System.Text.Json;
using Agentstration.Aep.Abstractions;
using Agentstration.Extensions.Foundry;

namespace Agentstration.ModelProviders.Tests;

[TestClass]
[DoNotParallelize]
public sealed class FoundryLiveTests
{
    [TestMethod]
    [DataRow("ApiKey")]
    [DataRow("Development")]
    [DataRow("ManagedIdentity")]
    [DataRow("WorkloadIdentity")]
    public async Task ExistingDeploymentSupportsDiscoveryChatAndStream(string authenticationMode)
    {
        using var live = Open(authenticationMode);
        var models = await live.Provider.ListModelsAsync(live.Values);
        Assert.IsTrue(models.Any(model => model.Id == live.Deployment), "The selected deployment was not discovered.");

        var request = new AepChatRequest(live.Deployment,
            [new AepMessage(AepRole.User, [AepContent.FromText("Reply with one short word.")])],
            new AepModelOptions { MaxOutputTokens = 64 }, BoundValues: live.Values);
        var response = await live.Provider.ChatAsync(request, default);
        Assert.IsNotNull(response.FinishReason);

        var completed = false;
        await foreach (var update in live.Provider.ChatStreamingAsync(request, default))
            completed |= update.FinishReason is not null;
        Assert.IsTrue(completed, "The Foundry stream did not finish.");
    }

    [TestMethod]
    [DataRow("ApiKey")]
    [DataRow("Development")]
    [DataRow("ManagedIdentity")]
    [DataRow("WorkloadIdentity")]
    public async Task ExistingToolCapableDeploymentAcceptsGovernedToolRoundTrip(string authenticationMode)
    {
        using var live = Open(authenticationMode);
        var model = (await live.Provider.ListModelsAsync(live.Values)).SingleOrDefault(value => value.Id == live.Deployment);
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
            }))], BoundValues: live.Values);
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
            AllowedPrivateHosts = new HashSet<string>((Environment.GetEnvironmentVariable("AGENTSTRATION_FOUNDRY_LIVE_ALLOWED_PRIVATE_HOSTS") ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), StringComparer.OrdinalIgnoreCase),
            RequestTimeout = TimeSpan.FromSeconds(60)
        };
        options.Validate();
        var values = new List<AepBoundValue>
        {
            Inline(FoundryValueRequirements.ProjectEndpoint, project),
            Inline(FoundryValueRequirements.InferenceEndpoint, inference),
            Inline(FoundryValueRequirements.AuthenticationMode, authenticationMode)
        };
        var key = Environment.GetEnvironmentVariable("AGENTSTRATION_FOUNDRY_LIVE_API_KEY");
        if (authenticationMode == "ApiKey")
        {
            if (string.IsNullOrWhiteSpace(key)) Assert.Inconclusive("Provide AGENTSTRATION_FOUNDRY_LIVE_API_KEY for ApiKey live tests.");
            values.Add(AepBoundValue.Secured(FoundryValueRequirements.Credential, new AepSecretAccessGrant(
                AepProtocol.SecretAccessVersion, new Uri("https://live-secret.test/api/aep/secrets/redeem"),
                "Agentstration.Extensions.Foundry", FoundryValueRequirements.Credential, "live", "live")));
        }
        AddOptional(values, FoundryValueRequirements.ManagedIdentityClientId, "AGENTSTRATION_FOUNDRY_LIVE_MANAGED_IDENTITY_CLIENT_ID");
        AddOptional(values, FoundryValueRequirements.WorkloadIdentityTenantId, "AZURE_TENANT_ID");
        AddOptional(values, FoundryValueRequirements.WorkloadIdentityClientId, "AZURE_CLIENT_ID");
        AddOptional(values, FoundryValueRequirements.WorkloadIdentityTokenFile, "AZURE_FEDERATED_TOKEN_FILE");
        var client = new HttpClient(FoundrySecureTransport.Create(options)) { Timeout = Timeout.InfiniteTimeSpan };
        var secretClient = new HttpClient(new SecretHandler(key));
        return new LiveProvider(new FoundryAepModelProvider(client, options, new FoundryBoundConnectionResolver(secretClient)),
            client, secretClient, deployment, values);
    }

    private static AepBoundValue Inline(string id, string value) => AepBoundValue.Inline(id, JsonSerializer.SerializeToElement(value));
    private static void AddOptional(ICollection<AepBoundValue> values, string id, string environmentName)
    {
        if (Environment.GetEnvironmentVariable(environmentName) is { Length: > 0 } value) values.Add(Inline(id, value));
    }
    private sealed record LiveProvider(FoundryAepModelProvider Provider, HttpClient Client, HttpClient SecretClient,
        string Deployment, IReadOnlyList<AepBoundValue> Values) : IDisposable
    {
        public void Dispose() { Client.Dispose(); SecretClient.Dispose(); }
    }
    private sealed class SecretHandler(string? key) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new AepSecretAccessResponse(AepProtocol.SecretAccessVersion,
                    Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(key ?? "unused"))), options: AepProtocol.JsonOptions)
            });
    }
}
