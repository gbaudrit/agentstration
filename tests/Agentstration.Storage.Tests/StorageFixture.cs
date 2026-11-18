using System.Text.Json;
using Agentstration.ConnectedServices.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;
using Agentstration.Storage.Contracts;

namespace Agentstration.Storage.Tests;

internal sealed class StorageFixture
{
    public static readonly ResourceScopeRef Tenant = ResourceScopeRef.Tenant(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    public static readonly ResourceScopeRef Workspace = ResourceScopeRef.Workspace(Guid.Parse("22222222-2222-2222-2222-222222222222"));
    public static readonly ResourceScopeRef Foreign = ResourceScopeRef.Tenant(Guid.Parse("33333333-3333-3333-3333-333333333333"));
    public static readonly JsonElement Empty = JsonSerializer.SerializeToElement(new { });
    public MemoryStore Store { get; } = new();
    public MetadataInspector Inspector { get; } = new();
    public TypeAvailability Availability { get; } = new();
    public ResourceGovernanceContext Governance { get; } = new();
    public AccessPolicies Access { get; } = new();
    public Usages Dependencies { get; } = new();
    public TestRequestContext Context { get; } = new();
    public StorageTypeManagementService Types { get; }
    public StorageManagementService Storages { get; }

    public StorageFixture()
    {
        var scopes = new Scopes();
        var use = new DescendantResourceUseAuthorizer(scopes);
        Types = new(Store, scopes, use, Governance, Availability, Context, Access, Context);
        Storages = new(Store, scopes, use, Access, Inspector, Availability, [Dependencies], Context, new Authorization(), new Authorization(), new FixedTime());
    }

    public static StorageTypeVersionDescriptor Version(bool remote = false) => new()
    {
        Version = "1.0.0",
        ConfigurationSchema = Schema,
        SchemaDigest = StorageDigests.Schema(Schema),
        EnvelopeSchema = Schema,
        EnvelopeSchemaDigest = StorageDigests.Schema(Schema),
        RequiresConnectedService = remote,
        Requirements = remote ? [new("connection", true, ConnectedServiceValueType.Text, ConnectedServiceValueProtection.Secured)] : [],
        Capabilities = new() { List = true, Read = true, Stat = true }
    };

    public static JsonElement Schema => JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new { prefix = new { type = "string", maxLength = 20 } },
        additionalProperties = false
    });

    public async Task<StorageTypeResource> TypeAsync(bool remote = false, ResourceScopeRef? scope = null)
    {
        var type = await Types.CreateAsync(new()
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = StorageResourceKinds.StorageType,
            Metadata = new() { Name = remote ? "remote" : "local" },
            ScopeRef = scope ?? Tenant,
            Definition = new() { DisplayName = "Type", PreferredVersion = "1.0.0", Versions = [Version(remote)] }
        }, default);
        return type.Value;
    }

    public static StorageTypeReference Reference(StorageTypeResource type, string? version = null)
    {
        var v = type.Definition.Versions.Single(v => v.Version == (version ?? type.Definition.PreferredVersion));
        return new()
        {
            Type = new(type.Name, type.ScopeRef, type.Namespace),
            TypeUid = type.Uid,
            Version = v.Version,
            SchemaDigest = v.SchemaDigest,
            ContractDigest = StorageDigests.Version(v)
        };
    }

    public static StorageResource Storage(StorageTypeResource type, string name = "storage", ResourceScopeRef? scope = null) => new()
    {
        ApiVersion = ResourceApiVersions.CoreV1,
        Kind = StorageResourceKinds.Storage,
        Metadata = new() { Name = name },
        ScopeRef = scope ?? Tenant,
        Definition = new() { DisplayName = "Storage", Type = Reference(type), Configuration = Empty, Envelope = Empty }
    };

    public async Task<StorageResource> RemoteAsync()
    {
        var type = await TypeAsync(remote: true);
        var service = new ConnectedServiceResource
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = ConnectedServiceResourceKinds.ConnectedService,
            Metadata = new() { Name = "account" },
            ScopeRef = Tenant,
            Generation = 1,
            Definition = new()
            {
                DisplayName = "Account",
                Type = new()
                {
                    Type = new("account-type", Tenant),
                    TypeUid = Guid.NewGuid(),
                    Version = "1.0.0",
                    SchemaDigest = "sha256:account"
                },
                Options = Empty
            }
        };
        var stored = await Store.PutExactAsync(Tenant, service, null, true, default);
        Inspector.Metadata = new(service.Definition.Type,
            [new("credential", ConnectedServiceValueType.Text, ConnectedServiceValueProtection.Secured)]);
        return Storage(type) with
        {
            Definition = Storage(type).Definition with
            {
                ConnectedService = new() { Service = new(stored.Value.Address, Tenant), ServiceUid = stored.Value.Uid, Generation = 1 },
                ConnectedServiceType = service.Definition.Type,
                Bindings = [new("connection", Inspector.Metadata.Exports.Single())]
            }
        };
    }

    public static StorageRevisionReference Revision(StorageResource storage) =>
        new(new(storage.ScopeRef!.Value, storage.Namespace, storage.Kind, storage.Name), storage.Uid, storage.Generation,
            StorageDigests.Binding(storage.Definition));

    public sealed class MetadataInspector : IConnectedServiceExportInspector
    {
        public ConnectedServiceExportMetadata Metadata { get; set; } = null!;
        public string? Error { get; set; }
        public Task<ConnectedServiceExportMetadata> InspectAsync(ConnectedServiceRevisionReference service,
            ConnectedServiceResolutionContext context, CancellationToken cancellationToken) =>
            Error is { } code ? throw new ConnectedServiceExportInspectionException(code) : Task.FromResult(Metadata);
    }
    public sealed class TypeAvailability : IStorageTypeAvailabilityProvider
    {
        public bool Available { get; set; } = true;
        public Task<bool> IsAvailableAsync(StorageTypeResource type, StorageTypeVersionDescriptor version, CancellationToken cancellationToken) => Task.FromResult(Available);
    }
    public sealed class Usages : IStorageUsageProvider
    {
        public bool InUse { get; set; }
        public Task<IReadOnlyList<StorageUsage>> GetUsagesAsync(ScopedResourceAddress storage, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<StorageUsage>>(InUse ? [new("DataSource", "dataset", "Dataset", "/dataset")] : []);
    }
    public sealed class TestRequestContext : ICurrentRequestContext, IRequestContextScopeFactory
    {
        public bool IsInitialized => true;
        public ControlPlaneAccessMode AccessMode { get; set; } = ControlPlaneAccessMode.System;
        public RequestContext Current => new(Guid.Empty, Tenant.TargetId!.Value, Workspace.TargetId!.Value);
        public IDisposable Push(RequestContext context) => new Noop();
        public IDisposable PushInstance(RequestContext operationalContext) => new Noop();
        public IDisposable PushTenantAdministration(RequestContext operationalContext) => new Noop();
        public IDisposable PushTenant(Guid principalId, Guid tenantId, AuthorizationRestriction? restriction = null) => new Noop();
        public IDisposable PushSystem() => new Noop();
        private sealed class Noop : IDisposable { public void Dispose() { } }
    }
    private sealed class Authorization : IAuthorizationService, IPlatformAuthorizationService
    {
        public Task<IReadOnlySet<string>> GetPermissionsAsync(RequestContext context, CancellationToken cancellationToken) => Task.FromResult<IReadOnlySet<string>>(new HashSet<string>());
        public Task<bool> HasPermissionAsync(RequestContext context, string permission, CancellationToken cancellationToken) => Task.FromResult(true);
        public Task EnsurePermissionAsync(RequestContext context, string permission, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<bool> IsPlatformAdministratorAsync(Guid principalId, CancellationToken cancellationToken) => Task.FromResult(true);
    }
    private sealed class FixedTime : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);
    }
    public sealed class AccessPolicies : IResourceAccessPolicyService
    {
        public bool Allowed { get; set; } = true;
        public Task<ResourceAccessDecision> EvaluateAsync(ResourceAccessIdentity resource, ResourceScopeRef consumerScopeRef,
            ResourceAccessAction action, CancellationToken cancellationToken) => Task.FromResult(new ResourceAccessDecision(Allowed, "test"));
        public Task<IReadOnlyList<ResourceAccessPolicyResponse>> ListAsync(ResourceScopeRef ownerScopeRef, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceAccessPolicyResponse?> GetAsync(Guid resourceUid, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceAccessPolicyResponse> PutAsync(ResourceAccessPolicy policy, string? ifMatch, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeleteAsync(Guid resourceUid, string? ifMatch, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
    private sealed class Scopes : IResourceScopeResolver, IResourceScopeOperations
    {
        public ResourceScopeRef DefaultScopeRef(string kind) => Tenant;
        public ResourceScopeRef TargetScopeRef(ResourceScopeKind kind) => Tenant;
        public Task<IReadOnlyList<ResourceScopeTarget>> ListTargetsAsync(string kind, string permission, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<T> WriteAsync<T>(Resource resource, ResourceScopeRef scopeRef, string permission, Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken) => operation(cancellationToken);
        public Task<T> WriteAsync<T>(string kind, ResourceScopeRef scopeRef, string permission, Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken) => operation(cancellationToken);
        public Task<ResolvedResourceScope?> ResolveAsync(ResourceScopeRef scopeRef, CancellationToken cancellationToken)
        {
            var instance = new ResourceScope(1, ResourceScopeRef.Instance, ResourceScopeKind.Instance, "instance", null);
            var tenant = new ResourceScope(2, Tenant, ResourceScopeKind.Tenant, Tenant.TargetKey, 1);
            var scope = scopeRef == Workspace ? new ResourceScope(3, Workspace, ResourceScopeKind.Workspace, Workspace.TargetKey, 2)
                : scopeRef == Tenant ? tenant : scopeRef == Foreign ? new(4, Foreign, ResourceScopeKind.Tenant, Foreign.TargetKey, 1) : instance;
            return Task.FromResult<ResolvedResourceScope?>(new(scope, scopeRef == Workspace ? [tenant, instance]
                : scopeRef == ResourceScopeRef.Instance ? [] : [instance]));
        }
    }
    public sealed class MemoryStore : IResourceStore
    {
        public HashSet<ResourceScopeRef> ReadDeniedScopes { get; } = [];
        private readonly Dictionary<ScopedResourceAddress, (Resource Value, string ETag)> resources = [];
        private int revision;
        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<StoredResource<T>?> GetExactAsync<T>(ScopedResourceAddress address, CancellationToken cancellationToken) where T : Resource =>
            ReadDeniedScopes.Contains(address.ScopeRef) ? throw new ResourceScopeAccessDeniedException(address.ScopeRef)
                : Task.FromResult(resources.TryGetValue(address, out var stored) ? new StoredResource<T>((T)stored.Value, stored.ETag, default) : null);
        public Task<StoredResource<T>> PutExactAsync<T>(ResourceScopeRef scopeRef, T resource, string? ifMatch, bool ifNoneMatch, CancellationToken cancellationToken) where T : Resource
        {
            var key = new ScopedResourceAddress(scopeRef, resource.Namespace, resource.Kind, resource.Name);
            resources.TryGetValue(key, out var previous);
            if ((ifNoneMatch && previous.Value is not null) || (ifMatch is not null && ifMatch != previous.ETag))
                throw new ResourceConcurrencyException("Concurrent update.");
            var etag = $"\"{++revision}\"";
            var updated = (T)resource.WithSystemState(previous.Value?.Uid ?? Guid.NewGuid(), scopeRef, etag);
            resources[key] = (updated, etag);
            return Task.FromResult(new StoredResource<T>(updated, etag, default));
        }
        public Task<IReadOnlyList<StoredResource<T>>> ListAsync<T>(string kind, int skip, int take, CancellationToken cancellationToken) where T : Resource =>
            Task.FromResult<IReadOnlyList<StoredResource<T>>>(resources.Values.Where(v => v.Value.Kind == kind).Skip(skip).Take(take)
                .Select(v => new StoredResource<T>((T)v.Value, v.ETag, default)).ToArray());
        public Task<IReadOnlyList<StoredResource<T>>> ListVisibleAsync<T>(ResourceScopeRef targetScopeRef, string kind, int skip, int take, CancellationToken cancellationToken) where T : Resource => ListAsync<T>(kind, skip, take, cancellationToken);
        public Task<StoredResource<T>?> GetAsync<T>(ResourceKey key, CancellationToken cancellationToken) where T : Resource => GetExactAsync<T>(key.AtScope(Tenant), cancellationToken);
        public Task<StoredResource<T>> PutAsync<T>(T resource, string? ifMatch, bool ifNoneMatch, CancellationToken cancellationToken) where T : Resource => PutExactAsync(resource.ScopeRef ?? Tenant, resource, ifMatch, ifNoneMatch, cancellationToken);
        public Task<StoredResource<T>> CreateImmutableAsync<T>(T resource, CancellationToken cancellationToken) where T : Resource => PutAsync(resource, null, true, cancellationToken);
        public Task DeleteAsync(ResourceKey key, string? ifMatch, CancellationToken cancellationToken) => DeleteExactAsync(key.AtScope(Tenant), ifMatch, cancellationToken);
        public Task DeleteExactAsync(ScopedResourceAddress address, string? ifMatch, CancellationToken cancellationToken)
        {
            if (ifMatch is not null && resources[address].ETag != ifMatch) throw new ResourceConcurrencyException("Concurrent delete.");
            resources.Remove(address);
            return Task.CompletedTask;
        }
    }
}
