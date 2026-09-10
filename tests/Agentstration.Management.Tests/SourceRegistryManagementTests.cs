using System.Net;
using System.Net.Http.Json;
using System.Text;
using Agentstration.Management.Abstractions;
using Agentstration.Management.Contracts;
using Agentstration.Management.Core;
using Agentstration.Web.Api.Models;
using Agentstration.Resources;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Management.Tests;

[TestClass]
public sealed class SourceRegistryManagementTests
{
    [TestMethod]
    public async Task OfficialApiIsSeededOfflineAndMutationsRequirePlatformAdministrator()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.UseEnvironment("Testing"));
        using var client = factory.CreateClient();

        var forbidden = await client.GetAsync("/api/sourceregistries");
        Assert.AreEqual(HttpStatusCode.Forbidden, forbidden.StatusCode);
        var context = await client.GetFromJsonAsync<ConsoleContextView>("/api/identity/context");
        Assert.IsNotNull(context);
        await factory.Services.GetRequiredService<IIdentityStore>().AddPlatformAdministratorAsync(
            new PlatformAdministrator(context.Context.PrincipalId, DateTimeOffset.UtcNow),
            default);

        var list = await client.GetFromJsonAsync<ValueResponse<SourceRegistryRegistrationView>>("/api/sourceregistries");
        var official = list!.Value.Single();
        Assert.AreEqual(SourceRegistryWellKnown.OfficialIndexUrl, official.Registration.Definition.IndexUrl.AbsoluteUri);
        Assert.AreEqual(SourceRegistryObservedStatus.NeverFetched, official.Observed.Definition.Status);

        using var update = new HttpRequestMessage(HttpMethod.Put, $"/api/sourceregistries/{SourceRegistryWellKnown.OfficialName}")
        {
            Content = JsonContent.Create(new UpdateOfficialSourceRegistryRequest(
                "https://registry.example/v1/index.json",
                false))
        };
        update.Headers.TryAddWithoutValidation("If-Match", official.Registration.ETag);
        using var response = await client.SendAsync(update);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        var changed = await response.Content.ReadFromJsonAsync<SourceRegistryRegistrationView>();
        Assert.IsNotNull(changed);
        Assert.IsFalse(changed.Registration.Definition.Enabled);
        Assert.AreEqual(SourceRegistryObservedStatus.Disabled, changed.Observed.Definition.Status);

        var audits = await factory.Services.GetRequiredService<ISecurityAuditStore>().ListLatestAsync(20, default);
        Assert.IsTrue(audits.Any(value => value.Action == SecurityAuditActions.SourceRegistryConfigurationUpdated
            && value.ActorPrincipalId == context.Context.PrincipalId));
    }

    [TestMethod]
    public async Task OfficialRegistrationIsSeededOnceAndAdministratorOverridesArePreserved()
    {
        using var fixture = new Fixture();

        var seeded = await fixture.Service.EnsureOfficialAsync(default);
        _ = await fixture.Service.UpdateOfficialAsync(
            new Uri("https://registry.example/v1/index.yaml"),
            enabled: false,
            seeded.Registration.ETag,
            default);

        var afterUpgrade = await fixture.Service.EnsureOfficialAsync(default);

        Assert.AreEqual(SourceRegistryWellKnown.OfficialName, afterUpgrade.Registration.Name);
        Assert.AreEqual("https://registry.example/v1/index.yaml", afterUpgrade.Registration.Definition.IndexUrl.AbsoluteUri);
        Assert.IsFalse(afterUpgrade.Registration.Definition.Enabled);
        Assert.AreEqual(SourceRegistryObservedStatus.Disabled, afterUpgrade.Observed.Definition.Status);
        Assert.AreEqual(2, afterUpgrade.Registration.Generation);
    }

    [TestMethod]
    public async Task RefreshSelectsEveryCompatibleShardAndDoesNotFetchOtherDocuments()
    {
        using var fixture = new Fixture();
        await fixture.Service.EnsureOfficialAsync(default);
        var compatibleJson = RegistryJson("compatible");
        var compatibleYaml = RegistryYaml("prerelease");
        var jsonDigest = new SourceRegistryReader().Read(compatibleJson, "registry-compatible.json").RegistryDigest;
        var yamlDigest = new SourceRegistryReader().Read(compatibleYaml, "registry-prerelease.yaml").RegistryDigest;
        var index = IndexYaml(jsonDigest, yamlDigest);
        fixture.Documents.Enqueue(Document(SourceRegistryWellKnown.OfficialIndexUrl, "index.yaml", index, "\"index-v1\""));
        fixture.Documents.Enqueue(Document("https://registry.agentstration.io/v1/registry-compatible.json", "registry-compatible.json", compatibleJson));
        fixture.Documents.Enqueue(Document("https://registry.agentstration.io/v1/registry-prerelease.yaml", "registry-prerelease.yaml", compatibleYaml));

        var refreshed = await fixture.Service.RefreshOfficialAsync(default);

        Assert.AreEqual(SourceRegistryObservedStatus.Fresh, refreshed.Observed.Definition.Status);
        Assert.AreEqual(SourceRegistryRefreshOutcome.Succeeded, refreshed.Observed.Definition.LastOutcome);
        Assert.AreEqual(2, refreshed.Observed.Definition.Current!.Catalogs.Count);
        CollectionAssert.AreEqual(
            new[]
            {
                SourceRegistryWellKnown.OfficialIndexUrl,
                "https://registry.agentstration.io/v1/registry-compatible.json",
                "https://registry.agentstration.io/v1/registry-prerelease.yaml"
            },
            fixture.Documents.Requests.Select(value => value.Url.AbsoluteUri).ToArray());
        var cached = await fixture.Cache.GetAsync(refreshed.Observed.Definition.Current.Id, default);
        Assert.IsNotNull(cached);
        Assert.AreEqual(2, cached.Catalogs.Count);
        Assert.AreEqual(1, (await fixture.Service.ListRefreshesAsync(SourceRegistryWellKnown.OfficialName, 10, default)).Count);
    }

    [TestMethod]
    public async Task FailedReplacementPreservesLastKnownGoodAndRecordsActionableState()
    {
        using var fixture = new Fixture();
        await fixture.Service.EnsureOfficialAsync(default);
        var shard = RegistryJson("compatible");
        var digest = new SourceRegistryReader().Read(shard, "registry-compatible.json").RegistryDigest;
        fixture.Documents.Enqueue(Document(SourceRegistryWellKnown.OfficialIndexUrl, "index.json", IndexJson(digest), "\"index-v1\""));
        fixture.Documents.Enqueue(Document("https://registry.agentstration.io/v1/registry-compatible.json", "registry-compatible.json", shard));
        var first = await fixture.Service.RefreshOfficialAsync(default);
        var observationId = first.Observed.Definition.Current!.Id;
        fixture.Documents.Failure = new SourceRetrievalException("source_registry_timeout", "The registry request timed out.");

        var failure = await Assert.ThrowsAsync<SourceRegistryOperationException>(() => fixture.Service.RefreshOfficialAsync(default));
        var afterFailure = await fixture.Service.GetAsync(SourceRegistryWellKnown.OfficialName, default);

        Assert.AreEqual("source_registry_timeout", failure.Code);
        Assert.AreEqual(SourceRegistryObservedStatus.Stale, afterFailure!.Observed.Definition.Status);
        Assert.AreEqual(SourceRegistryRefreshOutcome.Unavailable, afterFailure.Observed.Definition.LastOutcome);
        Assert.AreEqual("source_registry_timeout", afterFailure.Observed.Definition.LastErrorCode);
        Assert.AreEqual(observationId, afterFailure.Observed.Definition.Current!.Id);
        Assert.IsNotNull(await fixture.Cache.GetAsync(observationId, default));
        Assert.AreEqual(2, (await fixture.Service.ListRefreshesAsync(SourceRegistryWellKnown.OfficialName, 10, default)).Count);
    }

    [TestMethod]
    public async Task NotModifiedSendsValidatorsAndKeepsCatalogueProvenance()
    {
        using var fixture = new Fixture();
        await fixture.Service.EnsureOfficialAsync(default);
        var shard = RegistryJson("compatible");
        var digest = new SourceRegistryReader().Read(shard, "registry-compatible.json").RegistryDigest;
        var lastModified = new DateTimeOffset(2026, 9, 1, 10, 0, 0, TimeSpan.Zero);
        fixture.Documents.Enqueue(Document(SourceRegistryWellKnown.OfficialIndexUrl, "index.json", IndexJson(digest), "\"index-v1\"", lastModified));
        fixture.Documents.Enqueue(Document("https://registry.agentstration.io/v1/registry-compatible.json", "registry-compatible.json", shard));
        var first = await fixture.Service.RefreshOfficialAsync(default);
        fixture.Documents.Enqueue(new(
            new Uri(SourceRegistryWellKnown.OfficialIndexUrl),
            new Uri(SourceRegistryWellKnown.OfficialIndexUrl),
            "index.json",
            "\"index-v1\"",
            lastModified,
            true,
            null));

        var second = await fixture.Service.RefreshOfficialAsync(default);
        var conditional = fixture.Documents.Requests.Last();

        Assert.AreEqual("\"index-v1\"", conditional.ETag);
        Assert.AreEqual(lastModified, conditional.LastModified);
        Assert.AreEqual(SourceRegistryRefreshOutcome.NotModified, second.Observed.Definition.LastOutcome);
        Assert.AreEqual(first.Observed.Definition.Current!.Id, second.Observed.Definition.Current!.Id);
        Assert.AreEqual(first.Observed.Definition.Current.Catalogs[0], second.Observed.Definition.Current.Catalogs[0]);
    }

    [TestMethod]
    public async Task MissingCompatibleShardHasAnExplicitState()
    {
        using var fixture = new Fixture { Version = { CurrentVersion = "2.0.0" } };
        await fixture.Service.EnsureOfficialAsync(default);
        var shard = RegistryJson("compatible");
        var digest = new SourceRegistryReader().Read(shard, "registry-compatible.json").RegistryDigest;
        fixture.Documents.Enqueue(Document(SourceRegistryWellKnown.OfficialIndexUrl, "index.json", IndexJson(digest)));

        var failure = await Assert.ThrowsAsync<SourceRegistryOperationException>(() => fixture.Service.RefreshOfficialAsync(default));
        var state = await fixture.Service.GetAsync(SourceRegistryWellKnown.OfficialName, default);

        Assert.AreEqual("no_compatible_registry_catalog", failure.Code);
        Assert.AreEqual(SourceRegistryObservedStatus.NoCompatibleCatalog, state!.Observed.Definition.Status);
        Assert.AreEqual(1, fixture.Documents.Requests.Count);
    }

    [TestMethod]
    public async Task DigestMismatchCannotBecomeTheCurrentObservation()
    {
        using var fixture = new Fixture();
        await fixture.Service.EnsureOfficialAsync(default);
        var shard = RegistryJson("compatible");
        fixture.Documents.Enqueue(Document(
            SourceRegistryWellKnown.OfficialIndexUrl,
            "index.json",
            IndexJson("sha256:1111111111111111111111111111111111111111111111111111111111111111")));
        fixture.Documents.Enqueue(Document(
            "https://registry.agentstration.io/v1/registry-compatible.json",
            "registry-compatible.json",
            shard));

        var failure = await Assert.ThrowsAsync<SourceRegistryOperationException>(() => fixture.Service.RefreshOfficialAsync(default));
        var state = await fixture.Service.GetAsync(SourceRegistryWellKnown.OfficialName, default);

        Assert.AreEqual("source_registry_catalog_digest_mismatch", failure.Code);
        Assert.AreEqual(SourceRegistryObservedStatus.Invalid, state!.Observed.Definition.Status);
        Assert.IsNull(state.Observed.Definition.Current);
    }

    [TestMethod]
    public async Task DisabledRegistrationDoesNotReachTheNetwork()
    {
        using var fixture = new Fixture();
        var seeded = await fixture.Service.EnsureOfficialAsync(default);
        _ = await fixture.Service.UpdateOfficialAsync(
            seeded.Registration.Definition.IndexUrl,
            enabled: false,
            seeded.Registration.ETag,
            default);

        var failure = await Assert.ThrowsAsync<SourceRegistryOperationException>(() => fixture.Service.RefreshOfficialAsync(default));
        var state = await fixture.Service.GetAsync(SourceRegistryWellKnown.OfficialName, default);

        Assert.AreEqual("source_registry_disabled", failure.Code);
        Assert.AreEqual(SourceRegistryObservedStatus.Disabled, state!.Observed.Definition.Status);
        Assert.IsEmpty(fixture.Documents.Requests);
    }

    private static RetrievedSourceRegistryDocument Document(
        string url,
        string fileName,
        string content,
        string? etag = null,
        DateTimeOffset? lastModified = null) =>
        new(new(url), new(url), fileName, etag, lastModified, false, content);

    private static string RegistryJson(string name) => """
        {"apiVersion":"agentstration.io/v1","kind":"SourceRegistry","metadata":{"name":"$NAME$"},"definition":{"publishers":[{"name":"agentstration","status":"Official"}],"sources":[{"publisher":"agentstration","name":"sample","latest":"1","versions":[{"version":"1","manifestUrl":"sources/agentstration/sample/1/source.yaml","manifestDigest":"sha256:4b2aa0694ee27bc2b53b49cd52e5c221838f9ceed304fbd15c42ae26e2273b7d"}]}]}}
        """.Replace("$NAME$", name, StringComparison.Ordinal);

    private static string RegistryYaml(string name) => $$"""
        apiVersion: agentstration.io/v1
        kind: SourceRegistry
        metadata:
          name: {{name}}
        definition:
          publishers:
            - name: agentstration
              status: Official
          sources:
            - publisher: agentstration
              name: sample
              latest: '1'
              versions:
                - version: '1'
                  manifestUrl: sources/agentstration/sample/1/source.yaml
                  manifestDigest: sha256:4b2aa0694ee27bc2b53b49cd52e5c221838f9ceed304fbd15c42ae26e2273b7d
        """;

    private static string IndexJson(string digest) => """
        {"apiVersion":"agentstration.io/v1","kind":"SourceRegistryIndex","metadata":{"name":"official"},"definition":{"catalogs":[{"name":"compatible","compatibility":{"agentstration":{"minVersion":"0.2.0-alpha.1","maxVersionExclusive":"0.3.0"}},"registryUrl":"registry-compatible.json","registryDigest":"$DIGEST$"},{"name":"future","compatibility":{"agentstration":{"minVersion":"1.0.0","maxVersionExclusive":"1.1.0"}},"registryUrl":"registry-future.json","registryDigest":"$DIGEST$"}]}}
        """.Replace("$DIGEST$", digest, StringComparison.Ordinal);

    private static string IndexYaml(string jsonDigest, string yamlDigest) => $$"""
        apiVersion: agentstration.io/v1
        kind: SourceRegistryIndex
        metadata:
          name: official
        definition:
          catalogs:
            - name: compatible
              compatibility:
                agentstration:
                  minVersion: 0.2.0
                  maxVersionExclusive: 0.3.0
              registryUrl: registry-compatible.json
              registryDigest: {{jsonDigest}}
            - name: prerelease
              compatibility:
                agentstration:
                  minVersion: 0.2.0-alpha.1
                  maxVersionExclusive: 0.3.0
              registryUrl: registry-prerelease.yaml
              registryDigest: {{yamlDigest}}
            - name: future
              compatibility:
                agentstration:
                  minVersion: 1.0.0
              registryUrl: registry-future.json
              registryDigest: {{jsonDigest}}
        """;

    private sealed class Fixture : IDisposable
    {
        private readonly IDisposable systemScope;

        public Fixture()
        {
            var context = new CurrentRequestContext();
            systemScope = context.PushSystem();
            Store = new MemoryStore();
            Documents = new FakeDocuments();
            Cache = new MemoryCache();
            Version = new FakeVersion();
            Audit = new FakeAudit();
            var operations = new ResourceScopeOperationService(context, context, null!, null!, null!, new InstanceScopeResolver());
            Service = new(
                Store,
                new SourceRegistryIndexReader(),
                new SourceRegistryReader(),
                new SourceRegistryRuntimeReferenceResolver(),
                Documents,
                Cache,
                Version,
                operations,
                context,
                context,
                Audit,
                TimeProvider.System);
        }

        public MemoryStore Store { get; }
        public FakeDocuments Documents { get; }
        public MemoryCache Cache { get; }
        public FakeVersion Version { get; }
        public FakeAudit Audit { get; }
        public SourceRegistryManagementService Service { get; }
        public void Dispose() => systemScope.Dispose();
    }

    private sealed class FakeDocuments : ISourceRegistryDocumentRetriever
    {
        private readonly Queue<RetrievedSourceRegistryDocument> responses = new();
        public List<(Uri Url, string? ETag, DateTimeOffset? LastModified)> Requests { get; } = [];
        public Exception? Failure { get; set; }
        public void Enqueue(RetrievedSourceRegistryDocument response) => responses.Enqueue(response);

        public Task<RetrievedSourceRegistryDocument> RetrieveAsync(Uri source, string? etag, DateTimeOffset? lastModified, int maximumBytes, CancellationToken cancellationToken)
        {
            Requests.Add((source, etag, lastModified));
            if (Failure is not null) throw Failure;
            var response = responses.Dequeue();
            Assert.AreEqual(source, response.RequestedUrl);
            Assert.IsTrue(Encoding.UTF8.GetByteCount(response.Content ?? string.Empty) <= maximumBytes);
            return Task.FromResult(response);
        }
    }

    private sealed class MemoryCache : ISourceRegistryCacheStore
    {
        private readonly Dictionary<Guid, SourceRegistryCachedPublication> values = [];
        public Task StoreAsync(SourceRegistryCachedPublication publication, CancellationToken cancellationToken)
        {
            values.Add(publication.ObservationId, publication);
            return Task.CompletedTask;
        }
        public Task<SourceRegistryCachedPublication?> GetAsync(Guid observationId, CancellationToken cancellationToken) =>
            Task.FromResult(values.GetValueOrDefault(observationId));
    }

    private sealed class FakeVersion : IAgentstrationVersionProvider
    {
        public string? CurrentVersion { get; set; } = "0.2.0";
    }

    private sealed class FakeAudit : ISecurityAuditWriter
    {
        public List<SecurityAuditWrite> Entries { get; } = [];
        public Task WriteAsync(SecurityAuditWrite entry, CancellationToken cancellationToken)
        {
            Entries.Add(entry);
            return Task.CompletedTask;
        }
    }

    private sealed class InstanceScopeResolver : IResourceScopeResolver
    {
        private static readonly ResourceScope Instance = new(1, ResourceScopeRef.Instance, ResourceScopeKind.Instance, "instance", null);
        public Task<ResolvedResourceScope?> ResolveAsync(ResourceScopeRef scopeRef, CancellationToken cancellationToken) =>
            Task.FromResult<ResolvedResourceScope?>(scopeRef == ResourceScopeRef.Instance ? new(Instance, []) : null);
    }

    private sealed class MemoryStore : IControlPlaneStore
    {
        private readonly Dictionary<ScopedResourceAddress, (Resource Value, string ETag, DateTimeOffset At)> values = [];
        private long version;
        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<StoredResource<T>?> GetAsync<T>(ResourceKey key, CancellationToken cancellationToken) where T : Resource => GetExactAsync<T>(key.AtScope(ResourceScopeRef.Instance), cancellationToken);
        public Task<StoredResource<T>?> GetExactAsync<T>(ScopedResourceAddress address, CancellationToken cancellationToken) where T : Resource =>
            Task.FromResult(values.TryGetValue(address, out var entry) && entry.Value is T typed ? new StoredResource<T>(typed, entry.ETag, entry.At) : null);
        public Task<IReadOnlyList<StoredResource<T>>> ListAsync<T>(string kind, int skip, int take, CancellationToken cancellationToken) where T : Resource => ListExactAsync<T>(ResourceScopeRef.Instance, kind, skip, take, cancellationToken);
        public Task<IReadOnlyList<StoredResource<T>>> ListExactAsync<T>(ResourceScopeRef scopeRef, string kind, int skip, int take, CancellationToken cancellationToken) where T : Resource =>
            Task.FromResult<IReadOnlyList<StoredResource<T>>>(values.Where(value => value.Key.ScopeRef == scopeRef && value.Key.Address.Kind == kind && value.Value.Value is T).Select(value => new StoredResource<T>((T)value.Value.Value, value.Value.ETag, value.Value.At)).Skip(skip).Take(take).ToArray());
        public Task<IReadOnlyList<StoredResource<T>>> ListVisibleAsync<T>(ResourceScopeRef targetScopeRef, string kind, int skip, int take, CancellationToken cancellationToken) where T : Resource => ListExactAsync<T>(targetScopeRef, kind, skip, take, cancellationToken);
        public Task<StoredResource<T>> PutAsync<T>(T resource, string? ifMatch, bool ifNoneMatch, CancellationToken cancellationToken) where T : Resource => PutExactAsync(resource.ScopeRef ?? ResourceScopeRef.Instance, resource, ifMatch, ifNoneMatch, cancellationToken);
        public Task<StoredResource<T>> PutExactAsync<T>(ResourceScopeRef scopeRef, T resource, string? ifMatch, bool ifNoneMatch, CancellationToken cancellationToken) where T : Resource
        {
            var key = ScopedResourceAddress.Create(scopeRef, resource.Namespace, resource.Kind, resource.Name);
            if (ifNoneMatch && values.ContainsKey(key)) throw new ControlPlaneConcurrencyException("Already exists.");
            if (ifMatch is not null && (!values.TryGetValue(key, out var current) || current.ETag != ifMatch)) throw new ControlPlaneConcurrencyException("ETag mismatch.");
            var etag = $"\"{Interlocked.Increment(ref version)}\"";
            var value = resource.WithSystemState(resource.Uid == Guid.Empty ? Guid.NewGuid() : resource.Uid, scopeRef, etag);
            values[key] = (value, etag, DateTimeOffset.UnixEpoch);
            return Task.FromResult(new StoredResource<T>((T)value, etag, DateTimeOffset.UnixEpoch));
        }
        public Task<StoredResource<T>> CreateImmutableAsync<T>(T resource, CancellationToken cancellationToken) where T : Resource => PutAsync(resource, null, true, cancellationToken);
        public Task DeleteAsync(ResourceKey key, string? ifMatch, CancellationToken cancellationToken) => DeleteExactAsync(key.AtScope(ResourceScopeRef.Instance), ifMatch, cancellationToken);
        public Task DeleteExactAsync(ScopedResourceAddress address, string? ifMatch, CancellationToken cancellationToken) { values.Remove(address); return Task.CompletedTask; }
    }
}
