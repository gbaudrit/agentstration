extern alias openapi;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agentstration.Aep.Abstractions;
using Agentstration.Aep.AspNetCore;
using Agentstration.Aep.Client;
using Agentstration.Extensions.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.Infrastructure.Bootstrap;
using Agentstration.Models;
using Agentstration.Parameters;
using Agentstration.Parameters.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;
using Agentstration.Secrets;
using Agentstration.Secrets.Abstractions;
using Agentstration.Secrets.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using OpenApi = openapi::Agentstration.Extensions.OpenApi;

namespace Agentstration.Management.Tests;

[TestClass]
public sealed class ExtensionConfigurationApiTests : ModelManagementApiTestBase
{
    private const string ConfigurationPath = "/api/extensionregistrations/configurable/configuration";

    [TestMethod]
    public async Task UnconfiguredLegacyInstallationsDoNotRequireOperationalConfigurationAuthentication()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var owner = fixture.Scopes.PushSystem();
        var legacy = fixture.Registration with
        {
            Definition = fixture.Registration.Definition with
            { AuthenticationMode = AepTransportAuthenticationMode.None, ExpectedExtensionId = null, Credential = null }
        };
        var store = fixture.Authority.Services.GetRequiredService<IResourceStore>();
        var stored = await store.GetExactAsync<ExtensionRegistrationResource>(ScopedResourceAddress.Create(
            ResourceScopeRef.Instance, legacy.Namespace, legacy.Kind, legacy.Name), default);
        await store.PutExactAsync(ResourceScopeRef.Instance, legacy, stored!.ETag, false, default);
        var synchronizer = fixture.Authority.Services.GetRequiredService<IExtensionConfigurationSynchronizer>();
        await synchronizer.EnsureAsync(legacy, default);
        await synchronizer.EnsureAsync(new ResourceReference(legacy.Name, ResourceScopeRef.Instance, legacy.Namespace), legacy.Namespace, default);
        Assert.AreEqual(0, fixture.ApplyCalls);
        var view = await fixture.Client.GetFromJsonAsync<ExtensionConfigurationView>(ConfigurationPath);
        Assert.IsFalse(view!.Supported);
    }

    [TestMethod]
    public async Task InstanceApiRevalidatesTypedParametersAndRecoversLostAcknowledgmentWithoutOverwritingConcurrentEdits()
    {
        await using var fixture = await Fixture.CreateAsync();
        var client = fixture.Client;
        var initial = await client.GetFromJsonAsync<ExtensionConfigurationView>(ConfigurationPath);
        Assert.IsTrue(initial!.Supported);
        Assert.IsFalse(initial.Ready);
        var contract = initial.Contract!;
        var literal = new PutExtensionConfigurationRequest(contract.Id, contract.Version, contract.SchemaDigest,
            [ModelProviderValueBinding.FromLiteral("AllowedDomains", JsonSerializer.SerializeToElement(new[] { "example.test" }))]);
        var saved = await PutAsync(client, literal, null);
        Assert.AreEqual("applied", saved.Configuration!.Observation.State);
        Assert.AreEqual(1L, saved.Configuration.Observation.AppliedGeneration);
        var policy = fixture.Extension.Services.GetRequiredService<OpenApi.OpenApiDestinationPolicy>();
        await policy.ValidateAsync("https://api.example.test", default);

        using var created = await client.PostAsJsonAsync("/api/parameters", new CreateParameterRequest("domains",
            new() { DisplayName = "Domains", ValueType = ParameterValueType.Array, Value = JsonSerializer.SerializeToElement(new[] { "api.example.test" }) }, ResourceScopeRef.Instance));
        Assert.AreEqual(HttpStatusCode.Created, created.StatusCode);
        var parameterBinding = literal with
        {
            Bindings = [ModelProviderValueBinding.FromParameter("AllowedDomains", new(new(ResourceNamespace.Default, ParameterResourceKinds.Parameter, "domains"), ResourceScopeRef.Instance))]
        };
        var parameterSaved = await PutAsync(client, parameterBinding, saved.ETag);
        using (fixture.Scopes.PushSystem())
        {
            var service = fixture.Authority.Services.GetRequiredService<ParameterManagementService>();
            var parameter = (await service.GetExactAsync(ResourceScopeRef.Instance, "domains", default))!;
            await service.PutExactAsync(ResourceScopeRef.Instance, "domains", parameter.Value.Definition with
            { Value = JsonSerializer.SerializeToElement(new[] { "other.example.test" }) }, parameter.ETag, default);
            await fixture.Authority.Services.GetRequiredService<IExtensionConfigurationSynchronizer>().EnsureAsync(fixture.Registration, default);
        }
        await policy.ValidateAsync("https://other.example.test", default);
        await Assert.ThrowsExactlyAsync<OpenApi.OpenApiExtensionException>(() => policy.ValidateAsync("https://api.example.test", default));
        var current = (await client.GetFromJsonAsync<ExtensionConfigurationView>(ConfigurationPath))!;

        using var staleRequest = new HttpRequestMessage(HttpMethod.Put, ConfigurationPath) { Content = JsonContent.Create(literal) };
        staleRequest.Headers.TryAddWithoutValidation("If-Match", saved.ETag);
        Assert.AreEqual(HttpStatusCode.Conflict, (await client.SendAsync(staleRequest)).StatusCode);
        fixture.LoseNextApplyResponse = true;
        var pending = await PutAsync(client, literal, current.ETag);
        Assert.AreEqual("pending", pending.Configuration!.Observation.State);
        Assert.IsFalse(pending.Ready);
        Assert.AreEqual(2L, pending.Configuration.Observation.AppliedGeneration);
        using var retry = new HttpRequestMessage(HttpMethod.Post, ConfigurationPath + "/apply");
        retry.Headers.TryAddWithoutValidation("If-Match", pending.ETag);
        var retryResponse = await client.SendAsync(retry);
        retryResponse.EnsureSuccessStatusCode();
        var recovered = (await retryResponse.Content.ReadFromJsonAsync<ExtensionConfigurationView>())!;
        Assert.AreEqual("applied", recovered.Configuration!.Observation.State);
        Assert.AreEqual(3L, recovered.Configuration.Observation.AppliedGeneration);
        Assert.AreEqual(4, fixture.ApplyCalls);

        using var invalid = new HttpRequestMessage(HttpMethod.Put, ConfigurationPath)
        { Content = JsonContent.Create(literal with { Bindings = [ModelProviderValueBinding.FromLiteral("AllowedDomains", JsonSerializer.SerializeToElement(true))] }) };
        invalid.Headers.TryAddWithoutValidation("If-Match", recovered.ETag);
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, (await client.SendAsync(invalid)).StatusCode);
        Assert.AreEqual(3L, (await client.GetFromJsonAsync<ExtensionConfigurationView>(ConfigurationPath))!.Configuration!.Generation);

        SelectConsoleContext(client, ResourceScopeRef.Workspace(fixture.Context.WorkspaceId));
        Assert.AreEqual(HttpStatusCode.Forbidden, (await client.GetAsync(ConfigurationPath)).StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync(ConfigurationPath, literal)).StatusCode);
    }

    [TestMethod]
    public async Task FailedDeliveryRemainsNotReadyAfterReloadWithoutLosingTheLastActivation()
    {
        await using var fixture = await Fixture.CreateAsync();
        var initial = (await fixture.Client.GetFromJsonAsync<ExtensionConfigurationView>(ConfigurationPath))!;
        var contract = initial.Contract!;
        var first = new PutExtensionConfigurationRequest(contract.Id, contract.Version, contract.SchemaDigest,
            [ModelProviderValueBinding.FromLiteral("AllowedDomains", JsonSerializer.Deserialize<JsonElement>("[\"first.example.test\"]"))]);
        var applied = await PutAsync(fixture.Client, first, null);
        fixture.FailNextApplyBeforeDelivery = true;
        var pending = await PutAsync(fixture.Client, first with
        {
            Bindings = [ModelProviderValueBinding.FromLiteral("AllowedDomains", JsonSerializer.Deserialize<JsonElement>("[\"second.example.test\"]"))]
        }, applied.ETag);
        Assert.IsFalse(pending.Ready);
        var reloaded = (await fixture.Client.GetFromJsonAsync<ExtensionConfigurationView>(ConfigurationPath))!;
        Assert.AreEqual("pending", reloaded.Configuration!.Observation.State);
        Assert.AreEqual(2L, reloaded.Configuration.Generation);
        Assert.AreEqual(1L, reloaded.Configuration.Observation.AppliedGeneration);
        Assert.IsFalse(reloaded.Ready, "An unapplied desired generation must remain not ready after reloading.");
        var policy = fixture.Extension.Services.GetRequiredService<OpenApi.OpenApiDestinationPolicy>();
        await policy.ValidateAsync("https://first.example.test", default);
        await Assert.ThrowsExactlyAsync<OpenApi.OpenApiExtensionException>(() => policy.ValidateAsync("https://second.example.test", default));
        using var retry = new HttpRequestMessage(HttpMethod.Post, ConfigurationPath + "/apply");
        retry.Headers.TryAddWithoutValidation("If-Match", reloaded.ETag);
        using var response = await fixture.Client.SendAsync(retry);
        response.EnsureSuccessStatusCode();
        var recovered = (await response.Content.ReadFromJsonAsync<ExtensionConfigurationView>())!;
        Assert.IsTrue(recovered.Ready);
        Assert.AreEqual(2L, recovered.Configuration!.Observation.AppliedGeneration);
        await policy.ValidateAsync("https://second.example.test", default);
    }

    [TestMethod]
    public async Task BootstrapCreatesSameConfigurationAndPreservesOperatorEditsOnReplay()
    {
        await using var fixture = await Fixture.CreateAsync();
        var handler = new ExtensionConfigurationBootstrapHandler(fixture.Authority.Services.GetRequiredService<IExtensionConfigurationService>());
        var document = new BootstrapResourceDocument
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = ExtensionKinds.ExtensionConfiguration,
            Metadata = new ResourceMetadata { Name = "initial-openapi-options" },
            Definition = JsonSerializer.SerializeToElement(new
            {
                extension = new ResourceReference("configurable", ResourceScopeRef.Instance),
                optionSet = OpenApi.OpenApiOperationalConfiguration.Descriptor.Id,
                version = "1.0.0",
                bindings = new[] { ModelProviderValueBinding.FromLiteral("AllowedDomains", JsonSerializer.SerializeToElement(new[] { "example.test" })) }
            }, JsonSerializerOptions.Web)
        };
        using (fixture.Scopes.PushSystem())
        {
            var context = new BootstrapResourceOperationContext("fixture", "/tmp", BootstrapProfileScope.Instance);
            Assert.AreEqual(BootstrapResourceDisposition.Create, (await handler.PlanAsync(document, context, new(), default)).Disposition);
            Assert.AreEqual(0, fixture.ApplyCalls);
            Assert.AreEqual(BootstrapResourceApplyResult.Created, await handler.ApplyAsync(document, context, default));
        }
        var saved = (await fixture.Client.GetFromJsonAsync<ExtensionConfigurationView>(ConfigurationPath))!;
        Assert.AreEqual("initial-openapi-options", saved.Configuration!.Name);
        var edited = await PutAsync(fixture.Client, new(saved.Contract!.Id, saved.Contract.Version, saved.Contract.SchemaDigest,
            [ModelProviderValueBinding.FromLiteral("AllowedDomains", JsonSerializer.SerializeToElement(new[] { "api.example.test" }))]), saved.ETag);
        using (fixture.Scopes.PushSystem())
        {
            var context = new BootstrapResourceOperationContext("fixture", "/tmp", BootstrapProfileScope.Instance);
            Assert.AreEqual(BootstrapResourceDisposition.Skip, (await handler.PlanAsync(document, context, new(), default)).Disposition);
            Assert.AreEqual(BootstrapResourceApplyResult.Skipped, await handler.ApplyAsync(document, context, default));
        }
        var replayed = (await fixture.Client.GetFromJsonAsync<ExtensionConfigurationView>(ConfigurationPath))!;
        Assert.AreEqual(edited.Configuration!.Uid, replayed.Configuration!.Uid);
        Assert.AreEqual(edited.Configuration.Generation, replayed.Configuration.Generation);
        Assert.AreEqual("api.example.test", replayed.AppliedValues["AllowedDomains"][0].GetString());
    }

    [TestMethod]
    public async Task SecuredOptionUsesOneUseGrantAndNeverPersistsOrReturnsItsValue()
    {
        await using var fixture = await Fixture.CreateAsync(secured: true);
        using var created = await fixture.Client.PostAsJsonAsync("/api/secrets", new CreateSecretRequest("option-secret",
            new() { DisplayName = "Option secret", Vault = new("configuration-vault", ResourceScopeRef.Instance), Key = "option" }, ResourceScopeRef.Instance));
        created.EnsureSuccessStatusCode();
        using var set = await fixture.Client.PutAsJsonAsync("/api/secrets/option-secret/value?scopeRef=%2Finstance", new SetSecretValueRequest("synthetic-option-value"));
        set.EnsureSuccessStatusCode();
        var initial = (await fixture.Client.GetFromJsonAsync<ExtensionConfigurationView>(ConfigurationPath))!;
        var contract = initial.Contract!;
        var rejected = new PutExtensionConfigurationRequest(contract.Id, contract.Version, contract.SchemaDigest,
            [ModelProviderValueBinding.FromLiteral("Credential", JsonSerializer.SerializeToElement("synthetic-option-value"))]);
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, (await fixture.Client.PutAsJsonAsync(ConfigurationPath, rejected)).StatusCode);
        var saved = await PutAsync(fixture.Client, rejected with
        {
            Bindings = [ModelProviderValueBinding.FromSecret("Credential", new(new(ResourceNamespace.Default, SecretResourceKinds.Secret, "option-secret"), ResourceScopeRef.Instance))]
        }, null);
        Assert.AreEqual("applied", saved.Configuration!.Observation.State);
        Assert.IsTrue(((SecuredConfiguration)fixture.Extension.Services.GetRequiredService<IAepOperationalConfiguration>()).SawSecret);
        Assert.IsFalse(JsonSerializer.Serialize(saved).Contains("synthetic-option-value", StringComparison.Ordinal));
        Assert.IsEmpty(saved.AppliedValues);
        using (fixture.Scopes.PushSystem())
        {
            var stored = await fixture.Authority.Services.GetRequiredService<IResourceStore>().ListExactAsync<ExtensionConfigurationResource>(ResourceScopeRef.Instance,
                ExtensionKinds.ExtensionConfiguration, 0, 100, default);
            Assert.IsFalse(JsonSerializer.Serialize(stored).Contains("synthetic-option-value", StringComparison.Ordinal));
            await fixture.Authority.Services.GetRequiredService<IExtensionConfigurationSynchronizer>().EnsureAsync(fixture.Registration, default);
        }
        Assert.AreEqual(2, ((SecuredConfiguration)fixture.Extension.Services.GetRequiredService<IAepOperationalConfiguration>()).Applications);
    }

    private static async Task<ExtensionConfigurationView> PutAsync(HttpClient client, PutExtensionConfigurationRequest body, string? etag)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, ConfigurationPath) { Content = JsonContent.Create(body) };
        if (etag is not null) request.Headers.TryAddWithoutValidation("If-Match", etag);
        using var response = await client.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<ExtensionConfigurationView>())!;
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "instance-configuration-" + Guid.NewGuid().ToString("N"));
        public required WebApplicationFactory<Program> Authority { get; set; }
        public required WebApplicationFactory<openapi::Program> Extension { get; set; }
        public required HttpClient Client { get; set; }
        public required ExtensionRegistrationResource Registration { get; set; }
        public required RequestContext Context { get; set; }
        public IRequestContextScopeFactory Scopes => Authority.Services.GetRequiredService<IRequestContextScopeFactory>();
        public bool LoseNextApplyResponse { get; set; }
        public bool FailNextApplyBeforeDelivery { get; set; }
        public int ApplyCalls { get; set; }

        public static async Task<Fixture> CreateAsync(bool secured = false)
        {
            var token = AepStaticBearerCredentials.Generate("authority").AccessToken;
            var fixture = new Fixture { Authority = null!, Extension = null!, Client = null!, Registration = null!, Context = null! };
            fixture.Extension = new WebApplicationFactory<openapi::Program>().WithWebHostBuilder(builder =>
            {
                builder.UseSetting("OpenApi:ConfigurationStateFile", Path.Combine(fixture.directory, "state.json"));
                builder.ConfigureTestServices(services =>
                {
                    services.AddAepStaticBearerAuthentication(o => o.AddToken("fixture", "authority", token));
                    services.AddSingleton<OpenApi.IOpenApiAddressResolver, PublicResolver>();
                    if (secured) services.AddSingleton<IAepOperationalConfiguration, SecuredConfiguration>();
                    services.AddHttpClient("openapi-secret-access").ConfigurePrimaryHttpMessageHandler(() => fixture.Authority.Server.CreateHandler());
                });
            });
            fixture.Authority = Factory().WithWebHostBuilder(builder =>
            {
                builder.UseSetting("Agentstration:Aep:SecretAccess:PublicBaseUrl", "https://localhost");
                builder.ConfigureTestServices(services => services.AddHttpClient("agentstration-aep-configuration")
                    .ConfigurePrimaryHttpMessageHandler(() => new Relay(fixture)));
            });
            fixture.Client = fixture.Authority.CreateClient();
            fixture.Context = await GetBootstrapContextAsync(fixture.Authority);
            SelectConsoleContext(fixture.Client, ResourceScopeRef.Instance);
            using var vault = await fixture.Client.PostAsJsonAsync("/api/vaults", new CreateVaultRequest("configuration-vault",
                new() { DisplayName = "Configuration Vault", ProviderType = "local" }, ResourceScopeRef.Instance));
            vault.EnsureSuccessStatusCode();
            using (fixture.Scopes.PushSystem())
                await fixture.Authority.Services.GetRequiredService<SecretManagementService>().InitializeVaultExactAsync(ResourceScopeRef.Instance, "configuration-vault", default);
            using var secret = await fixture.Client.PostAsJsonAsync("/api/secrets", new CreateSecretRequest("configuration-transport",
                new() { DisplayName = "Transport", Vault = new("configuration-vault", ResourceScopeRef.Instance), Key = "transport" }, ResourceScopeRef.Instance));
            secret.EnsureSuccessStatusCode();
            using var set = await fixture.Client.PutAsJsonAsync("/api/secrets/configuration-transport/value?scopeRef=%2Finstance", new SetSecretValueRequest(token));
            set.EnsureSuccessStatusCode();
            using (fixture.Scopes.PushSystem())
            {
                var store = fixture.Authority.Services.GetRequiredService<IResourceStore>();
                fixture.Registration = (await store.PutExactAsync(ResourceScopeRef.Instance, new ExtensionRegistrationResource
                {
                    ApiVersion = ResourceApiVersions.CoreV1,
                    Kind = ExtensionKinds.ExtensionRegistration,
                    Metadata = new ResourceMetadata { Name = "configurable" },
                    ScopeRef = ResourceScopeRef.Instance,
                    Definition = new()
                    {
                        DisplayName = "Configurable",
                        Endpoint = new("http://localhost"),
                        ExpectedExtensionId = "Agentstration.Extensions.OpenApi",
                        AuthenticationMode = AepTransportAuthenticationMode.StaticBearer,
                        Credential = new("configuration-transport", ResourceScopeRef.Instance)
                    }
                }, null, true, default)).Value;
            }
            return fixture;
        }
        public async ValueTask DisposeAsync()
        {
            Client.Dispose(); await Authority.DisposeAsync(); await Extension.DisposeAsync();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
    private sealed class Relay(Fixture fixture) : DelegatingHandler(fixture.Extension.Server.CreateHandler())
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (fixture.FailNextApplyBeforeDelivery && request.Method == HttpMethod.Put
                && request.RequestUri!.AbsolutePath == AepOperationalConfigurationProtocol.Path)
            {
                fixture.FailNextApplyBeforeDelivery = false;
                throw new HttpRequestException("Simulated unavailable transport.");
            }
            var response = await base.SendAsync(request, token);
            if (request.Method == HttpMethod.Put && request.RequestUri!.AbsolutePath == AepOperationalConfigurationProtocol.Path)
            {
                fixture.ApplyCalls++;
                if (fixture.LoseNextApplyResponse)
                { fixture.LoseNextApplyResponse = false; response.Dispose(); throw new HttpRequestException("Simulated response loss."); }
            }
            return response;
        }
    }
    private sealed class SecuredConfiguration(IHttpClientFactory clients) : IAepOperationalConfiguration
    {
        private static readonly AepOperationalConfigurationDescriptor Contract = AepOperationalConfigurationDescriptor.Create("fixture/secured", "1.0.0",
            [new("Credential", JsonSerializer.SerializeToElement(new { type = "string", maxLength = 128 }), true,
                AepValueProtection.Secured, [AepConfigurationValueSource.Secret], AepConfigurationScope.Process, AepConfigurationApplyMode.Immediate)]);
        private string? revision;
        public bool SawSecret { get; private set; }
        public int Applications { get; private set; }
        public AepOperationalConfigurationState Read() => new(Contract, revision, SawSecret, new Dictionary<string, JsonElement>());
        public async ValueTask<AepOperationalConfigurationState> ApplyAsync(AepOperationalConfigurationRequest request, CancellationToken token)
        {
            using var http = clients.CreateClient("openapi-secret-access");
            using var value = await new AepValueResolver(request.Values, http).ResolveAsync("Credential", token);
            SawSecret = value.ReadString() == "synthetic-option-value";
            Assert.IsTrue(SawSecret);
            revision = request.Revision; Applications++;
            return Read();
        }
    }
    private sealed class PublicResolver : OpenApi.IOpenApiAddressResolver
    {
        public Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<IPAddress>>([IPAddress.Parse("8.8.8.8")]);
    }
}
