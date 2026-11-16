using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentstration.Identity;
using Agentstration.Identity.Contracts;
using Agentstration.Infrastructure.Knowledge;
using Agentstration.Knowledge;
using Agentstration.Knowledge.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Security.Contracts;
using Agentstration.Storage;
using Agentstration.Storage.Contracts;
using Agentstration.Tools;

namespace Agentstration.Infrastructure.Tests;

[TestClass]
public sealed class KnowledgeMetadataTests
{
    [TestMethod]
    [DataRow("workspace", 1, 1)]
    [DataRow("workspace", 8, 256)]
    [DataRow("instance", 8, 256)]
    [DataRow("tenant", 8, 256)]
    public async Task ConsultationUsesOnlyMetadataRegardlessOfHistorySizeOrContentPermissions(string owner, int history, int artifacts)
    {
        var fixture = new Fixture(owner, artifacts);
        using var scope = fixture.Push();
        fixture.Authorization.Denied.UnionWith([AuthorizationPermissions.RunsExecute, AuthorizationPermissions.ArtifactsReadContent]);
        for (var index = 1; index < history; index++)
            fixture.Store.Add(fixture.Snapshot with { Metadata = new() { Name = "older-" + index }, Uid = Guid.NewGuid() });

        var list = await fixture.Snapshots.ListAsync(fixture.Id, default);
        Assert.HasCount(history, list);
        Assert.AreEqual(1, list.Count(value => value.LifecycleState == KnowledgeSnapshotLifecycleState.Active));
        Assert.IsTrue(list.All(value => value.ContentAvailability == KnowledgeContentAvailability.Unknown));
        var detail = await fixture.Snapshots.GetAsync(new(fixture.Snapshot.Name), default);
        Assert.HasCount(artifacts, detail.Snapshot.Artifacts);
        Assert.AreEqual(fixture.Snapshot.Uid, (await fixture.Snapshots.GetActiveAsync(fixture.Id, default))!.Snapshot.Uid);
        var status = await fixture.Access.GetStatusAsync(fixture.Id, new(fixture.Snapshot.Name), default);
        Assert.AreEqual("ready", status.State);
        Assert.AreEqual(KnowledgeContentAvailability.Unknown, status.ContentAvailability);
        Assert.HasCount(artifacts, status.Documents);
        Assert.AreEqual(0, fixture.Publications.Reads.Count);
        Assert.AreEqual(0, fixture.Resolver.Calls);
        Assert.AreEqual(0, fixture.Store.Writes, "Consultation must not create a read lease or preparation.");
    }

