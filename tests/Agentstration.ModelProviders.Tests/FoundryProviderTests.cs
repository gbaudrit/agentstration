using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Agentstration.Aep.Abstractions;
using Agentstration.Aep.AspNetCore;
using Agentstration.Extensions.Foundry;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Agentstration.ModelProviders.Tests;

[TestClass]
public sealed class FoundryProviderTests
{
    [TestMethod]
    public async Task ManifestDeclaresExactContributionValueRequirementsWithoutProcessConfiguration()
    {
        await using var host = new WebApplicationFactory<FoundryAepModelProvider>();
        using var client = host.CreateClient();
        var manifest = await client.GetFromJsonAsync<AepManifest>(AepProtocol.DiscoveryPath, AepProtocol.JsonOptions);

        Assert.IsNotNull(manifest);
        Assert.IsTrue(manifest.Capabilities.ContainsKey(AepCapabilityNames.ValueRequirements));
        Assert.IsTrue(manifest.Capabilities.ContainsKey(AepCapabilityNames.BoundValues));
        Assert.AreEqual(AepProtocol.SecretAccessVersion,
            manifest.Capabilities[AepCapabilityNames.SecretAccess].Version);
        var requirements = manifest.ValueRequirements!.OrderBy(value => value.Id, StringComparer.Ordinal).ToArray();
        CollectionAssert.AreEqual(new[]
        {
            "authenticationMode", "credential", "inferenceEndpoint", "managedIdentityClientId", "projectEndpoint",
            "workloadIdentityClientId", "workloadIdentityTenantId", "workloadIdentityTokenFile"
        }, requirements.Select(value => value.Id).ToArray());
        Assert.IsTrue(requirements.All(value => value.ContributionKind == AepContributionKinds.ModelProvider
            && value.ContributionId == "microsoft-foundry" && value.Type == AepValueType.Text));
        Assert.AreEqual(AepValueProtection.Secured, requirements.Single(value => value.Id == "credential").Protection);
        Assert.IsFalse(requirements.Single(value => value.Id == "credential").Required);
        foreach (var id in new[] { "projectEndpoint", "inferenceEndpoint", "authenticationMode" })
            Assert.IsTrue(requirements.Single(value => value.Id == id).Required);
        Assert.AreEqual("uri", requirements.Single(value => value.Id == "projectEndpoint").Format);
        Assert.AreEqual("uri", requirements.Single(value => value.Id == "inferenceEndpoint").Format);
    }

    [TestMethod]
    public void InlineCredentialIsRejectedAndMissingRequiredValuesFailClosed()
    {
        var inlineCredential = Values("first", "key", credentialInline: true);
        var issues = AepBoundValueValidator.Validate(inlineCredential, FoundryValueRequirements.All,
            AepContributionKinds.ModelProvider, "microsoft-foundry", requireAll: true);
        Assert.IsTrue(issues.Any(value => value.Code == "bound_value_protection_invalid" && value.RequirementId == "credential"));

        var missing = Values("first", "key").Where(value => value.RequirementId != FoundryValueRequirements.ProjectEndpoint).ToArray();
        issues = AepBoundValueValidator.Validate(missing, FoundryValueRequirements.All,
            AepContributionKinds.ModelProvider, "microsoft-foundry", requireAll: true);
        Assert.IsTrue(issues.Any(value => value.Code == "bound_value_required" && value.RequirementId == "projectEndpoint"));
    }

    [TestMethod]
    public async Task AuthenticationModesEnforceConditionalCredentialAndIdentityValues()
    {
        var secrets = new SecretHandler(new Dictionary<string, string> { ["key"] = "credential" });
        var resolver = new FoundryBoundConnectionResolver(new HttpClient(secrets));
        using var apiKey = await resolver.ResolveAsync(Values("first", "key"));
        Assert.AreEqual(FoundryAuthenticationMode.ApiKey, apiKey.Connection.AuthenticationMode);
        Assert.AreEqual("credential", apiKey.Connection.Credential);

        var managed = StandardValues("first", "ManagedIdentity").Append(Inline(
            FoundryValueRequirements.ManagedIdentityClientId, "11111111-1111-1111-1111-111111111111")).ToArray();
        using var managedLease = await resolver.ResolveAsync(managed);
        Assert.AreEqual(FoundryAuthenticationMode.ManagedIdentity, managedLease.Connection.AuthenticationMode);

        var missingCredential = await Assert.ThrowsAsync<AepServerException>(async () =>
            await resolver.ResolveAsync(StandardValues("first", "ApiKey").ToArray()));
        Assert.AreEqual("bound_value_invalid", missingCredential.Code);

        var wrongIdentity = StandardValues("first", "Development").Append(Inline(
            FoundryValueRequirements.ManagedIdentityClientId, "11111111-1111-1111-1111-111111111111")).ToArray();
        Assert.AreEqual("bound_value_invalid", (await Assert.ThrowsAsync<AepServerException>(async () =>
            await resolver.ResolveAsync(wrongIdentity))).Code);
    }

