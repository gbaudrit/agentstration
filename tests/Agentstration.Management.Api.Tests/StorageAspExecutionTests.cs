using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Agentstration.Aep.Abstractions;
using Agentstration.Aep.AspNetCore;
using Agentstration.Aep.Client;
using Agentstration.Asp.Abstractions;
using Agentstration.Asp.Aep;
using Agentstration.Asp.Client;
using Agentstration.Asp.Server;
using Agentstration.ConnectedServices;
using Agentstration.ConnectedServices.Contracts;
using Agentstration.Extensions.Aep;
using Agentstration.Extensions.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.Infrastructure.Storage;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Secrets;
using Agentstration.Secrets.Abstractions;
using Agentstration.Storage;
using Agentstration.Storage.Contracts;
using Asp.Samples.LocalStorage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Client;

namespace Agentstration.Management.Tests;

[TestClass]
public sealed class StorageAspExecutionTests : ModelManagementApiTestBase
{
    private static readonly JsonElement Schema = JsonSerializer.SerializeToElement(new { type = "object", additionalProperties = true });
    private static readonly JsonElement Empty = JsonSerializer.SerializeToElement(new { });

    [TestMethod]
    public async Task ExactAepContributionAndManualExportsExecuteThroughRealMcpWithoutAutomaticLinks()
    {
        await using var extension = await Extension.StartAsync();
        await using var product = Factory().WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?> { ["Agentstration:Aep:SecretAccess:PublicBaseUrl"] = "http://localhost" })));
        var bootstrap = await GetBootstrapContextAsync(product);
        var services = product.Services;
        using var caller = services.GetRequiredService<IRequestContextScopeFactory>().Push(bootstrap);
        var scope = ResourceScopeRef.Workspace(bootstrap.WorkspaceId);
        var resources = services.GetRequiredService<IResourceStore>();
        var secrets = services.GetRequiredService<SecretManagementService>();
        await secrets.CreateVaultAsync(new()
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = SecretResourceKinds.Vault,
            ScopeRef = scope,
            Metadata = new() { Name = "fixture-vault" },
            Definition = new() { DisplayName = "Fixture vault", ProviderType = "local" }
        }, default);
        foreach (var (name, value) in new[] { ("transport", FixtureFactory.Credential), ("downstream", "storage-downstream-secret") })
        {
            await secrets.CreateSecretAsync(new()
            {
                ApiVersion = ResourceApiVersions.CoreV1,
                Kind = SecretResourceKinds.Secret,
                ScopeRef = scope,
                Metadata = new() { Name = name },
                Definition = new() { DisplayName = name, Vault = new("fixture-vault", scope), Key = name }
            }, default);
            using var secretValue = new SecretValue(Encoding.UTF8.GetBytes(value));
            await secrets.SetValueExactAsync(scope, name, secretValue, default);
        }
        using var secretClient = product.CreateClient();
        extension.Factory.SecretClient = secretClient;
        var registration = await resources.PutExactAsync(scope, new ExtensionRegistrationResource
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = ExtensionKinds.ExtensionRegistration,
            Metadata = new() { Name = "storage-fixture" },
            ScopeRef = scope,
            Generation = 1,
            Definition = new()
            {
                DisplayName = "Storage fixture",
                Endpoint = extension.Endpoint,
                ExpectedExtensionId = "fixture.storage",
                AuthenticationMode = AepTransportAuthenticationMode.StaticBearer,
                Credential = new("transport", scope)
            }
        }, null, true, default);
        using (services.GetRequiredService<IRequestContextScopeFactory>().PushSystem())
            await services.GetServices<IAepEnrollmentResourceProvisioner>().OfType<AepStorageTypeProvisioner>().Single()
                .ProvisionAsync(registration.Value, extension.Manifest, default);
        var type = (await services.GetRequiredService<StorageTypeManagementService>().GetExactAsync(scope,
            ResourceNamespace.Parse("fixture.storage"), "objects", default))!.Value;
        var serviceVersion = new ConnectedServiceTypeVersionDescriptor("1.0.0", StorageDigests.Schema(Schema), Schema,
            [new("endpoint-input", true, ConnectedServiceValueType.Text, ConnectedServiceValueProtection.Standard),
                new("credential-input", true, ConnectedServiceValueType.Text, ConnectedServiceValueProtection.Secured)],
            [new("endpoint-export", ConnectedServiceValueType.Text, ConnectedServiceValueProtection.Standard, SourceInputId: "endpoint-input"),
                new("credential-export", ConnectedServiceValueType.Text, ConnectedServiceValueProtection.Secured, SourceInputId: "credential-input")]);
        var serviceType = (await services.GetRequiredService<ConnectedServiceTypeManagementService>().CreateAsync(new()
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = ConnectedServiceResourceKinds.ConnectedServiceType,
            Metadata = new() { Name = "unrelated-account" },
            ScopeRef = scope,
            Definition = new() { DisplayName = "Independent account", PreferredVersion = "1.0.0", Versions = [serviceVersion] }
        }, default)).Value;
        var service = (await services.GetRequiredService<ConnectedServiceManagementService>().CreateAsync(new()
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = ConnectedServiceResourceKinds.ConnectedService,
            Metadata = new() { Name = "account" },
            ScopeRef = scope,
            Definition = new()
            {
                DisplayName = "Account",
                Options = Empty,
                Type = new() { Type = new(serviceType.Name, scope), TypeUid = serviceType.Uid, Version = "1.0.0", SchemaDigest = serviceVersion.SchemaDigest },
                Bindings = [new() { InputId = "endpoint-input", Kind = ConnectedServiceBindingKind.Literal,
                    Literal = JsonSerializer.SerializeToElement("Opaque_BACKEND_Value") },
                    new() { InputId = "credential-input", Kind = ConnectedServiceBindingKind.Secret,
                        Secret = new(new(ResourceNamespace.Default, SecretResourceKinds.Secret, "downstream"), scope) }]
            }
        }, default)).Value;
        var version = type.Definition.Versions.Single();
        var properties = new StorageProperties
        {
            DisplayName = "Remote fixture",
            Type = new()
            {
                Type = new(type.Name, scope, type.Namespace),
                TypeUid = type.Uid,
                Version = version.Version,
                SchemaDigest = version.SchemaDigest,
                ContractDigest = version.ContractDigest
            },
            ConnectedService = new() { Service = new(service.Address, scope), ServiceUid = service.Uid, Generation = service.Generation },
            ConnectedServiceType = service.Definition.Type,
            Bindings = [new("backend-endpoint", serviceVersion.Exports.Single(e => e.Id == "endpoint-export")),
                new("backend-secret", serviceVersion.Exports.Single(e => e.Id == "credential-export"))],
            Configuration = JsonSerializer.SerializeToElement(new Dictionary<string, object> { ["Opaque_KEY"] = new object[] { "MiXeD", 42 } }),
            Envelope = JsonSerializer.SerializeToElement(new { prefix = "notes/" })
        };
        var management = services.GetRequiredService<StorageManagementService>();
        async Task<StorageResource> Create(string name, StorageProperties definition) => (await management.CreateAsync(new()
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = StorageResourceKinds.Storage,
            ScopeRef = scope,
            Metadata = new() { Name = name },
            Definition = definition
        }, default)).Value;
        var incomplete = await Assert.ThrowsAsync<StorageException>(() => Create("unmapped", properties with { Bindings = [] }));
        Assert.AreEqual("storage_mapping_incomplete", incomplete.Code);
        Assert.AreEqual(0, extension.Factory.Configurations);
        var first = await Create("remote-first", properties);
        var second = await Create("remote-second", properties);
        var gateway = services.GetRequiredService<IStorageContentGateway>();
        var firstTarget = Target(first, scope);
        var written = await gateway.WriteAsync(firstTarget, new("write-one", DateTimeOffset.UtcNow.AddMinutes(1), "notes/key",
            Convert.ToBase64String(Encoding.UTF8.GetBytes("content"))));
        var read = await gateway.ReadAsync(firstTarget, written.Reference, 0, 100);
        Assert.AreEqual("content", Encoding.UTF8.GetString(Convert.FromBase64String(read.ContentBase64)));
        Assert.AreEqual(properties.Configuration.GetRawText(), extension.Factory.Last!.Configuration.GetRawText());
        Assert.AreEqual(properties.Envelope.GetRawText(), extension.Factory.Last.Envelope.GetRawText());
        Assert.AreEqual("Opaque_BACKEND_Value", extension.Factory.Last.Connection.Single(v => v.RequirementId == "backend-endpoint").InlineValue!.Value.GetString());
        Assert.AreEqual(AspValidation.Digest(Encoding.UTF8.GetBytes("storage-downstream-secret")), extension.Factory.SecretDigest);
        Assert.DoesNotContain("storage-downstream-secret", JsonSerializer.Serialize(extension.Factory.Last, AepProtocol.JsonOptions), StringComparison.Ordinal);
        Assert.AreEqual("[REDACTED]", extension.Factory.Last.ToString());
        Assert.HasCount(0, (await gateway.ListAsync(Target(second, scope), "notes/", 10)).Objects);
        var cross = await Assert.ThrowsAsync<StorageException>(() => gateway.ReadAsync(Target(second, scope), written.Reference, 0, 100));
        Assert.AreEqual("storage_content_invalid_reference", cross.Code);
        var narrowed = (await services.GetRequiredService<StorageManagementService>().CreateAsync(new()
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = StorageResourceKinds.Storage,
            ScopeRef = scope,
            Metadata = new() { Name = "narrowed-remote" },
            Definition = properties with { Envelope = JsonSerializer.SerializeToElement(new { prefix = "notes/", maximumReadBytes = 4 }) }
        }, default)).Value;
        var narrowedTarget = Target(narrowed, scope);
        var narrowedWrite = await gateway.WriteAsync(narrowedTarget, new("narrowed-write", DateTimeOffset.UtcNow.AddMinutes(1),
            "notes/limited", Convert.ToBase64String(Encoding.UTF8.GetBytes("bounded-content"))));
        Assert.AreEqual(4, Convert.FromBase64String((await gateway.ReadAsync(narrowedTarget, narrowedWrite.Reference, 0, 4)).ContentBase64).Length);
        var exceedsEnvelope = await Assert.ThrowsAsync<StorageException>(() => gateway.ReadAsync(narrowedTarget, narrowedWrite.Reference, 0, 5));
        Assert.AreEqual("storage_content_limit_exceeded", exceedsEnvelope.Code);
        extension.Factory.Enabled = false;
        var denied = await Assert.ThrowsAsync<StorageException>(() => gateway.ListAsync(firstTarget, "notes/", 10));
        Assert.AreEqual("storage_content_forbidden", denied.Code);
        extension.Factory.Enabled = true;
        var configurationCount = extension.Factory.Configurations;
        _ = await resources.PutExactAsync(scope, registration.Value with { Generation = registration.Value.Generation + 1 }, registration.ETag, false, default);
        var stale = await Assert.ThrowsAsync<StorageException>(() => gateway.ListAsync(firstTarget, "notes/", 10));
        Assert.AreEqual("storage_provider_unavailable", stale.Code);
        Assert.AreEqual(configurationCount, extension.Factory.Configurations);
    }

    private static StorageContentRequest Target(StorageResource resource, ResourceScopeRef scope) => new(
        new(new(scope, resource.Namespace, resource.Kind, resource.Name), resource.Uid, resource.Generation, StorageDigests.Binding(resource.Definition)),
        new(scope, resource.Address), new(Write: true, Prefix: "notes/"));

    [TestMethod]
    public async Task DirectTargetsWithoutAConfiguredLeaseAndUnauthenticatedConfigurationAreRefused()
    {
        await using var extension = await Extension.StartAsync();
        using var http = new HttpClient();
        using var unauthenticated = await http.PostAsJsonAsync(new Uri(extension.Endpoint, AepStorageProtocol.BindingPath), new { });
        Assert.AreEqual(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", FixtureFactory.Credential);
        using var excessive = await http.PostAsync(new Uri(extension.Endpoint, AepStorageProtocol.BindingPath),
            new StringContent(new string('x', 100001), Encoding.UTF8, "application/json"));
        Assert.AreEqual(HttpStatusCode.RequestEntityTooLarge, excessive.StatusCode);
        Assert.AreEqual("application/problem+json", excessive.Content.Headers.ContentType?.MediaType);
        await using var mcp = await McpClient.CreateAsync(new HttpClientTransport(new()
        { Endpoint = new Uri(extension.Endpoint, "/mcp") }, http));
        var denied = await Assert.ThrowsAsync<AspException>(() => AspClient.ConnectAsync(mcp, "asp", new("selected-store", "binding", "1")));
        Assert.AreEqual("forbidden", denied.Code);
        Assert.AreEqual(0, extension.Factory.Configurations);
    }

    private sealed class Extension(WebApplication app, FixtureFactory factory, AepManifest manifest) : IAsyncDisposable
    {
        public Uri Endpoint { get; } = new(app.Urls.Single());
        public FixtureFactory Factory { get; } = factory;
        public AepManifest Manifest { get; } = manifest;
        public async ValueTask DisposeAsync() { await app.StopAsync(); await app.DisposeAsync(); }
        public static async Task<Extension> StartAsync()
        {
            var factory = new FixtureFactory();
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            builder.Services.AddHttpContextAccessor();
            builder.Services.AddAepStaticBearerAuthentication(options => options.AddToken("fixture", "product", FixtureFactory.Credential));
            builder.Services.AddAep(options =>
            {
                options.Extension = new("fixture.storage", "Fixture storage", "1.0.0");
                options.Capabilities[AepCapabilityNames.SecretAccess] = new(AepProtocol.SecretAccessVersion);
                options.Capabilities[AepCapabilityNames.BoundValues] = new(AepProtocol.BoundValuesCapabilityVersion);
                options.McpServers.Add(new("content", "/mcp"));
                foreach (var contribution in factory.Contributions) options.StorageTypes.Add(contribution);
            });
            builder.Services.AddSingleton(factory);
            builder.Services.AddSingleton(services => new AspAepBindings(factory, services.GetRequiredService<Microsoft.AspNetCore.Http.IHttpContextAccessor>(),
                user => user.FindFirstValue(ClaimTypes.NameIdentifier) == "product", TimeProvider.System));
            builder.Services.AddSingleton(services => new AspMcpServer(services.GetRequiredService<AspAepBindings>().ResolveAsync));
            builder.Services.AddMcpServer().WithHttpTransport(options => options.SessionMode = HttpServerSessionMode.Stateless)
                .WithListToolsHandler((request, ct) => request.Services!.GetRequiredService<AspMcpServer>().ListToolsAsync(request, ct))
                .WithCallToolHandler((request, ct) => request.Services!.GetRequiredService<AspMcpServer>().CallToolAsync(request, ct));
            var app = builder.Build();
            app.UseAuthentication(); app.UseAuthorization();
            app.MapAep(); app.MapAspAepBindings(); app.MapMcp("/mcp").RequireAuthorization(AepAuthenticationDefaults.Policy);
            await app.StartAsync();
            using var client = new HttpClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", FixtureFactory.Credential);
            var manifest = await client.GetFromJsonAsync<AepManifest>(new Uri(app.Urls.Single() + AepProtocol.DiscoveryPath), AepProtocol.JsonOptions);
            Assert.IsNotNull(manifest);
            Assert.HasCount(0, AepDescriptorValidator.Validate(manifest));
            return new(app, factory, manifest);
        }
    }

    private sealed class FixtureFactory : IAspAepBindingFactory
    {
        // Public, disposable test credential. Never use outside this loopback fixture.
        public const string Credential = "public-disposable-storage-fixture-token-1040";
        private readonly FixtureObjects memory = new();
        public HttpClient SecretClient { get; set; } = null!;
        public string? SecretDigest { get; private set; }
        public bool Enabled { get; set; } = true;
        public int Configurations { get; private set; }
        public AepStorageBindingRequest? Last { get; private set; }
        public IReadOnlyList<AepStorageTypeContribution> Contributions { get; } =
        [new("objects", "Fixture objects", "1.0.0", [new("1.0.0", AepSchemaDigest.Compute(Schema), Schema,
            AepSchemaDigest.Compute(Schema), Schema, true,
            [new("backend-endpoint", true, AepValueType.Text, AepValueProtection.Standard),
                new("backend-secret", true, AepValueType.Text, AepValueProtection.Secured)],
            new(true, true, true, true), new(100, 65536, 65536), new("asp.content/1", "content", "asp", "sample.memory", "1.0.0"))])];
        public async ValueTask<AspStoreBinding> CreateAsync(AepStorageBindingRequest request, ClaimsPrincipal authenticatedCaller, CancellationToken cancellationToken)
        {
            Last = request; Configurations++;
            var values = new AepValueResolver(request.Connection, SecretClient);
            using var resolved = await values.ResolveAsync("backend-secret", cancellationToken);
            SecretDigest = AspValidation.Digest(Encoding.UTF8.GetBytes(resolved.ReadString()));
            var store = AspValidation.Parse<AspStoreRef>(request.Store);
            var policy = AspValidation.Parse<AspAccessPolicy>(request.DatasetRule);
            if (request.Envelope.TryGetProperty("maximumReadBytes", out var maximumRead))
                policy = policy with { MaxReadBytes = Math.Min(policy.MaxReadBytes, maximumRead.GetInt32()) };
            var envelopePrefix = request.Envelope.GetProperty("prefix").GetString()!;
            AspValidation.Require(policy.Prefix.StartsWith(envelopePrefix, StringComparison.Ordinal), "forbidden", "The dataset rule widens the envelope.");
            return new AspStoreBinding(store, policy, memory, (call, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                AspValidation.Require(Enabled && call.User?.FindFirstValue(ClaimTypes.NameIdentifier) == "product", "forbidden", "The caller is not authorized.");
                return ValueTask.FromResult(new AspAccessContext(store, "product", "provider-grant-v1", policy));
            });
        }
    }

    private sealed class FixtureObjects : IAspObjectReader, IAspObjectWriter
    {
        private readonly MemoryObjectProvider inner = new(TimeProvider.System);
        public string ProviderId => inner.ProviderId;
        public string ProviderVersion => inner.ProviderVersion;
        public JsonElement ConfigurationSchema => Schema;
        public ValueTask<AspProviderPage> ListAsync(AspAccessContext context, string prefix, string? afterKey, int limit, CancellationToken cancellationToken) => inner.ListAsync(context, prefix, afterKey, limit, cancellationToken);
        public ValueTask<AspProviderObject> StatAsync(AspAccessContext context, string key, string version, CancellationToken cancellationToken) => inner.StatAsync(context, key, version, cancellationToken);
        public ValueTask<AspProviderRead> ReadAsync(AspAccessContext context, string key, string version, long offset, int maxBytes, CancellationToken cancellationToken) => inner.ReadAsync(context, key, version, offset, maxBytes, cancellationToken);
        public ValueTask<AspProviderObject> WriteAsync(AspAccessContext context, AspProviderWrite request, CancellationToken cancellationToken) => inner.WriteAsync(context, request, cancellationToken);
    }
}
