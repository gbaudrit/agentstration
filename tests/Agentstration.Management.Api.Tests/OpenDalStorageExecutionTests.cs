using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Agentstration.Aep.Abstractions;
using Agentstration.Aep.AspNetCore;
using Agentstration.ConnectedServices;
using Agentstration.ConnectedServices.Contracts;
using Agentstration.Extensions.Aep;
using Agentstration.Extensions.Contracts;
using Agentstration.Extensions.OpenDal;
using Agentstration.Identity.Contracts;
using Agentstration.Infrastructure.Storage;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Secrets;
using Agentstration.Secrets.Abstractions;
using Agentstration.Storage;
using Agentstration.Storage.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Agentstration.Management.Tests;

[TestClass]
public sealed class OpenDalStorageExecutionTests : ModelManagementApiTestBase
{
    [TestMethod]
    public async Task ContributedOpenDalTypeCreatesProductStoragesAndExecutesRealMcpWithRevocationAndDisablement()
    {
        // Public disposable test credential; never production data.
        const string credential = "public-disposable-opendal-product-fixture-1039";
        var root = Path.Combine(Path.GetTempPath(), "opendal-product-" + Guid.NewGuid().ToString("N"));
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OpenDal:RootDirectory"] = root,
            ["OpenDal:AllowedClients:0"] = "product",
            ["Aep:EnrollmentMode"] = "Disabled"
        });
        builder.Services.AddOpenDalStorage(builder.Configuration);
        builder.Services.AddAepStaticBearerAuthentication(options => options.AddToken("product", "product", credential));
        await using var extension = builder.Build();
        extension.Urls.Add("http://127.0.0.1:0");
        extension.MapOpenDalStorage();
        await extension.StartAsync();
        try
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", credential);
            var endpoint = new Uri(extension.Urls.Single());
            var manifest = (await http.GetFromJsonAsync<AepManifest>(new Uri(endpoint, AepProtocol.DiscoveryPath), AepProtocol.JsonOptions))!;
            Assert.IsEmpty(AepDescriptorValidator.Validate(manifest));
            await using var product = Factory();
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
                Metadata = new() { Name = "opendal-vault" },
                Definition = new() { DisplayName = "Fixture", ProviderType = "local" }
            }, default);
            await secrets.CreateSecretAsync(new()
            {
                ApiVersion = ResourceApiVersions.CoreV1,
                Kind = SecretResourceKinds.Secret,
                ScopeRef = scope,
                Metadata = new() { Name = "opendal-transport" },
                Definition = new() { DisplayName = "Transport", Vault = new("opendal-vault", scope), Key = "transport" }
            }, default);
            using (var value = new SecretValue(Encoding.UTF8.GetBytes(credential)))
                await secrets.SetValueExactAsync(scope, "opendal-transport", value, default);
            var registration = await resources.PutExactAsync(scope, new ExtensionRegistrationResource
            {
                ApiVersion = ResourceApiVersions.CoreV1,
                Kind = ExtensionKinds.ExtensionRegistration,
                Metadata = new() { Name = "opendal" },
                ScopeRef = scope,
                Generation = 1,
                Definition = new()
                {
                    DisplayName = "OpenDAL",
                    Endpoint = endpoint,
                    ExpectedExtensionId = OpenDalStorageTypes.ExtensionId,
                    AuthenticationMode = AepTransportAuthenticationMode.StaticBearer,
                    Credential = new("opendal-transport", scope)
                }
            }, null, true, default);
            using (services.GetRequiredService<IRequestContextScopeFactory>().PushSystem())
                await services.GetServices<IAepEnrollmentResourceProvisioner>().OfType<AepStorageTypeProvisioner>().Single()
                    .ProvisionAsync(registration.Value, manifest, default);
            var type = (await services.GetRequiredService<StorageTypeManagementService>().GetExactAsync(scope,
                ResourceNamespace.Parse(OpenDalStorageTypes.ExtensionId), OpenDalStorageTypes.FileSystemId, default))!.Value;
            var version = type.Definition.Versions.Single();
            var management = services.GetRequiredService<StorageManagementService>();
            async Task<StorageResource> Create(string name) => (await management.CreateAsync(new()
            {
                ApiVersion = ResourceApiVersions.CoreV1,
                Kind = StorageResourceKinds.Storage,
                ScopeRef = scope,
                Metadata = new() { Name = name },
                Definition = new()
                {
                    DisplayName = name,
                    Type = new()
                    {
                        Type = new(type.Name, scope, type.Namespace),
                        TypeUid = type.Uid,
                        Version = version.Version,
                        SchemaDigest = version.SchemaDigest,
                        ContractDigest = version.ContractDigest
                    },
                    Configuration = JsonSerializer.SerializeToElement(new { }),
                    Envelope = JsonSerializer.SerializeToElement(new { prefix = "notes/", write = true })
                }
            }, default)).Value;
            var first = await Create("opendal-first");
            var second = await Create("opendal-second");
            Assert.IsNull(first.Definition.ConnectedService);
            Assert.IsEmpty(first.Definition.Bindings);
            await VerifyExplicitRemoteMappingsAsync(services, scope);
            var gateway = services.GetRequiredService<IStorageContentGateway>();
            var target = Target(first, scope);
            var write = new StorageWrite("product-write", DateTimeOffset.UtcNow.AddMinutes(2), "notes/a",
                Convert.ToBase64String("real OpenDAL through product"u8));
            var written = await gateway.WriteAsync(target, write);
            Assert.AreEqual(written.Version, (await gateway.WriteAsync(target, write)).Version);
            Assert.AreEqual("real OpenDAL through product", Encoding.UTF8.GetString(Convert.FromBase64String(
                (await gateway.ReadAsync(target, written.Reference, 0, 100)).ContentBase64)));
            Assert.IsEmpty((await gateway.ListAsync(Target(second, scope), "notes/", 10)).Objects);
            var cross = await Assert.ThrowsAsync<StorageException>(() => gateway.ReadAsync(Target(second, scope), written.Reference, 0, 100));
            Assert.AreEqual("storage_content_invalid_reference", cross.Code);

            builder.Configuration["OpenDal:Enabled"] = "false";
            ((IConfigurationRoot)builder.Configuration).Reload();
            await Assert.ThrowsAsync<StorageException>(() => gateway.ListAsync(target, "notes/", 1));
            builder.Configuration["OpenDal:Enabled"] = "true";
            ((IConfigurationRoot)builder.Configuration).Reload();
            Assert.HasCount(1, (await gateway.ListAsync(target, "notes/", 10)).Objects);
            _ = await resources.PutExactAsync(scope, registration.Value with
            { Definition = registration.Value.Definition with { Enabled = false } }, registration.ETag, false, default);
            await Assert.ThrowsAsync<StorageException>(() => gateway.ReadAsync(target, written.Reference, 0, 100));
            Assert.IsEmpty(Directory.GetFiles(root, "objects.json", SearchOption.AllDirectories));
            Assert.HasCount(1, Directory.GetFiles(root, "*.blob", SearchOption.AllDirectories));
        }
        finally
        {
            await extension.StopAsync();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static StorageContentRequest Target(StorageResource resource, ResourceScopeRef scope) => new(
        new(new(scope, resource.Namespace, resource.Kind, resource.Name), resource.Uid, resource.Generation, StorageDigests.Binding(resource.Definition)),
        new(scope, resource.Address), new(Write: true, Prefix: "notes/"));

    private static async Task VerifyExplicitRemoteMappingsAsync(IServiceProvider services, ResourceScopeRef scope)
    {
        var remote = (await services.GetRequiredService<StorageTypeManagementService>().GetExactAsync(scope,
            ResourceNamespace.Parse(OpenDalStorageTypes.ExtensionId), "opendal.s3", default))!.Value;
        var version = remote.Definition.Versions.Single();
        var schema = JsonSerializer.SerializeToElement(new { type = "object", additionalProperties = false });
        var inputs = version.Requirements.Select((r, i) => new ConnectedServiceInputDescriptor("input-" + i, true, r.Type, r.Protection)).ToArray();
        var exports = inputs.Select((input, i) => new ConnectedServiceExportDescriptor("export-" + i,
            input.Type, input.Protection, SourceInputId: input.Id)).ToArray();
        var serviceVersion = new ConnectedServiceTypeVersionDescriptor("1.0.0", StorageDigests.Schema(schema), schema, inputs, exports);
        var serviceType = (await services.GetRequiredService<ConnectedServiceTypeManagementService>().CreateAsync(new()
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = ConnectedServiceResourceKinds.ConnectedServiceType,
            ScopeRef = scope,
            Metadata = new() { Name = "independent-vendor" },
            Definition = new() { DisplayName = "Independent vendor", PreferredVersion = "1.0.0", Versions = [serviceVersion] }
        }, default)).Value;
        var account = (await services.GetRequiredService<ConnectedServiceManagementService>().CreateAsync(new()
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = ConnectedServiceResourceKinds.ConnectedService,
            ScopeRef = scope,
            Metadata = new() { Name = "independent-account" },
            Definition = new()
            {
                DisplayName = "Independent account",
                Options = JsonSerializer.SerializeToElement(new { }),
                Type = new() { Type = new(serviceType.Name, scope), TypeUid = serviceType.Uid, Version = "1.0.0", SchemaDigest = serviceVersion.SchemaDigest },
                Bindings = inputs.Select(input => input.Protection == ConnectedServiceValueProtection.Secured
                    ? new ConnectedServiceValueBinding
                    {
                        InputId = input.Id,
                        Kind = ConnectedServiceBindingKind.Secret,
                        Secret = new(new(ResourceNamespace.Default, SecretResourceKinds.Secret, "opendal-transport"), scope)
                    }
                    : new ConnectedServiceValueBinding
                    {
                        InputId = input.Id,
                        Kind = ConnectedServiceBindingKind.Literal,
                        Literal = JsonSerializer.SerializeToElement("Opaque_VENDOR_Value")
                    }).ToArray()
            }
        }, default)).Value;
        var definition = new StorageProperties
        {
            DisplayName = "Remote mapping fixture",
            Type = new()
            {
                Type = new(remote.Name, scope, remote.Namespace),
                TypeUid = remote.Uid,
                Version = version.Version,
                SchemaDigest = version.SchemaDigest,
                ContractDigest = version.ContractDigest
            },
            ConnectedService = new() { Service = new(account.Address, scope), ServiceUid = account.Uid, Generation = account.Generation },
            ConnectedServiceType = account.Definition.Type,
            Configuration = JsonSerializer.SerializeToElement(new { bucket = "opaque-destination" }),
            Envelope = JsonSerializer.SerializeToElement(new { prefix = "notes/", write = false })
        };
        async Task<StorageResource> Create(string name, StorageProperties properties) =>
            (await services.GetRequiredService<StorageManagementService>().CreateAsync(new()
            {
                ApiVersion = ResourceApiVersions.CoreV1,
                Kind = StorageResourceKinds.Storage,
                ScopeRef = scope,
                Metadata = new() { Name = name },
                Definition = properties
            }, default)).Value;
        var missing = await Assert.ThrowsAsync<StorageException>(() => Create("missing-mapping", definition));
        Assert.AreEqual("storage_mapping_incomplete", missing.Code);
        var mapped = await Create("explicit-mapping", definition with
        {
            Bindings = version.Requirements.Select((r, i) => new StorageExportBinding(r.Id, exports[i])).ToArray()
        });
        var context = await services.GetRequiredService<IStorageResolver>().ResolveAsync(Target(mapped, scope).Storage,
            new(scope, mapped.Address));
        Assert.IsTrue(context.Capabilities.List && context.Capabilities.Read && context.Capabilities.Write);
        Assert.HasCount(4, context.Bindings);
        Assert.AreEqual(definition.Configuration.GetRawText(), context.Configuration.GetRawText());
        Assert.AreEqual(definition.Envelope.GetRawText(), context.Envelope.GetRawText());
        Assert.IsTrue(context.Bindings.All(b => b.RequirementId != b.Export.Id));
    }
}