    [TestMethod]
    public async Task DiscoveryUsesProviderValuesAndKeepsConcurrentProjectsAndCredentialsIsolated()
    {
        var secrets = new SecretHandler(new Dictionary<string, string> { ["first-key"] = "alpha", ["second-key"] = "beta" });
        var seen = new List<string>();
        using var providerHttp = Client((request, _) =>
        {
            lock (seen) seen.Add($"{request.RequestUri!.Host}:{request.Headers.GetValues("api-key").Single()}");
            var model = request.RequestUri.Host.StartsWith("first", StringComparison.Ordinal) ? "model-first" : "model-second";
            return Task.FromResult(Json(HttpStatusCode.OK, JsonSerializer.Serialize(new
            {
                value = new[] { new { type = "ModelDeployment", name = model, capabilities = new { chat = true } } }
            }, AepProtocol.JsonOptions)));
        });
        var provider = new FoundryAepModelProvider(providerHttp, new FoundryExtensionOptions(),
            new FoundryBoundConnectionResolver(new HttpClient(secrets)));

        var results = await Task.WhenAll(
            provider.ListModelsAsync(Values("first", "first-key")),
            provider.ListModelsAsync(Values("second", "second-key")));

        Assert.AreEqual("model-first", results[0].Single().Id);
        Assert.AreEqual("model-second", results[1].Single().Id);
        CollectionAssert.AreEquivalent(new[] { "first.example:alpha", "second.example:beta" }, seen);
        CollectionAssert.AreEquivalent(new[] { "first-key", "second-key" }, secrets.RedeemedCapabilities.ToArray());
    }

    [TestMethod]
    public async Task UpdatedAndRevokedValuesTakeEffectOnTheNextOperation()
    {
        var secretValues = new Dictionary<string, string> { ["current"] = "one" };
        var secrets = new SecretHandler(secretValues);
        var observations = new List<string>();
        using var providerHttp = Client((request, _) =>
        {
            observations.Add($"{request.RequestUri!.Host}:{request.Headers.GetValues("api-key").Single()}");
            return Task.FromResult(Json(HttpStatusCode.OK, "{\"value\":[]}"));
        });
        var provider = new FoundryAepModelProvider(providerHttp, new FoundryExtensionOptions(),
            new FoundryBoundConnectionResolver(new HttpClient(secrets)));

        await provider.ListModelsAsync(Values("first", "current"));
        secretValues["current"] = "two";
        await provider.ListModelsAsync(Values("second", "current"));
        secretValues.Remove("current");
        var revoked = await Assert.ThrowsAsync<AepServerException>(() => provider.ListModelsAsync(Values("third", "current")));

        CollectionAssert.AreEqual(new[] { "first.example:one", "second.example:two" }, observations);
        Assert.AreEqual("secret_unavailable", revoked.Code);
    }

    [TestMethod]
    public async Task DiscoveryRejectsCrossOriginContinuationAndNeverForwardsCredential()
    {
        var calls = 0;
        using var providerHttp = Client((_, _) =>
        {
            calls++;
            return Task.FromResult(Json(HttpStatusCode.OK,
                "{\"value\":[],\"nextLink\":\"https://attacker.example/collect\"}"));
        });
        var provider = Provider(providerHttp);
        var exception = await Assert.ThrowsAsync<AepServerException>(() => provider.ListModelsAsync(Values("first", "key")));
        Assert.AreEqual("discovery_origin_invalid", exception.Code);
        Assert.AreEqual(1, calls);
    }

