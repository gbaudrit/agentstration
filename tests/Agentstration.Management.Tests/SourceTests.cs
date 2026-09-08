using System.Net;
using System.Net.Http.Json;
using Agentstration.Infrastructure.Sources;
using Agentstration.Management.Abstractions;
using Agentstration.Management.Contracts;
using Agentstration.Management.Core;
using Agentstration.Management.Storage.Sqlite;
using Agentstration.Resources;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Management.Tests;

[TestClass]
public sealed class SourceTests
{
    [TestMethod]
    public void SourceResourceFamilySupportsEveryOwnershipScope()
    {
        var kinds = new[]
        {
            ResourceKinds.Source,
            ResourceKinds.SourceVersion,
            ResourceKinds.SourceConfiguration,
            ResourceKinds.SourceObservedState,
            ResourceKinds.SourceImportRecord
        };

        foreach (var kind in kinds)
            CollectionAssert.AreEquivalent(
                new[] { ResourceScopeKind.Instance, ResourceScopeKind.Tenant, ResourceScopeKind.Workspace },
                ResourceScopePolicy.AllowedScopes(kind).ToArray());
    }

    [TestMethod]
    public async Task ImportIsIdempotentAndRejectsSameVersionWithAnotherDigestAsync()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var system = fixture.Context.PushSystem();
        var first = await fixture.Service.ImportYamlAsync(Manifest("1", "Published name", includeChannel: true), default);
        var repeated = await fixture.Service.ImportYamlAsync(Manifest("1", "Published name", includeChannel: true), default);

        Assert.AreEqual(SourceImportOutcome.Created, first.Outcome);
        Assert.AreEqual(SourceImportOutcome.Unchanged, repeated.Outcome);
        Assert.AreEqual(first.Source.Source.Uid, repeated.Source.Source.Uid);
        Assert.AreEqual(first.Version.Uid, repeated.Version.Uid);
        Assert.AreEqual(1, repeated.Source.VersionCount);

        var conflict = await Assert.ThrowsExactlyAsync<SourceVersionConflictException>(() =>
            fixture.Service.ImportYamlAsync(Manifest("1", "Changed without a version change", includeChannel: false), default));
        StringAssert.Contains(conflict.Message, "different manifest digest");
        Assert.HasCount(1, await fixture.Service.ListVersionsAsync("agentstration", "official-samples", default));