    [TestMethod]
    public async Task MetadataPermissionsAndWorkspaceIsolationStillApply()
    {
        var fixture = new Fixture();
        using var scope = fixture.Push();
        fixture.Authorization.Denied.Add(AuthorizationPermissions.RunsRead);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => fixture.Snapshots.ListAsync(fixture.Id, default));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => fixture.Snapshots.GetAsync(new(fixture.Snapshot.Name), default));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => fixture.Access.GetStatusAsync(fixture.Id, new(), default));
        fixture.Authorization.Denied.Clear();
        using var other = fixture.Context.Push(fixture.Caller with { WorkspaceId = Guid.NewGuid() });
        var failure = await Assert.ThrowsAsync<KnowledgeSnapshotException>(() => fixture.Snapshots.GetAsync(new(fixture.Snapshot.Name), default));
        Assert.AreEqual("knowledge_snapshot_not_found", failure.Code);
        Assert.AreEqual(0, fixture.Publications.Reads.Count);
    }

    [TestMethod]
    public async Task ReadingOneDocumentValidatesOnlyThatDocumentAndKeepsItsExactSnapshotAcrossPages()
    {
        var fixture = new Fixture(artifacts: 256);
        using var scope = fixture.Push();
        fixture.Publications.Content["document-0"] = Encoding.UTF8.GetBytes(Fixture.Text);
        var page = await fixture.Access.ReadAsync(fixture.Id, new()
        { SnapshotId = fixture.Snapshot.Name, DocumentId = "document-0", MaxCharacters = 4 }, default);
        Assert.AreEqual("Nant", page.Chunks.Single().Content);
        Assert.IsNotNull(page.NextCursor);
        var second = fixture.Snapshot with { Metadata = new() { Name = "snapshot-b" }, Uid = Guid.NewGuid() };
        fixture.Store.Add(second);
        fixture.Store.Add(fixture.Observed with { ActiveSnapshotName = second.Name, ActiveSnapshotUid = second.Uid });
        var continued = await fixture.Access.ReadAsync(fixture.Id, new() { Cursor = page.NextCursor }, default);
        Assert.AreEqual(fixture.Snapshot.Name, continued.SnapshotId);
        Assert.AreEqual(Fixture.Text, page.Chunks.Single().Content + continued.Chunks.Single().Content);
        CollectionAssert.AreEqual(new[] { "document-0", "document-0" }, fixture.Publications.Reads);
        Assert.AreEqual(0, fixture.Resolver.Calls);
    }

    [TestMethod]
    [DataRow("missing", "storage_publication_unavailable")]
    [DataRow("tampered", "knowledge_access_content_integrity")]
    [DataRow("evidence", "knowledge_snapshot_content_invalid")]
    public async Task ContentFailuresAreDeferredUntilTheSelectedReadAndPreservePublicationMetadata(string mode, string error)
    {
        var fixture = new Fixture(artifacts: 256);
        using var scope = fixture.Push();
        if (mode == "tampered") fixture.Publications.Content["document-0"] = Encoding.UTF8.GetBytes("Xantes records");
        if (mode == "evidence")
        {
            var first = fixture.Snapshot.Artifacts[0];
            fixture.Store.Add(fixture.Snapshot with { Artifacts = [first with { Content = first.Content! with { Length = 99 } }] });
        }
        Assert.AreEqual(KnowledgeSnapshotLifecycleState.Active,
            (await fixture.Snapshots.GetActiveAsync(fixture.Id, default))!.LifecycleState);
        try
        {
            _ = await fixture.Access.ReadAsync(fixture.Id, new() { SnapshotId = fixture.Snapshot.Name, DocumentId = "document-0" }, default);
            Assert.Fail("An invalid selected document must fail at the content boundary.");
        }
        catch (StorageException exception) { Assert.AreEqual(error, exception.Code); }
        catch (KnowledgeAccessException exception) { Assert.AreEqual(error, exception.Code); }
        var active = (await fixture.Snapshots.GetActiveAsync(fixture.Id, default))!;
        Assert.AreEqual(fixture.Snapshot.Uid, active.Snapshot.Uid);
        Assert.AreEqual(KnowledgeSnapshotLifecycleState.Active, active.LifecycleState);
        Assert.AreEqual(KnowledgeContentAvailability.Unknown, active.ContentAvailability);
        Assert.IsTrue(fixture.Publications.Reads.All(key => key == "document-0"));
    }

    [TestMethod]
    public async Task ActualContentAccessRechecksPermissionsEvenAfterMetadataWasRead()
    {
        var fixture = new Fixture();
        using var scope = fixture.Push();
        _ = await fixture.Snapshots.GetAsync(new(fixture.Snapshot.Name), default);
        fixture.Authorization.Denied.Add(AuthorizationPermissions.ArtifactsReadContent);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => fixture.Access.ReadAsync(fixture.Id,
            new() { SnapshotId = fixture.Snapshot.Name, DocumentId = "document-0" }, default));
        Assert.AreEqual(0, fixture.Publications.Reads.Count);
        fixture.Authorization.Denied.Add(AuthorizationPermissions.RunsExecute);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => fixture.Access.ReadAsync(fixture.Id,
            new() { SnapshotId = fixture.Snapshot.Name }, default));
        Assert.AreEqual(KnowledgeContentAvailability.Unknown,
            (await fixture.Access.GetStatusAsync(fixture.Id, new(), default)).ContentAvailability);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task EmptyDocumentsStillValidateTheirExactPhysicalReference(bool exists)
    {
        var fixture = new Fixture();
        using var scope = fixture.Push();
        var artifact = fixture.Snapshot.Artifacts[0];
        var digest = Convert.ToHexStringLower(SHA256.HashData([]));
        fixture.Store.Add(fixture.Snapshot with
        { Artifacts = [artifact with { Length = 0, Sha256 = digest, Content = artifact.Content! with { Length = 0, Sha256 = digest } }] });
        if (exists)
        {
            fixture.Publications.Content[artifact.ArtifactId] = [];
            var result = await fixture.Access.ReadAsync(fixture.Id, new() { SnapshotId = fixture.Snapshot.Name }, default);
            Assert.AreEqual(string.Empty, result.Chunks.Single().Content);
            Assert.IsTrue(result.EndOfSelection);
        }
        else
            await Assert.ThrowsAsync<StorageException>(() => fixture.Access.ReadAsync(fixture.Id,
                new() { SnapshotId = fixture.Snapshot.Name }, default));
        Assert.HasCount(1, fixture.Publications.Reads);
    }

    [TestMethod]
    public async Task RetirementRemainsVisibleWithoutReadingContentAndBlocksNewReads()
    {
        var fixture = new Fixture();
        using var scope = fixture.Push();
        fixture.Store.Add(fixture.Observed with { RetiringSnapshots = [fixture.Snapshot.Uid] });
        Assert.AreEqual(KnowledgeSnapshotLifecycleState.Retiring,
            (await fixture.Snapshots.GetAsync(new(fixture.Snapshot.Name), default)).LifecycleState);
        var retiring = await Assert.ThrowsAsync<KnowledgeAccessException>(() => fixture.Access.ReadAsync(fixture.Id,
            new() { SnapshotId = fixture.Snapshot.Name }, default));
        Assert.AreEqual("knowledge_snapshot_content_retired", retiring.Code);
        fixture.Store.Add(fixture.Observed with
        { RetiredSnapshots = new Dictionary<Guid, DateTimeOffset> { [fixture.Snapshot.Uid] = fixture.Clock.GetUtcNow() } });
        var retired = await fixture.Snapshots.GetAsync(new(fixture.Snapshot.Name), default);
        Assert.AreEqual(KnowledgeSnapshotLifecycleState.Retired, retired.LifecycleState);
        Assert.AreEqual(KnowledgeContentAvailability.Retired, retired.ContentAvailability);
        Assert.IsNotNull(retired.Snapshot.ContentRetiredAt);
        var failure = await Assert.ThrowsAsync<KnowledgeAccessException>(() => fixture.Access.ReadAsync(fixture.Id,
            new() { SnapshotId = fixture.Snapshot.Name }, default));
        Assert.AreEqual("knowledge_snapshot_content_retired", failure.Code);
        Assert.AreEqual(KnowledgeContentAvailability.Retired,
            (await fixture.Access.GetStatusAsync(fixture.Id, new(), default)).ContentAvailability);
        Assert.AreEqual(0, fixture.Publications.Reads.Count);
    }

    [TestMethod]
    public async Task RetrievalRejectsAnotherSourcesSnapshotAndHonorsCancellationWithoutProbing()
    {
        var fixture = new Fixture();
        using var scope = fixture.Push();
        fixture.Store.Add(fixture.Snapshot with { KnowledgeSourceUid = Guid.NewGuid() });
        var failure = await Assert.ThrowsAsync<KnowledgeSnapshotException>(() => fixture.Snapshots.ResolveForRetrievalAsync(
            fixture.Id, fixture.Snapshot.Name, default));
        Assert.AreEqual("knowledge_snapshot_source_mismatch", failure.Code);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => fixture.Snapshots.ListAsync(fixture.Id, cancellation.Token));
        Assert.AreEqual(0, fixture.Publications.Reads.Count);
    }

    [TestMethod]
    public async Task StatusToolSharesTheMetadataOnlyBoundary()
    {
        var fixture = new Fixture(artifacts: 256);
        using var scope = fixture.Push();
        fixture.Authorization.Denied.Add(AuthorizationPermissions.ArtifactsReadContent);
        var tool = new KnowledgeContentStatusMcpTool(fixture.Access, fixture.Sources, fixture.Context);
        var result = await tool.ExecuteAsync(new(fixture.Caller.TenantId, new(fixture.Caller.WorkspaceId),
            fixture.Caller.PrincipalId, "status-call", null, JsonSerializer.SerializeToElement(new
            {
                source = new
                {
                    name = fixture.Source.Name,
                    @namespace = fixture.Source.Namespace.Value,
                    scopeRef = fixture.Source.ScopeRef!.Value.Value,
                    uid = fixture.Source.Uid
                },
                request = new { }
            }), ToolDefinitionCallerKind.Console), default);
        Assert.AreEqual("ready", result!.Value.GetProperty("state").GetString());
        Assert.AreEqual(0, fixture.Publications.Reads.Count);
        Assert.AreEqual(0, fixture.Resolver.Calls);
        Assert.AreEqual(0, fixture.Store.Writes);
    }

    [TestMethod]
    public async Task StructuredDescriptionNeedsNoExecutionPermissionAndQueryValidatesOnlyItsExactPublication()
    {
        var fixture = new Fixture(artifacts: 256, structured: true);
        using var scope = fixture.Push();
        fixture.Authorization.Denied.Add(AuthorizationPermissions.RunsExecute);
        _ = await fixture.Access.DescribeDocumentsAsync(fixture.Id, fixture.Snapshot.Name, default);
        _ = await fixture.Access.GetStatusAsync(fixture.Id, new(), default);
        Assert.AreEqual(0, fixture.Documents.Inspections.Count);
        Assert.AreEqual(0, fixture.Documents.Queries.Count);
        fixture.Authorization.Denied.Clear();
        var result = await fixture.Access.QueryDocumentsAsync(fixture.Id, new()
        { SnapshotId = fixture.Snapshot.Name, Profile = fixture.Documents.Profile, Query = JsonSerializer.SerializeToElement(new { }), PageSize = 1 }, default);
        Assert.IsNotNull(result.NextCursor);
        var second = fixture.Snapshot with { Uid = Guid.NewGuid(), Metadata = new() { Name = "snapshot-b" } };
        fixture.Store.Add(second);
        fixture.Store.Add(fixture.Observed with { ActiveSnapshotName = second.Name, ActiveSnapshotUid = second.Uid });
        var continued = await fixture.Access.QueryDocumentsAsync(fixture.Id, new() { Cursor = result.NextCursor }, default);
        Assert.AreEqual(fixture.Snapshot.Name, continued.SnapshotId);
        Assert.HasCount(2, fixture.Documents.Inspections);
        Assert.HasCount(2, fixture.Documents.Queries);
        Assert.IsTrue(fixture.Documents.Inspections.All(reference => reference.Publication.Id == "publication-a"));
        Assert.IsTrue(fixture.Documents.Queries.All(target => target.Publication == "publication-a"
            && !target.Policy.Put && !target.Policy.Delete && !target.Policy.Publish));
        Assert.AreEqual(0, fixture.Publications.Reads.Count);
        Assert.AreEqual(0, fixture.Resolver.Calls);
    }

    private sealed class Fixture
    {
        public const string Text = "Nantes records";
        public CurrentRequestContext Context { get; } = new();
        public RequestContext Caller { get; } = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        public MemoryStore Store { get; }
        public Authorization Authorization { get; } = new();
        public Publications Publications { get; } = new();
        public Resolver Resolver { get; } = new();
        public TimeProvider Clock { get; } = TimeProvider.System;
        public KnowledgeSnapshotService Snapshots { get; }
        public KnowledgeAccessService Access { get; }
        public KnowledgeSourceManagementService Sources { get; }
        public KnowledgeSourceResource Source { get; }
        public Documents Documents { get; } = new();
        public KnowledgeSnapshotResource Snapshot { get; }
        public KnowledgeSnapshotObservedResource Observed { get; }
        public KnowledgeSourceId Id { get; }
        private readonly string owner;

        public Fixture(string owner = "workspace", int artifacts = 1, bool structured = false)
        {
            this.owner = owner;
            Store = new(Context);
            var execution = ResourceScopeRef.Workspace(Caller.WorkspaceId);
            var ownership = owner switch { "instance" => ResourceScopeRef.Instance, "tenant" => ResourceScopeRef.Tenant(Caller.TenantId), _ => execution };
            var source = new KnowledgeSourceResource
            {
                ApiVersion = ResourceApiVersions.CoreV1,
                Kind = KnowledgeResourceKinds.KnowledgeSource,
                Metadata = new() { Name = "knowledge" },
                Uid = Guid.NewGuid(),
                Generation = 1,
                ScopeRef = ownership,
                Definition = new() { DisplayName = "Knowledge" }
            };
            Id = new(source.Name, ScopeRef: ownership);
            Source = source;
            var target = new StorageContentRequest(new(new(ownership, ResourceNamespace.Default, "Storage", "output"),
                Guid.NewGuid(), 1, "binding"), new(ownership, source.Address), new());
            var publication = new StorageDocumentPublicationReference(new(target.Storage, target.Consumer, Guid.NewGuid(), new(), "publication-a"),
                new("publication-a", "digest", artifacts, "schema"), Documents.Profile);
            Snapshot = new()
            {
                ApiVersion = ResourceApiVersions.CoreV1,
                Kind = KnowledgeResourceKinds.KnowledgeSnapshot,
                Metadata = new() { Name = "snapshot-a" },
                Uid = Guid.NewGuid(),
                ScopeRef = execution,
                KnowledgeSourceUid = source.Uid,
                KnowledgeSourceScopeRef = ownership,
                KnowledgeSourceName = source.Name,
                KnowledgeSourceNamespace = source.Namespace,
                KnowledgeSourceGeneration = source.Generation,
                RequestHash = "request",
                PublishedAt = Clock.GetUtcNow(),
                PublishedBy = Caller.PrincipalId,
                ProjectionId = "projection",
                ProjectionUid = Guid.NewGuid(),
                ProjectionFlowRunId = "run",
                ProjectionFlow = new("projection", ResourceNamespace.Default, "1", false, null, null),
                Artifacts = Enumerable.Range(0, artifacts).Select(index => new KnowledgeSnapshotArtifact
                {
                    ArtifactId = "document-" + index,
                    ProducerFlowRunId = "producer",
                    ProducerFlowStepId = "step",
                    StorageFlowRunId = "storage",
                    MediaType = "text/plain",
                    Length = Encoding.UTF8.GetByteCount(Text),
                    Sha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Text))),
                    Content = structured || index % 2 == 0 ? new(target, "document-" + index, "exact-version", Encoding.UTF8.GetByteCount(Text),
                        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Text))))
                    { Document = structured ? new(publication, "document-" + index, "revision") : null } : null
                }).ToArray()
            };
            Observed = new()
            {
                ApiVersion = ResourceApiVersions.CoreV1,
                Kind = KnowledgeResourceKinds.KnowledgeSnapshotObservedState,
                Metadata = new() { Name = "snapshot-state-" + source.Uid.ToString("N") },
                ScopeRef = execution,
                KnowledgeSourceUid = source.Uid,
                ActiveSnapshotName = Snapshot.Name,
                ActiveSnapshotUid = Snapshot.Uid
            };
            Store.Add(source); Store.Add(Snapshot); Store.Add(Observed);
            var sources = new KnowledgeSourceManagementService(Store, null!, null!, null!, Context, new Audit(), Clock);
            Sources = sources;
            Snapshots = new(Store, sources, Context, Context, Authorization, Resolver, new Audit(), Clock, Publications);
            var projections = new KnowledgeProjectionService(Store, null!, Context, Context, Authorization, sources,
                null!, null!, null!, null!, Resolver, new Audit(), Clock, null!);
            var reader = new KnowledgeSnapshotContentReader(Publications, null!, Store, Context, Context, Authorization);
            Access = new(sources, Snapshots, projections, Store, Context, Context, Authorization, reader, new Protector(), Clock, documents: Documents);
        }

        public IDisposable Push() => owner switch
        {
            "instance" => Context.PushInstance(Caller),
            "tenant" => Context.PushTenantAdministration(Caller),
            _ => Context.Push(Caller)
        };
    }

    private sealed class MemoryStore(CurrentRequestContext context) : IResourceStore
    {
        private readonly Dictionary<ScopedResourceAddress, Resource> values = [];
        public int Writes { get; private set; }
        public void Add(Resource value) => values[new(value.ScopeRef!.Value, value.Namespace, value.Kind, value.Name)] = value;
        public Task<StoredResource<T>?> GetExactAsync<T>(ScopedResourceAddress address, CancellationToken ct) where T : Resource
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(values.TryGetValue(address, out var value) ? Wrap((T)value) : null);
        }
        public Task<IReadOnlyList<StoredResource<T>>> ListExactAsync<T>(ResourceScopeRef scope, string kind, int skip, int take, CancellationToken ct) where T : Resource
        {
            ct.ThrowIfCancellationRequested();
            IReadOnlyList<StoredResource<T>> result = values.Values.Where(value => value.ScopeRef == scope && value.Kind == kind)
                .Cast<T>().Skip(skip).Take(take).Select(Wrap).ToArray();
            return Task.FromResult(result);
        }
        public Task<StoredResource<T>> PutExactAsync<T>(ResourceScopeRef scope, T resource, string? ifMatch, bool ifNoneMatch, CancellationToken ct) where T : Resource
        {
            ct.ThrowIfCancellationRequested(); Writes++;
            resource = (T)(resource with { ScopeRef = scope, Uid = resource.Uid == Guid.Empty ? Guid.NewGuid() : resource.Uid });
            Add(resource);
            return Task.FromResult(Wrap(resource));
        }
        public Task<StoredResource<T>?> GetAsync<T>(ResourceKey key, CancellationToken ct) where T : Resource =>
            GetExactAsync<T>(new(ResourceScopeRef.Workspace(context.Current.WorkspaceId), key.Namespace, key.Kind, key.Name), ct);
        public Task<IReadOnlyList<StoredResource<T>>> ListAsync<T>(string kind, int skip, int take, CancellationToken ct) where T : Resource =>
            ListExactAsync<T>(ResourceScopeRef.Workspace(context.Current.WorkspaceId), kind, skip, take, ct);
        public Task InitializeAsync(CancellationToken ct) => Task.CompletedTask;
        public Task<StoredResource<T>> PutAsync<T>(T resource, string? ifMatch, bool ifNoneMatch, CancellationToken ct) where T : Resource => throw new NotSupportedException();
        public Task<StoredResource<T>> CreateImmutableAsync<T>(T resource, CancellationToken ct) where T : Resource => throw new NotSupportedException();
        public Task DeleteAsync(ResourceKey key, string? ifMatch, CancellationToken ct) => throw new NotSupportedException();
        private static StoredResource<T> Wrap<T>(T value) where T : Resource => new(value, "etag", DateTimeOffset.UnixEpoch);
    }

    private sealed class Authorization : IAuthorizationService
    {
        public HashSet<string> Denied { get; } = [];
        public Task<IReadOnlySet<string>> GetPermissionsAsync(RequestContext context, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> HasPermissionAsync(RequestContext context, string permission, CancellationToken ct) => Task.FromResult(!Denied.Contains(permission));
        public Task EnsurePermissionAsync(RequestContext context, string permission, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); if (Denied.Contains(permission)) throw new AuthorizationDeniedException(permission); return Task.CompletedTask; }
    }
    private sealed class Resolver : IKnowledgeSnapshotArtifactResolver
    {
        public int Calls { get; private set; }
        public Task<IReadOnlyList<KnowledgeDurableArtifactEvidence>> ResolveAsync(Guid workspaceId, IReadOnlyList<string> artifactIds, CancellationToken cancellationToken)
        { Calls++; throw new InvalidOperationException("Metadata consultation must not resolve physical Artifact availability."); }
    }
    private sealed class Publications : IStoragePublicationGateway
    {
        public List<string> Reads { get; } = [];
        public Dictionary<string, byte[]> Content { get; } = [];
        public Task<ReadOnlyMemory<byte>> ReadAsync(StoragePublicationReference reference, long offset, int length, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); Reads.Add(reference.Key);
            Assert.AreEqual("exact-version", reference.Version);
            if (!Content.TryGetValue(reference.Key, out var bytes)) throw new StorageException("storage_publication_unavailable", "Unavailable.");
            return Task.FromResult<ReadOnlyMemory<byte>>(bytes.AsMemory((int)offset, Math.Min(length, bytes.Length - (int)offset)));
        }
        public Task ValidateAsync(StorageContentRequest target, CancellationToken ct) => throw new InvalidOperationException("Unexpected validation.");
        public Task<StoragePublicationReference> PrepareAsync(StorageContentRequest target, string id, ReadOnlyMemory<byte> content, CancellationToken ct) => throw new InvalidOperationException("Unexpected preparation.");
        public Task SealAsync(StoragePublicationReference reference, CancellationToken ct) => throw new InvalidOperationException("Unexpected seal.");
    }
    private sealed class Documents : IStorageDocumentGateway
    {
        public StorageQueryProfilePin Profile { get; } = new("query", "1", "json", "schema");
        public List<StorageDocumentPublicationReference> Inspections { get; } = [];
        public List<StorageDocumentTarget> Queries { get; } = [];
        public Task<StorageDocumentDescription> DescribeAsync(StorageDocumentTarget target, CancellationToken ct) =>
            Task.FromResult(new StorageDocumentDescription(new(true, false, false, true, true, [new("query", "1", "json")]),
                target.Policy, [Profile], JsonSerializer.SerializeToElement(new { type = "object" }), "schema"));
        public Task ValidatePublicationAsync(StorageDocumentPublicationReference reference, CancellationToken ct)
        { Inspections.Add(reference); return Task.CompletedTask; }
        public Task<StorageDocumentPage> QueryAsync(StorageDocumentTarget target, StorageDocumentQuery request, CancellationToken ct)
        { Queries.Add(target); return Task.FromResult(new StorageDocumentPage([], [], request.Cursor is null ? "provider-cursor" : null)); }
        public Task<StorageDocumentValue> GetAsync(StorageDocumentTarget target, string id, string? revision, CancellationToken ct) => throw new InvalidOperationException("Query must not read individual documents.");
        public Task<StorageDocumentValue> PutAsync(StorageDocumentTarget target, StorageDocumentWrite request, CancellationToken ct) => throw new NotSupportedException();
        public Task<StorageDocumentValue> DeleteAsync(StorageDocumentTarget target, StorageDocumentDelete request, CancellationToken ct) => throw new NotSupportedException();
        public Task<StorageDocumentPublicationReference> PrepareAsync(StorageDocumentTarget target, string publicationId, IReadOnlyList<StorageDocumentValue> documents, string requestId, DateTimeOffset notAfter, CancellationToken ct) => throw new NotSupportedException();
        public Task SealAsync(StorageDocumentPublicationReference reference, CancellationToken ct) => throw new NotSupportedException();
        public Task RetireAsync(StorageDocumentPublicationReference reference, CancellationToken ct) => throw new NotSupportedException();
    }
    private sealed class Audit : ISecurityAuditWriter
    { public Task WriteAsync(SecurityAuditWrite entry, CancellationToken cancellationToken) => Task.CompletedTask; }
    private sealed class Protector : IKnowledgeAccessTokenProtector
    {
        public string Protect(string value) => value;
        public string Unprotect(string value) => value;
    }
}
