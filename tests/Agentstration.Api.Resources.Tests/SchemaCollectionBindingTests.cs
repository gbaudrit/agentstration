using Agentstration.Identity.Contracts;
using Agentstration.Resources;
using Agentstration.Schemas;
using Agentstration.Schemas.Contracts;
using Agentstration.Storage;
using Agentstration.Storage.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Management.Tests;

[TestClass]
public sealed class SchemaCollectionBindingTests : ResourceManagementApiTestBase
{
    [TestMethod]
    public async Task CollectionAdoptsExactParentSubschemaAndProviderEnforcesItWithoutRegistryLookups()
    {
        await using var product = Factory().WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(
            new Dictionary<string, string?> { ["Agentstration:Aep:SecretAccess:PublicBaseUrl"] = "http://localhost" })));
        var context = await GetBootstrapContextAsync(product);
        using var caller = product.Services.GetRequiredService<IRequestContextScopeFactory>().Push(context);
        var owner = ResourceScopeRef.Workspace(context.WorkspaceId);
        using var secretClient = product.CreateClient();
        await using var fixture = await StructuredStorageFixture.StartAsync(product.Services, secretClient, owner, false);
        var schemas = product.Services.GetRequiredService<SchemaManagementService>();
        var schema = await schemas.CreateAsync(new()
        {
            ApiVersion = ResourceApiVersions.CoreV1, Kind = SchemaResourceKinds.Schema, ScopeRef = owner,
            Metadata = new() { Name = "observations" }, Definition = new()
            {
                DisplayName = "Observations", Format = SchemaFormatIds.JsonSchema,
                Content = StructuredStorageFixture.Json("""
                    {"type":"object","properties":{"results":{"type":"array","items":{"$ref":"#/$defs/observation"}}},
                    "$defs":{"observation":{"type":"object","properties":{"status":{"type":"string"},"value":{"type":"number"}},"required":["status","value"]}}}
                    """)
            }
        }, default);
        var versionA = await schemas.PublishAsync(owner, schema.Value.Namespace, schema.Value.Name, "A", default);
        var storages = product.Services.GetRequiredService<StorageManagementService>();
        var current = (await storages.GetExactAsync(owner, fixture.Storage.Namespace, fixture.Storage.Name, default))!;
        var definition = current.Value.Definition with { Collections = current.Value.Definition.Collections.Select(c => c.Uid != fixture.Collection ? c : c with
        {
            SchemaBinding = new() { Reference = new() { Name = schema.Value.Name, Version = "A", ScopeRef = owner, SchemaPath = "/properties/results/items" } }
        }).ToArray() };
        var adopted = await storages.ReconcileAsync(owner, current.Value.Namespace, current.Value.Name, definition, current.ETag, default);
        var collection = adopted.Value.Definition.Collections.Single(c => c.Uid == fixture.Collection);
        Assert.AreEqual(versionA.Value.DefinitionHash, collection.SchemaSnapshot!.ParentDefinitionHash);
        Assert.AreEqual("A", collection.SchemaSnapshot.Source!.Version);
        Assert.IsFalse(collection.Schema.TryGetProperty("$ref", out _));

        _ = await schemas.PutAsync(owner, schema.Value.Namespace, schema.Value.Name, schema.Value.Definition with
        { Content = StructuredStorageFixture.Json("""{"type":"object","properties":{"results":{"type":"array","items":{"type":"object","required":["new_field"]}}}}""") }, schema.ETag, default);
        _ = await schemas.PublishAsync(owner, schema.Value.Namespace, schema.Value.Name, "B", default);
        var target = await product.Services.GetRequiredService<StorageDocumentService>().ResolveTargetAsync(owner,
            adopted.Value.Namespace, adopted.Value.Name, collection.Uid, default);
        var gateway = product.Services.GetRequiredService<IStorageDocumentGateway>();
        var description = await gateway.DescribeAsync(target, default);
        Assert.AreEqual("status", description.Schema.GetProperty("required")[0].GetString());
        _ = await gateway.PutAsync(target, new("schema-valid", DateTimeOffset.UtcNow.AddMinutes(1), "valid",
            StructuredStorageFixture.Json("""{"status":"ok","value":42}""")), default);
        var failure = await Assert.ThrowsAsync<StorageException>(() => gateway.PutAsync(target,
            new("schema-invalid", DateTimeOffset.UtcNow.AddMinutes(1), "invalid", StructuredStorageFixture.Json("""{"value":42}""")), default));
        Assert.AreEqual("storage_documents_schema_validation", failure.Code);
    }

    [TestMethod]
    public async Task IncompatibleDocumentSchemaIsRejectedBeforePhysicalAccess()
    {
        await using var product = Factory().WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(
            new Dictionary<string, string?> { ["Agentstration:Aep:SecretAccess:PublicBaseUrl"] = "http://localhost" })));
        var context = await GetBootstrapContextAsync(product);
        using var caller = product.Services.GetRequiredService<IRequestContextScopeFactory>().Push(context);
        using var secretClient = product.CreateClient();
        var owner = ResourceScopeRef.Workspace(context.WorkspaceId);
        await using var fixture = await StructuredStorageFixture.StartAsync(product.Services, secretClient, owner, false);
        var storages = product.Services.GetRequiredService<StorageManagementService>();
        var current = (await storages.GetExactAsync(owner, fixture.Storage.Namespace, fixture.Storage.Name, default))!;
        var definition = current.Value.Definition with { Collections = current.Value.Definition.Collections.Select(c => c with
        { Schema = StructuredStorageFixture.Json("""{"type":"object","properties":{"status":{"type":"string","pattern":"^ok$"}}}""") }).ToArray() };
        var failure = await Assert.ThrowsAsync<StorageException>(() => storages.ReconcileAsync(owner,
            current.Value.Namespace, current.Value.Name, definition, current.ETag, default));
        Assert.AreEqual("storage_collection_schema_invalid", failure.Code);
        Assert.AreEqual(0, fixture.Bindings.Configurations);
    }
}