        var records = await fixture.Store.ListAllAsync<SourceImportRecordResource>(ResourceKinds.SourceImportRecord, default);
        CollectionAssert.AreEquivalent(
            new[] { SourceImportOutcome.Created, SourceImportOutcome.Unchanged, SourceImportOutcome.Rejected },
            records.Select(value => value.Value.Definition.Outcome).ToArray());
    }

    [TestMethod]
    public async Task NewVersionKeepsHistoryAndNeverOverwritesLocalDisplayNameAsync()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var system = fixture.Context.PushSystem();
        var imported = await fixture.Service.ImportYamlAsync(Manifest("release-a", "Published name", includeChannel: true), default);
        var configured = await fixture.Service.UpdateDisplayNameAsync(
            "agentstration", "official-samples", "My local name", imported.Source.Configuration.ETag!, default);

        var next = await fixture.Service.ImportYamlAsync(Manifest("release-b", "New published name", includeChannel: false), default);
        Assert.AreEqual("My local name", next.Source.Configuration.Definition.DisplayName);
        Assert.AreEqual(configured.Value.Uid, next.Source.Configuration.Uid);
        Assert.AreEqual(2, next.Source.VersionCount);
        var versions = await fixture.Service.ListVersionsAsync("agentstration", "official-samples", default);
        Assert.IsTrue(versions.Single(value => value.Definition.Version == "release-a").Definition.PublishedDefinition.Channels.Any(value => value.Name == "stable"));
        Assert.IsEmpty(versions.Single(value => value.Definition.Version == "release-b").Definition.PublishedDefinition.Channels);
        Assert.IsFalse(typeof(SourceResource).GetProperties().Any(property => property.Name.Equals("ActiveVersion", StringComparison.OrdinalIgnoreCase)));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => fixture.Store.PutAsync(next.Source.Source, next.Source.Source.ETag, false, default));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => fixture.Store.PutAsync(next.Version, next.Version.ETag, false, default));
    }

    [TestMethod]
    public async Task InvalidLaterDefinitionPreservesVersionsAndExposesObservedFailureAsync()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var system = fixture.Context.PushSystem();
        _ = await fixture.Service.ImportYamlAsync(Manifest("1", "Published name", includeChannel: false), default);
        var invalid = Manifest("2", "Published name", includeChannel: true)
            .Replace("binding: git-distribution", "binding: missing", StringComparison.Ordinal);

        var exception = await Assert.ThrowsExactlyAsync<SourceValidationException>(() => fixture.Service.ImportYamlAsync(invalid, default));
        Assert.AreEqual("source_channel_binding_invalid", exception.Code);
        var source = await fixture.Service.GetAsync("agentstration", "official-samples", default);
        Assert.IsNotNull(source);
        Assert.AreEqual(1, source.VersionCount);
        Assert.AreEqual(SourceImportOutcome.Rejected, source.Observed.Definition.LastOutcome);
        Assert.AreEqual("source_channel_binding_invalid", source.Observed.Definition.ErrorCode);
        Assert.IsNotNull(source.Observed.Definition.LastSuccessfulVersionUid);
    }

    [TestMethod]
    public async Task ImportedVersionsSurviveAStoreRestartAsync()
    {
        var database = Path.Combine(Path.GetTempPath(), $"agentstration-source-{Guid.NewGuid():N}.db");
        try
        {
            await using (var first = await Fixture.CreateAsync(database))
            {
                using var system = first.Context.PushSystem();
                _ = await first.Service.ImportYamlAsync(Manifest("2026-09", "Official samples", includeChannel: false), default);
            }
            await using (var restarted = await Fixture.CreateAsync(database))
            {
                using var system = restarted.Context.PushSystem();
                var source = await restarted.Service.GetAsync("agentstration", "official-samples", default);
                Assert.IsNotNull(source);
                Assert.AreEqual(1, source.VersionCount);
                Assert.AreEqual("2026-09", (await restarted.Service.ListVersionsAsync("agentstration", "official-samples", default)).Single().Definition.Version);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(database);
        }
    }

    [TestMethod]
    public void ReaderRequiresOneBoundedNativeEnvelopeAndProducesCanonicalDigest()
    {
        var reader = new SourceManifestReader();
        var parsed = reader.Read(Manifest("1", "Name", includeChannel: false));
        var reordered = reader.Read("""
            definition:
              catalogs: []
              channels: []
              bindings: []
              publisher: { name: agentstration }
              displayName: Name
              version: "1"
            metadata: { name: official-samples }
            kind: SourceVersion
            apiVersion: agentstration.io/v1
            """);
        Assert.AreEqual(parsed.Digest, reordered.Digest);
        Assert.ThrowsExactly<SourceValidationException>(() => reader.Read(Manifest("1", "Name", false) + "\n---\n{}"));
        Assert.ThrowsExactly<SourceValidationException>(() => reader.Read("apiVersion: agentstration.io/v1\nkind: SourceVersion\nmetadata: { name: x }\nspec: {}"));
        Assert.ThrowsExactly<SourceValidationException>(() => reader.Read(Manifest("1", "Name", false).Replace("catalogs: []", "catalogues: []", StringComparison.Ordinal)));
        Assert.ThrowsExactly<SourceValidationException>(() => reader.Read(new string('x', SourceManifestReader.MaximumManifestBytes + 1)));
    }

    [TestMethod]
    public async Task HttpRetrieverStreamsWithinBoundAndRetainsValidatorsAsync()
    {
        var handler = new StubHttpHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Manifest("1", "Name", false)) };
            response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"manifest-1\"");
            response.Content.Headers.LastModified = DateTimeOffset.Parse("2026-09-06T20:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
            return response;
        });
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(1) };
        var retrieved = await new HttpSourceManifestRetriever(client).RetrieveAsync(new Uri("https://sources.example/source.yaml"), default);
        Assert.AreEqual("\"manifest-1\"", retrieved.Origin.ETag);
        Assert.AreEqual("https://sources.example/source.yaml", retrieved.Origin.Url);
        await Assert.ThrowsExactlyAsync<SourceRetrievalException>(() =>
            new HttpSourceManifestRetriever(client).RetrieveAsync(new Uri("file:///tmp/source.yaml"), default));
    }

    [TestMethod]
    public async Task PlatformApiImportsListsAndUpdatesDisplayNameAsync()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.UseEnvironment("Testing"));
        using var client = factory.CreateClient();
        var context = await client.GetFromJsonAsync<ConsoleContextView>("/api/identity/context");
        Assert.IsNotNull(context);
        await factory.Services.GetRequiredService<IIdentityStore>().AddPlatformAdministratorAsync(
            new PlatformAdministrator(context.Context.PrincipalId, DateTimeOffset.UtcNow),
            default);
        var name = $"source-{Guid.NewGuid():N}"[..30];
        var manifest = Manifest("1", "Published name", false).Replace("official-samples", name, StringComparison.Ordinal);
        var response = await client.PostAsJsonAsync("/api/sources/imports/yaml", new ImportSourceYamlRequest(manifest));
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);
        var imported = await response.Content.ReadFromJsonAsync<SourceImportResult>();
        Assert.IsNotNull(imported);
        Assert.AreEqual("Published name", imported.Source.Configuration.Definition.DisplayName);
        var workspaceScope = ResourceScopeRef.Workspace(context.Context.WorkspaceId);
        Assert.AreEqual(workspaceScope, imported.Source.Source.ScopeRef);

        var instanceResponse = await client.PostAsJsonAsync(
            "/api/sources/imports/yaml",
            new ImportSourceYamlRequest(manifest, ResourceScopeRef.Instance));
        Assert.AreEqual(HttpStatusCode.Created, instanceResponse.StatusCode);
        var instance = await instanceResponse.Content.ReadFromJsonAsync<SourceImportResult>();
        Assert.IsNotNull(instance);
        Assert.AreEqual(ResourceScopeRef.Instance, instance.Source.Source.ScopeRef);
        Assert.AreNotEqual(imported.Source.Source.Uid, instance.Source.Source.Uid);

        using var update = new HttpRequestMessage(
            HttpMethod.Put,
            $"/api/sources/agentstration/{name}/display-name?scopeRef={Uri.EscapeDataString(workspaceScope.ToString())}")
        {
            Content = JsonContent.Create(new UpdateSourceDisplayNameRequest("Local name"))
        };
        update.Headers.TryAddWithoutValidation("If-Match", imported.Source.Configuration.ETag);
        var updatedResponse = await client.SendAsync(update);
        updatedResponse.EnsureSuccessStatusCode();
        var updated = await updatedResponse.Content.ReadFromJsonAsync<SourceConfigurationResource>();
        Assert.AreEqual("Local name", updated?.Definition.DisplayName);
    }

    private static string Manifest(string version, string displayName, bool includeChannel) => $$"""
        apiVersion: agentstration.io/v1
        kind: SourceVersion
        metadata:
          name: official-samples
        definition:
          version: "{{version}}"
          displayName: {{displayName}}
          publisher:
            name: agentstration
          bindings:{{(includeChannel ? "\n    - name: git-distribution\n      targetKind: sourceProvider" : " []")}}
          channels:{{(includeChannel ? "\n    - name: stable\n      provider:\n        binding: git-distribution\n      configuration:\n        optionSet: git/source-channel\n        version: \"1.0\"\n        schemaDigest: sha256:test\n        values: {}" : " []")}}
          catalogs: []
        """;

    private sealed class StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(response(request));
    }

    private sealed class StubRetriever : ISourceManifestRetriever
    {
        public Task<RetrievedSourceManifest> RetrieveAsync(Uri source, CancellationToken cancellationToken) =>
            throw new SourceRetrievalException("not_configured", "No test HTTP source is configured.");
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly ServiceProvider services;
        private readonly bool ownsDatabase;
        private readonly string database;
        public SourceManagementService Service => services.GetRequiredService<SourceManagementService>();
        public CurrentRequestContext Context => services.GetRequiredService<CurrentRequestContext>();
        public IControlPlaneStore Store => services.GetRequiredService<IControlPlaneStore>();

        private Fixture(ServiceProvider services, string database, bool ownsDatabase)
        {
            this.services = services;
            this.database = database;
            this.ownsDatabase = ownsDatabase;
        }

        public static async Task<Fixture> CreateAsync(string? database = null)
        {
            var ownsDatabase = database is null;
            database ??= Path.Combine(Path.GetTempPath(), $"agentstration-source-{Guid.NewGuid():N}.db");
            var collection = new ServiceCollection();
            collection.AddSingleton(TimeProvider.System);
            collection.AddSingleton<CurrentRequestContext>();
            collection.AddSingleton<ICurrentRequestContext>(provider => provider.GetRequiredService<CurrentRequestContext>());
            collection.AddSingleton<IRequestContextScopeFactory>(provider => provider.GetRequiredService<CurrentRequestContext>());
            collection.AddSqliteControlPlane($"Data Source={database}");
            collection.AddSingleton(provider => new ResourceScopeOperationService(
                provider.GetRequiredService<CurrentRequestContext>(),
                provider.GetRequiredService<CurrentRequestContext>(),
                null!,
                null!,
                null!,
                provider.GetRequiredService<IResourceScopeResolver>()));
            collection.AddSingleton<ISourceManifestReader, SourceManifestReader>();
            collection.AddSingleton<ISourceManifestRetriever, StubRetriever>();
            collection.AddSingleton<SourceManagementService>();
            var services = collection.BuildServiceProvider();
            var fixture = new Fixture(services, database, ownsDatabase);
            using (fixture.Context.PushSystem()) await fixture.Store.InitializeAsync(default);
            return fixture;
        }

        public async ValueTask DisposeAsync()
        {
            await services.DisposeAsync();
            if (!ownsDatabase) return;
            SqliteConnection.ClearAllPools();
            File.Delete(database);
        }
    }
}
