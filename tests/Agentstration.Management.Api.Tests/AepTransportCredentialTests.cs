using System.Net;
using System.Text;
using System.Text.Json;
using Agentstration.Aep.Abstractions;
using Agentstration.Aep.Client;
using Agentstration.Extensions.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.ModelProviders;
using Agentstration.Resources;
using Agentstration.Secrets;
using Agentstration.Secrets.Abstractions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Agentstration.Management.Tests;

[TestClass]
public sealed class AepTransportCredentialTests : ModelManagementApiTestBase
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task PrivateInstanceTransportCredentialWorksFromBusinessScopesWithoutBeingShared(bool tenant)
    {
        var handler = new TransportHandler();
        await using var factory = Factory().WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IHttpClientFactory>();
            services.AddSingleton<IHttpClientFactory>(new TransportHttpFactory(handler));
        }));
        var context = await GetBootstrapContextAsync(factory);
        var services = factory.Services;
        var scopes = services.GetRequiredService<IRequestContextScopeFactory>();
        var current = services.GetRequiredService<ICurrentRequestContext>();
        var secrets = services.GetRequiredService<SecretManagementService>();
        using (scopes.PushSystem())
        {
            await secrets.CreateVaultAsync(new VaultResource
            {
                ApiVersion = ResourceApiVersions.CoreV1,
                Kind = SecretResourceKinds.Vault,
                Metadata = new() { Name = "transport-vault" },
                ScopeRef = ResourceScopeRef.Instance,
                Definition = new() { DisplayName = "Transport vault", ProviderType = "local" }
            }, default);
            await secrets.InitializeVaultExactAsync(ResourceScopeRef.Instance, "transport-vault", default);
            await secrets.CreateSecretAsync(new SecretResource
            {
                ApiVersion = ResourceApiVersions.CoreV1,
                Kind = SecretResourceKinds.Secret,
                Metadata = new() { Name = "transport-key" },
                ScopeRef = ResourceScopeRef.Instance,
                Definition = new() { DisplayName = "Private transport key", Vault = new("transport-vault"), Key = "transport" }
            }, default);
            using var value = new SecretValue(Encoding.UTF8.GetBytes("fixture-transport-value"));
            await secrets.SetValueExactAsync(ResourceScopeRef.Instance, "transport-key", value, default);
        }

        using var consumer = tenant ? scopes.PushTenant(context.PrincipalId, context.TenantId) : scopes.Push(context);
        var expectedMode = tenant ? ControlPlaneAccessMode.Tenant : ControlPlaneAccessMode.Workspace;
        handler.ObserveScope = () => current.AccessMode;
        Assert.IsNull(await secrets.GetSecretExactAsync(ResourceScopeRef.Instance, "transport-key", default));
        var provider = new ModelProviderConfiguration
        {
            Uid = Guid.NewGuid(),
            Name = "transport-provider",
            AdapterType = AepModelProvider.AdapterType,
            ContributionId = "test",
            ScopeRef = ResourceScopeRef.Instance,
            Endpoint = new("https://transport.test/"),
            Extension = new("transport-extension", ResourceScopeRef.Instance),
            ExtensionScopeRef = ResourceScopeRef.Instance,
            ExpectedExtensionId = "extension.transport",
            AuthenticationMode = AepTransportAuthenticationMode.StaticBearer,
            Credential = new("transport-key")
        };
        var deployment = new ModelDeploymentConfiguration
        {
            Name = "transport-model",
            ProviderName = provider.Name,
            ModelName = "test-model"
        };
        var adapter = services.GetRequiredService<AepModelProvider>();
        _ = await adapter.ResolveCapabilitiesAsync(provider, deployment);
        using var chat = adapter.CreateChatClient(provider, deployment);
        var response = await chat.GetResponseAsync([new ChatMessage(ChatRole.User, "fixture request")]);
        Assert.AreEqual("pong", response.Text);
        Assert.IsTrue(handler.Authorizations.Count > 0);
        Assert.IsTrue(handler.Authorizations.All(value => value == "Bearer fixture-transport-value"));
        Assert.IsTrue(handler.Scopes.All(value => value == expectedMode), "Only credential resolution may enter the infrastructure scope.");
        Assert.AreEqual(expectedMode, current.AccessMode);
        Assert.IsNull(await secrets.GetSecretExactAsync(ResourceScopeRef.Instance, "transport-key", default));

        var sent = handler.Authorizations.Count;
        var missing = await Assert.ThrowsAsync<AepProtocolException>(() => adapter.ResolveCapabilitiesAsync(
            provider with { Credential = new("missing-transport-key") }, deployment).AsTask());
        Assert.AreEqual("credential_unavailable", missing.Code);
        Assert.AreEqual(sent, handler.Authorizations.Count);
        Assert.AreEqual(expectedMode, current.AccessMode);

        await Assert.ThrowsAsync<SecretAccessDeniedException>(() => adapter.ResolveCapabilitiesAsync(
            provider with { Credential = new("transport-key", ResourceScopeRef.Tenant(context.TenantId)) }, deployment).AsTask());
        Assert.AreEqual(sent, handler.Authorizations.Count);
        Assert.AreEqual(expectedMode, current.AccessMode);

        // An ancestor Secret still requires an explicit use grant when the
        // registration itself belongs to a Tenant, even during infrastructure reads.
        await Assert.ThrowsAsync<SecretAccessDeniedException>(() => adapter.ResolveCapabilitiesAsync(
            provider with
            {
                Extension = new("tenant-transport", ResourceScopeRef.Tenant(context.TenantId)),
                ExtensionScopeRef = ResourceScopeRef.Tenant(context.TenantId),
                Credential = new("transport-key", ResourceScopeRef.Instance)
            }, deployment).AsTask());
        Assert.AreEqual(sent, handler.Authorizations.Count);
        Assert.AreEqual(expectedMode, current.AccessMode);
    }

    private sealed class TransportHttpFactory(TransportHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class TransportHandler : HttpMessageHandler
    {
        public Func<ControlPlaneAccessMode>? ObserveScope { get; set; }
        public List<string> Authorizations { get; } = [];
        public List<ControlPlaneAccessMode> Scopes { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Authorizations.Add(request.Headers.Authorization?.ToString() ?? string.Empty);
            Scopes.Add(ObserveScope!());
            var path = request.RequestUri!.AbsolutePath;
            var json = path == AepProtocol.DiscoveryPath
                ? JsonSerializer.Serialize(new AepManifest(AepProtocol.Version,
                    new("extension.transport", "Transport fixture", "1.0.0"),
                    new Dictionary<string, AepCapabilityDescriptor>(),
                    new([new("test", "Test", new(ModelDiscovery: true))])), AepProtocol.JsonOptions)
                : path.EndsWith("/chat", StringComparison.Ordinal)
                    ? JsonSerializer.Serialize(new AepChatResponse(
                        [new(AepRole.Assistant, [AepContent.FromText("pong")])], "test-model", AepFinishReason.Stop), AepProtocol.JsonOptions)
                : JsonSerializer.Serialize(new[] { new AepModelDescriptor("test-model", "Test model") }, AepProtocol.JsonOptions);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
    }
}
