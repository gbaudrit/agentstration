using System.Text.Json;
using Agentstration.ConnectedServices.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.ResourceManagement.Storage.Sqlite;
using Agentstration.Resources;
using Agentstration.Storage.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Management.Tests;

[TestClass]
public sealed class StoragePersistenceTests
{
    [TestMethod]
    public async Task RestartPreservesExactStorageTypeServiceMappingEnvelopeAndConcurrency()
    {
        var directory = Path.Combine(Path.GetTempPath(), "agentstration-storage-api", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var connection = $"Data Source={Path.Combine(directory, "management.db")}";
        var scope = ResourceScopeRef.Instance;
        StoredResource<StorageResource> saved;
        try
        {
            await using (var first = Provider(connection))
            {
                var store = first.GetRequiredService<IResourceStore>();
                await store.InitializeAsync(default);
                var schema = JsonSerializer.SerializeToElement(new { type = "object" });
                var version = new StorageTypeVersionDescriptor
                {
                    Version = "1.0.0", ConfigurationSchema = schema, SchemaDigest = StorageDigests.Schema(schema),
                    EnvelopeSchema = schema, EnvelopeSchemaDigest = StorageDigests.Schema(schema), RequiresConnectedService = true,
                    Requirements = [new("auth", true, ConnectedServiceValueType.Text, ConnectedServiceValueProtection.Secured)]
                };
                var type = await store.PutExactAsync(scope, new StorageTypeResource
                {
                    ApiVersion = ResourceApiVersions.CoreV1, Kind = StorageResourceKinds.StorageType,
                    Metadata = new() { Name = "persistent" }, ScopeRef = scope, Generation = 1,
                    Definition = new() { DisplayName = "Persistent", PreferredVersion = version.Version, Versions = [version] }
                }, null, true, default);
                saved = await store.PutExactAsync(scope, new StorageResource
                {
                    ApiVersion = ResourceApiVersions.CoreV1, Kind = StorageResourceKinds.Storage,
                    Metadata = new() { Name = "store" }, ScopeRef = scope, Generation = 3,
                    Definition = new()
                    {
                        DisplayName = "Store", Type = new() { Type = new(type.Value.Name, scope), TypeUid = type.Value.Uid,
                            Version = version.Version, SchemaDigest = version.SchemaDigest, ContractDigest = StorageDigests.Version(version) },
                        ConnectedService = new() { Service = new(new(ResourceNamespace.Default, "ConnectedService", "account"), scope), ServiceUid = Guid.NewGuid(), Generation = 7 },
                        ConnectedServiceType = new() { Type = new("account-type", scope), TypeUid = Guid.NewGuid(), Version = "2.0.0", SchemaDigest = "sha256:account" },
                        Bindings = [new("auth", new("credential", ConnectedServiceValueType.Text, ConnectedServiceValueProtection.Secured))],
                        Configuration = JsonSerializer.SerializeToElement(new Dictionary<string, object> { ["ProviderCase"] = "KeepMe" }),
                        Envelope = JsonSerializer.SerializeToElement(new { root = "fixed-envelope" })
                    }
                }, null, true, default);
            }
            await using (var second = Provider(connection))
            {
                var store = second.GetRequiredService<IResourceStore>();
                await store.InitializeAsync(default);
                var restored = await store.GetExactAsync<StorageResource>(new(scope, ResourceNamespace.Default,
                    StorageResourceKinds.Storage, "store"), default);
                Assert.IsNotNull(restored);
                Assert.AreEqual(saved.Value.Uid, restored.Value.Uid);
                Assert.AreEqual(saved.ETag, restored.ETag);
                Assert.AreEqual(3, restored.Value.Generation);
                Assert.AreEqual(saved.Value.Definition.ConnectedService, restored.Value.Definition.ConnectedService);
                Assert.AreEqual(saved.Value.Definition.Bindings.Single(), restored.Value.Definition.Bindings.Single());
                Assert.AreEqual("KeepMe", restored.Value.Definition.Configuration.GetProperty("ProviderCase").GetString());
                Assert.AreEqual(StorageDigests.Binding(saved.Value.Definition), StorageDigests.Binding(restored.Value.Definition));
                var updated = await store.PutExactAsync(scope, restored.Value with { Generation = 4 }, restored.ETag, false, default);
                await Assert.ThrowsAsync<ResourceConcurrencyException>(async () =>
                    await store.PutExactAsync(scope, restored.Value with { Generation = 5 }, restored.ETag, false, default));
                Assert.AreNotEqual(saved.ETag, updated.ETag);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    private static ServiceProvider Provider(string connection)
    {
        var services = new ServiceCollection();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<ICurrentRequestContext, SystemOperationRequestContext>();
        services.AddSqliteResourceManagement(connection);
        return services.BuildServiceProvider();
    }
}
