using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Agentstration.Aep.Abstractions;
using Agentstration.Aep.AspNetCore;
using Agentstration.Asp.Abstractions;
using Agentstration.Asp.Aep;
using Agentstration.Asp.Server;
using Agentstration.ConnectedServices;
using Agentstration.ConnectedServices.Contracts;
using Agentstration.Extensions.Aep;
using Agentstration.Extensions.Contracts;
using Agentstration.Extensions.PostgreSql;
using Agentstration.Infrastructure.ConnectedServices;
using Agentstration.Infrastructure.Storage;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Secrets;
using Agentstration.Secrets.Abstractions;
using Agentstration.Storage;
using Agentstration.Storage.Contracts;
using Asp.Samples.StructuredDocuments;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Agentstration.Management.Tests;

/// <summary>Loopback AEP/MCP + actual late-bound product Secrets; deterministic memory or explicit local PostgreSQL.</summary>
internal sealed class StructuredStorageFixture(WebApplication extension, StorageResource storage,
    StorageRevisionReference reference, StructuredStorageFixture.BindingFactory bindings) : IAsyncDisposable
{
    // Public disposable fixture credentials, confined to loopback tests.
    private const string Credential = "public-disposable-structured-product-fixture";
    internal static JsonElement Json(string value) { using var json = JsonDocument.Parse(value); return json.RootElement.Clone(); }
    internal static JsonElement Schema => Json("""{"type":"object","properties":{"status":{"type":"string"},"value":{"type":"number"}}}""");
    public StorageResource Storage { get; } = storage;
    public StorageRevisionReference Reference { get; } = reference;
    public BindingFactory Bindings { get; } = bindings;
    public Guid Collection => Storage.Definition.Collections.Single(c => c.Name == "shared").Uid;
    public StorageDocumentTarget Target(ResourceScopeRef consumer) => new(Reference, new(consumer, Storage.Address), Collection,
        Storage.Definition.Collections.Single(c => c.Uid == Collection).Policy);
    public async ValueTask DisposeAsync() { await extension.StopAsync(); await extension.DisposeAsync(); }

    internal static async Task<StructuredStorageFixture> StartAsync(IServiceProvider services, HttpClient secretClient,
        ResourceScopeRef owner, bool postgres)
    {
        var connectionText = Environment.GetEnvironmentVariable("AGENTSTRATION_TEST_ASP_POSTGRES");
        if (postgres && string.IsNullOrWhiteSpace(connectionText)) Assert.Inconclusive("Set AGENTSTRATION_TEST_ASP_POSTGRES for the disposable local provider.");
        var connection = new NpgsqlConnectionStringBuilder(connectionText ?? "Host=127.0.0.1;Port=1;Database=fixture;Username=fixture;SslMode=Disable");
        if (postgres) await new PostgreSqlDocumentProvider(connection.ConnectionString, Schema).InitializeAsync(default);
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Aep:EnrollmentMode"] = "Disabled",
            ["PostgreSqlDocuments:AllowedClients:0"] = "product",
            ["PostgreSqlDocuments:AllowLoopbackWithoutTls"] = "true"
        });
        builder.Services.AddPostgreSqlDocuments(builder.Configuration);
        builder.Services.AddAepStaticBearerAuthentication(o => o.AddToken("fixture", "product", Credential));
        var factory = new BindingFactory(postgres, secretClient);
        builder.Services.Replace(ServiceDescriptor.Singleton<IAspDocumentAepBindingFactory>(factory));
        var app = builder.Build(); app.MapPostgreSqlDocuments(); await app.StartAsync();
        try
        {
            using var http = new HttpClient(); http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Credential);
            var endpoint = new Uri(app.Urls.Single());
            var manifest = (await http.GetFromJsonAsync<AepManifest>(new Uri(endpoint, AepProtocol.DiscoveryPath), AepProtocol.JsonOptions))!;
            Assert.IsEmpty(AepDescriptorValidator.Validate(manifest));
            var secrets = services.GetRequiredService<SecretManagementService>();
            var suffix = Guid.NewGuid().ToString("N");
            var vault = "documents-vault-" + suffix;
            await secrets.CreateVaultAsync(new()
            {
                ApiVersion = ResourceApiVersions.CoreV1,
                Kind = SecretResourceKinds.Vault,
                ScopeRef = owner,
                Metadata = new() { Name = vault },
                Definition = new() { DisplayName = "Fixture", ProviderType = "local" }
            }, default);
            async Task<string> Secret(string key, string value)
            {
                var name = key + "-" + suffix;
                await secrets.CreateSecretAsync(new()
                {
                    ApiVersion = ResourceApiVersions.CoreV1,
                    Kind = SecretResourceKinds.Secret,
                    ScopeRef = owner,
                    Metadata = new() { Name = name },
                    Definition = new() { DisplayName = key, Vault = new(vault, owner), Key = key }
                }, default);
                using var bytes = new SecretValue(Encoding.UTF8.GetBytes(value)); await secrets.SetValueExactAsync(owner, name, bytes, default); return name;
            }
            var transportSecret = await Secret("transport", Credential);
            var passwordSecret = await Secret("password", "public-disposable-database-fixture");
            var resources = services.GetRequiredService<IResourceStore>();
            var registration = await resources.PutExactAsync(owner, new ExtensionRegistrationResource
            {
                ApiVersion = ResourceApiVersions.CoreV1,
                Kind = ExtensionKinds.ExtensionRegistration,
                ScopeRef = owner,
                Metadata = new() { Name = "documents-" + suffix },
                Generation = 1,
                Definition = new()
                {
                    DisplayName = "Structured fixture",
                    Endpoint = endpoint,
                    ExpectedExtensionId = PostgreSqlStorageTypes.ExtensionId,
                    AuthenticationMode = AepTransportAuthenticationMode.StaticBearer,
                    Credential = new(transportSecret, owner)
                }
            }, null, true, default);
            foreach (var provisioner in services.GetServices<IAepEnrollmentResourceProvisioner>().Where(p => p is AepStorageTypeProvisioner or AepEnrollmentConnectedServiceTypeProvisioner))
                await provisioner.ProvisionAsync(registration.Value, manifest, default);
            var ns = ResourceNamespace.Parse(PostgreSqlStorageTypes.ExtensionId);
            var type = (await services.GetRequiredService<StorageTypeManagementService>().GetExactAsync(owner, ns, PostgreSqlStorageTypes.TypeId, default))!.Value;
            var serviceType = (await services.GetRequiredService<ConnectedServiceTypeManagementService>().GetExactAsync(owner, ns, "postgresql", default))!.Value;
            var serviceVersion = serviceType.Definition.Versions.Single();
            var literals = new Dictionary<string, JsonElement>
            {
                ["host"] = JsonSerializer.SerializeToElement(connection.Host),
                ["port"] = JsonSerializer.SerializeToElement(connection.Port),
                ["database"] = JsonSerializer.SerializeToElement(connection.Database),
                ["username"] = JsonSerializer.SerializeToElement(connection.Username)
            };
            var service = (await services.GetRequiredService<ConnectedServiceManagementService>().CreateAsync(new()
            {
                ApiVersion = ResourceApiVersions.CoreV1,
                Kind = ConnectedServiceResourceKinds.ConnectedService,
                ScopeRef = owner,
                Metadata = new() { Name = "documents-connection-" + suffix },
                Definition = new()
                {
                    DisplayName = "Independent typed exports",
                    Options = Json("{}"),
                    Type = new() { Type = new(serviceType.Name, owner, ns), TypeUid = serviceType.Uid, Version = serviceVersion.Version, SchemaDigest = serviceVersion.SchemaDigest },
                    Bindings = [.. literals.Select(p => new ConnectedServiceValueBinding { InputId = p.Key, Kind = ConnectedServiceBindingKind.Literal, Literal = p.Value }),
                        new() { InputId = "password", Kind = ConnectedServiceBindingKind.Secret,
                            Secret = new(new(ResourceNamespace.Default, SecretResourceKinds.Secret, passwordSecret), owner) }]
                }
            }, default)).Value;
            var version = type.Definition.Versions.Single();
            var storage = (await services.GetRequiredService<StorageManagementService>().CreateAsync(new()
            {
                ApiVersion = ResourceApiVersions.CoreV1,
                Kind = StorageResourceKinds.Storage,
                ScopeRef = owner,
                Metadata = new() { Name = "documents-" + suffix },
                Definition = new()
                {
                    DisplayName = "Structured fixture",
                    Type = new()
                    {
                        Type = new(type.Name, owner, ns),
                        TypeUid = type.Uid,
                        Version = version.Version,
                        SchemaDigest = version.SchemaDigest,
                        ContractDigest = version.ContractDigest
                    },
                    ConnectedService = new() { Service = new(service.Address, owner), ServiceUid = service.Uid, Generation = service.Generation },
                    ConnectedServiceType = service.Definition.Type,
                    Bindings = version.Requirements.Select(r => new StorageExportBinding(r.Id, serviceVersion.Exports.Single(e => e.Id == r.Id))).ToArray(),
                    Configuration = Json("{}"),
                    Envelope = Json("""{"get":true,"put":true,"delete":true,"query":true,"publish":true,"queryProfiles":["odata/4.01"]}"""),
                    Collections = [new() { Name = "shared", DisplayName = "Shared", Schema = Schema,
                        Policy = new(Put: true, Delete: true, Publish: true, QueryProfiles: ["odata/4.01"]) },
                        new() { Name = "private", DisplayName = "Private", Schema = Schema,
                            Policy = new(Put: true, QueryProfiles: ["odata/4.01"]) }]
                }
            }, default)).Value;
            var reference = new StorageRevisionReference(new(owner, storage.Namespace, storage.Kind, storage.Name), storage.Uid, storage.Generation, StorageDigests.Binding(storage.Definition));
            return new(app, storage, reference, factory);
        }
        catch { await app.StopAsync(); await app.DisposeAsync(); throw; }
    }

    internal sealed class BindingFactory(bool postgres, HttpClient secrets) : IAspDocumentAepBindingFactory
    {
        private readonly ConcurrentDictionary<string, MemoryDocumentProvider> providers = new(StringComparer.Ordinal);
        public int Configurations { get; private set; }
        public AepStorageBindingRequest? Last { get; private set; }
        public IReadOnlyList<AepStorageTypeContribution> Contributions => PostgreSqlStorageTypes.Contributions;
        public async ValueTask<AspDocumentBinding> CreateAsync(AepStorageBindingRequest request, ClaimsPrincipal caller, CancellationToken ct)
        {
            Configurations++; Last = request;
            var production = new PostgreSqlBindingFactory(new Options(), new Clients(secrets));
            var binding = await production.CreateAsync(request, caller, ct);
            if (postgres) return binding;
            var memory = providers.GetOrAdd(request.DocumentSelection!.Value.GetProperty("collection").GetString()!, _ => new(Schema, TimeProvider.System));
            return binding with { Provider = new MemoryIdentity(memory) };
        }
    }
    private sealed class Options : IOptionsMonitor<PostgreSqlOptions>
    {
        public PostgreSqlOptions CurrentValue { get; } = new() { AllowedClients = ["product"], AllowLoopbackWithoutTls = true };
        public PostgreSqlOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<PostgreSqlOptions, string?> listener) => null;
    }
    private sealed class Clients(HttpClient client) : IHttpClientFactory { public HttpClient CreateClient(string name) => new(new ForwardingHandler(client), true); }
    private sealed class ForwardingHandler(HttpClient client) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            using var forwarded = new HttpRequestMessage(request.Method, request.RequestUri);
            foreach (var header in request.Headers) forwarded.Headers.TryAddWithoutValidation(header.Key, header.Value);
            if (request.Content is not null)
            {
                forwarded.Content = new ByteArrayContent(await request.Content.ReadAsByteArrayAsync(ct));
                foreach (var header in request.Content.Headers) forwarded.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
            return await client.SendAsync(forwarded, ct);
        }
    }
    private sealed class MemoryIdentity(MemoryDocumentProvider inner) : IAspDocumentProvider, IAspDocumentPublisher
    {
        public string ProviderId => "postgresql-jsonb";
        public string ProviderVersion => "1.0.0";
        public JsonElement ConfigurationSchema => PostgreSqlStorageTypes.ConfigurationSchema;
        public JsonElement DocumentSchema => inner.DocumentSchema;
        public IReadOnlyList<AspQueryProfileDescriptor> QueryProfiles => inner.QueryProfiles;
        public ValueTask<AspDocument> GetAsync(AspDocumentContext c, AspDocumentGet r, CancellationToken ct) => inner.GetAsync(c, r, ct);
        public ValueTask<AspDocument> PutAsync(AspDocumentContext c, AspDocumentPut r, CancellationToken ct) => inner.PutAsync(c, r, ct);
        public ValueTask<AspDocumentDeleted> DeleteAsync(AspDocumentContext c, AspDocumentDelete r, CancellationToken ct) => inner.DeleteAsync(c, r, ct);
        public ValueTask<AspDocumentProviderPage> QueryAsync(AspDocumentContext c, AspDocumentQuery r, string? p, CancellationToken ct) => inner.QueryAsync(c, r, p, ct);
        public ValueTask<AspDocumentPublication> PrepareAsync(AspDocumentContext c, AspDocumentPrepare r, CancellationToken ct) => inner.PrepareAsync(c, r, ct);
        public ValueTask<AspDocumentPublication> SealAsync(AspDocumentContext c, AspDocumentPublicationRequest r, CancellationToken ct) => inner.SealAsync(c, r, ct);
        public ValueTask<AspDocumentPublication> InspectAsync(AspDocumentContext c, AspDocumentPublicationRequest r, CancellationToken ct) => inner.InspectAsync(c, r, ct);
        public ValueTask RetireAsync(AspDocumentContext c, AspDocumentPublicationRequest r, CancellationToken ct) => inner.RetireAsync(c, r, ct);
    }
}