    [TestMethod]
    public void BoundEndpointCannotAuthorizeItselfForPrivateNetworkEgress()
    {
        var options = new FoundryExtensionOptions();
        Assert.IsFalse(FoundrySecureTransport.IsAddressAllowed(IPAddress.Parse("10.0.0.5"), "first.example", options));
        options = options with { AllowedPrivateHosts = new HashSet<string>(["operator-approved.example"], StringComparer.OrdinalIgnoreCase) };
        Assert.IsFalse(FoundrySecureTransport.IsAddressAllowed(IPAddress.Parse("10.0.0.5"), "first.example", options));
        Assert.IsTrue(FoundrySecureTransport.IsAddressAllowed(IPAddress.Parse("10.0.0.5"), "operator-approved.example", options));
        Assert.IsFalse(FoundrySecureTransport.IsAddressAllowed(IPAddress.Parse("169.254.169.254"), "operator-approved.example", options));
    }

    [TestMethod]
    public async Task DiscoveryBoundsMalformedResponsesAndCancellation()
    {
        var provider = Provider(Client((_, _) => Task.FromResult(Json(HttpStatusCode.OK, "not-json"))));
        Assert.AreEqual("discovery_invalid", (await Assert.ThrowsAsync<AepServerException>(() =>
            provider.ListModelsAsync(Values("first", "key")))).Code);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        provider = Provider(Client(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Json(HttpStatusCode.OK, "{\"value\":[]}");
        }));
        await Assert.ThrowsAsync<OperationCanceledException>(() => provider.ListModelsAsync(Values("first", "key"), cancellation.Token));
    }

    private static FoundryAepModelProvider Provider(HttpClient providerHttp) => new(providerHttp, new FoundryExtensionOptions(),
        new FoundryBoundConnectionResolver(new HttpClient(new SecretHandler(new Dictionary<string, string> { ["key"] = "offline-key" }))));

    private static IReadOnlyList<AepBoundValue> Values(string project, string capability, bool credentialInline = false)
    {
        var values = StandardValues(project, "ApiKey").ToList();
        values.Add(credentialInline ? Inline(FoundryValueRequirements.Credential, "offline-key") :
            AepBoundValue.Secured(FoundryValueRequirements.Credential, Grant(capability)));
        return values;
    }

    private static IEnumerable<AepBoundValue> StandardValues(string project, string mode)
    {
        yield return Inline(FoundryValueRequirements.ProjectEndpoint, $"https://{project}.example/api/projects/{project}");
        yield return Inline(FoundryValueRequirements.InferenceEndpoint, $"https://{project}.example/openai/v1");
        yield return Inline(FoundryValueRequirements.AuthenticationMode, mode);
    }

    private static AepBoundValue Inline(string id, string value) => AepBoundValue.Inline(id, JsonSerializer.SerializeToElement(value));
    private static AepSecretAccessGrant Grant(string capability) => new(AepProtocol.SecretAccessVersion,
        new Uri("https://secrets.test/api/aep/secrets/redeem"), "Agentstration.Extensions.Foundry",
        FoundryValueRequirements.Credential, $"execution-{capability}", capability);
    private static HttpClient Client(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) => new(new StubHandler(handler));
    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class SecretHandler(IDictionary<string, string> values) : HttpMessageHandler
    {
        public List<string> RedeemedCapabilities { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var payload = JsonSerializer.Deserialize<AepSecretAccessRequest>(await request.Content!.ReadAsStringAsync(cancellationToken), AepProtocol.JsonOptions)!;
            RedeemedCapabilities.Add(payload.SecretCapability);
            if (!values.TryGetValue(payload.SecretCapability, out var value))
                return Json(HttpStatusCode.NotFound, JsonSerializer.Serialize(new AepErrorResponse(
                    new AepError("secret_unavailable", "Unavailable.")), AepProtocol.JsonOptions));
            return Json(HttpStatusCode.OK, JsonSerializer.Serialize(new AepSecretAccessResponse(
                AepProtocol.SecretAccessVersion, Convert.ToBase64String(Encoding.UTF8.GetBytes(value))), AepProtocol.JsonOptions));
        }
    }
    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => handler(request, cancellationToken);
    }
}
