using System.Text.Json;
using Agentstration.DataSources.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.ResourceManagement.Storage.Sqlite;
using Agentstration.Resources;
using Agentstration.Storage.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Management.Tests;

[TestClass]
public sealed class DataSourceQueryPersistenceTests
{
    [TestMethod]
    public async Task QueryDraftAndIndependentImmutablePublicationSurviveARealStoreRestart()
    {
        var directory = Path.Combine(Path.GetTempPath(), "agentstration-query-persistence", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var connection = "Data Source=" + Path.Combine(directory, "management.db");
        var scope = ResourceScopeRef.Instance;
        StoredResource<DataSourceQueryResource> saved;
        StoredResource<DataSourceQueryVersionResource> published;
        try
        {
            await using (var provider = Provider(connection))
            {
                var store = provider.GetRequiredService<IResourceStore>(); await store.InitializeAsync(default);
                saved = await store.PutExactAsync(scope, new DataSourceQueryResource
                {
                    ApiVersion = ResourceApiVersions.CoreV1, Kind = DataSourceResourceKinds.DataSourceQuery, ScopeRef = scope,
                    Metadata = new() { Name = "alerts-active" }, Generation = 1,
                    Definition = new() { DisplayName = "Active alerts", Query = JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["$filter"] = "status eq 'active'" }),
                        Selection = new() { Source = new("alerts", scope, ResourceNamespace.Default), SourceUid = Guid.NewGuid(), AcquisitionScope = scope,
                            Contract = new(new StorageQueryProfilePin("odata/4.01", "1.0", "odata-options+json", "model")) } }
                }, null, true, default);
                published = await store.CreateImmutableAsync(new DataSourceQueryVersionResource
                {
                    ApiVersion = ResourceApiVersions.CoreV1, Kind = DataSourceResourceKinds.DataSourceQueryVersion, ScopeRef = scope,
                    Metadata = new() { Name = "alerts-active-version-a" }, Generation = 1, QueryUid = saved.Value.Uid,
                    QueryName = saved.Value.Name, QueryGeneration = saved.Value.Generation, Version = "A", Definition = saved.Value.Definition,
                    ContractDigest = "contract", DefinitionDigest = "definition", PublishedAt = DateTimeOffset.UnixEpoch
                }, default);
                saved = await store.PutExactAsync(scope, saved.Value with { Generation = 2, Definition = saved.Value.Definition with
                { Query = JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["$filter"] = "status eq 'closed'" }) } }, saved.ETag, false, default);
            }
            await using (var restarted = Provider(connection))
            {
                var store = restarted.GetRequiredService<IResourceStore>(); await store.InitializeAsync(default);
                var draft = (await store.GetExactAsync<DataSourceQueryResource>(new(scope, default, DataSourceResourceKinds.DataSourceQuery, saved.Value.Name), default))!;
                var version = (await store.GetExactAsync<DataSourceQueryVersionResource>(new(scope, default, DataSourceResourceKinds.DataSourceQueryVersion, published.Value.Name), default))!;
                Assert.AreEqual(saved.Value.Uid, draft.Value.Uid); Assert.AreEqual(saved.ETag, draft.ETag);
                Assert.AreEqual("status eq 'closed'", draft.Value.Definition.Query.GetProperty("$filter").GetString());
                Assert.AreEqual("status eq 'active'", version.Value.Definition.Query.GetProperty("$filter").GetString());
                Assert.AreEqual(draft.Value.Uid, version.Value.QueryUid);
                Assert.AreEqual(published.Value.Uid, version.Value.Uid);
                await Assert.ThrowsAsync<InvalidOperationException>(() => store.PutExactAsync(scope,
                    version.Value with { Version = "B" }, version.ETag, false, default));
            }
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(directory, true); }
    }
    private static ServiceProvider Provider(string connection)
    {
        var services = new ServiceCollection(); services.AddSingleton(TimeProvider.System);
        services.AddSingleton<ICurrentRequestContext, SystemOperationRequestContext>(); services.AddSqliteResourceManagement(connection);
        return services.BuildServiceProvider();
    }
}
