using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Agentstration.Identity.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Work;
using Agentstration.Work.Storage.Abstractions;
using Microsoft.AspNetCore.DataProtection;

namespace Agentstration.Web.Hosting;

public sealed class BootstrapVersionHistory(IResourceStore store, IRequestContextScopeFactory scopes,
    BootstrapProfileCatalog catalog, IDataProtectionProvider protection, TimeProvider time, BootstrapApplicationLock execution,
    IResourceScopeResolver scopeResolver, IResourceIdentityCatalog identities, IWorkplaceRepository workplace) : IBootstrapVersionHistory
{
    private const string ProfileKind = "BootstrapProfile";
    private const string VersionKind = "BootstrapProfileVersion";
    private const string ApplicationKind = "ScopedBootstrapApplication";
    private readonly IDataProtector snapshotProtector = protection.CreateProtector("Agentstration.Bootstrap.ProfileArchive.v1");

    internal async Task ValidateAsync(IReadOnlyList<LoadedBootstrapProfile> profiles, BootstrapSourceProvenance? source, CancellationToken token)
    {
        using var system = scopes.PushSystem();
        foreach (var profile in profiles)
        {
            var existing = await store.GetExactAsync<VersionMetadata>(
                Address(ResourceScopeRef.Instance, VersionKind, Id(ProfileId(profile.Summary, source), profile.Summary.Version)), token);
            if (existing is not null)
                RequireSameContent(existing.Value.Definition, profile.Summary);
        }
    }

    internal async Task<IReadOnlyList<AppliedBootstrapVersion>> RegisterAsync(IReadOnlyList<LoadedBootstrapProfile> profiles,
        BootstrapSourceProvenance? source, CancellationToken cancellationToken)
    {
        using var system = scopes.PushSystem();
        var versions = new List<AppliedBootstrapVersion>();
        foreach (var profile in profiles)
        {
            var summary = profile.Summary;
            var profileId = ProfileId(summary, source);
            var versionId = Id(profileId, summary.Version);
            var address = Address(ResourceScopeRef.Instance, VersionKind, versionId);
            var existing = await store.GetExactAsync<VersionRecord>(address, cancellationToken);
            if (existing is null)
            {
                var archive = source is null ? snapshotProtector.Protect(await catalog.ArchiveAsync(profile, cancellationToken)) : null;
                var value = new VersionRecord
                {
                    ApiVersion = ResourceApiVersions.CoreV1,
                    Kind = VersionKind,
                    Metadata = new() { Name = versionId },
                    ScopeRef = ResourceScopeRef.Instance,
                    Definition = new(profileId, summary.Version, summary.Scope, summary.Digest, time.GetUtcNow(), source),
                    ProtectedArchive = archive
                };
                try { existing = await store.CreateImmutableAsync(value, cancellationToken); }
                catch (ResourceConcurrencyException)
                {
                    existing = await store.GetExactAsync<VersionRecord>(address, cancellationToken);
                    if (existing is null) throw;
                }
            }
            RequireSameContent(existing.Value.Definition, summary);

            var profileAddress = Address(ResourceScopeRef.Instance, ProfileKind, profileId);
            if (await store.GetExactAsync<ProfileRecord>(profileAddress, cancellationToken) is null)
            {
                try
                {
                    await store.CreateImmutableAsync(new ProfileRecord
                    {
                        ApiVersion = ResourceApiVersions.CoreV1,
                        Kind = ProfileKind,
                        Metadata = new() { Name = profileId },
                        ScopeRef = ResourceScopeRef.Instance,
                        Definition = new(profileId, summary.Name, summary.DisplayName, summary.Scope)
                    }, cancellationToken);
                }
                catch (ResourceConcurrencyException)
                {
                    if (await store.GetExactAsync<ProfileRecord>(profileAddress, cancellationToken) is null) throw;
                }
            }
            versions.Add(existing.Value.Definition with { Source = source });
        }
        return versions;
    }

    internal async Task<LoadedBootstrapSelection?> StageLocalPacksAsync(IReadOnlyList<LoadedBootstrapProfile> profiles,
        IReadOnlyList<AppliedBootstrapVersion> versions, CancellationToken cancellationToken)
    {
        if (!profiles.Any(profile => profile.Resources.Any(resource => resource.Resource.Kind == PackBootstrapKinds.PackInstallation)))
            return null;
        using var system = scopes.PushSystem();
        var root = Directory.CreateTempSubdirectory("agentstration-bootstrap-version-");
        try
        {
            var staged = profiles.ToArray();
            for (var index = 0; index < staged.Length; index++)
            {
                var profile = staged[index];
                if (!profile.Resources.Any(resource => resource.Resource.Kind == PackBootstrapKinds.PackInstallation)) continue;
                var version = versions[index];
                var record = await store.GetExactAsync<VersionRecord>(
                    Address(ResourceScopeRef.Instance, VersionKind, Id(version.ProfileId, version.Version)), cancellationToken);
                var bytes = record?.Value.ProtectedArchive
                    ?? throw new DeclarativeBootstrapException("The registered local Bootstrap archive is missing.");
                using var stream = new MemoryStream(snapshotProtector.Unprotect(bytes), writable: false);
                using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
                var directory = Path.Combine(root.FullName, profile.Summary.Name);
                cancellationToken.ThrowIfCancellationRequested();
                archive.ExtractToDirectory(directory);
                staged[index] = profile with { DirectoryPath = directory };
            }
            return new(staged, temporaryRoot: root.FullName);
        }
        catch
        {
            await new LoadedBootstrapSelection([], temporaryRoot: root.FullName).DisposeAsync();
            throw;
        }
    }

    internal async Task BeginAsync(Guid id, Guid correlation, ResourceScopeRef scope, BootstrapApplicationSource source,
        IReadOnlyList<AppliedBootstrapVersion> versions, IReadOnlyList<BootstrapBindingSelection> bindings, CancellationToken cancellationToken)
    {
        using var system = scopes.PushSystem();
        var existing = await ReadAsync(scope, id, cancellationToken);
        if (existing is not null && existing.Value.ExecutionOwner != execution.Owner)
            throw new DeclarativeBootstrapException("The Bootstrap application belongs to another execution.");
        var record = existing?.Value ?? new ApplicationRecord
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = ApplicationKind,
            Metadata = new() { Name = id.ToString("N") },
            ScopeRef = scope,
            ExecutionOwner = execution.Owner,
            Definition = new(id, correlation, scope, source, time.GetUtcNow(), null, BootstrapApplicationStatus.Running, [], [], [])
        };
        await store.PutExactAsync(scope, record with
        {
            Definition = record.Definition with
            {
                Versions = record.Definition.Versions.Concat(versions).DistinctBy(value => (value.ProfileId, value.Version)).ToArray(),
                Bindings = record.Definition.Bindings.Concat(bindings.Select(binding => binding with { TargetValue = null })).Distinct().ToArray()
            }
        }, existing?.ETag, existing is null, cancellationToken);
    }

    internal async Task RecordAsync(ResourceScopeRef scope, Guid id, IReadOnlyList<BootstrapAppliedResource> results,
        BootstrapApplicationStatus? status, CancellationToken cancellationToken)
    {
        using var system = scopes.PushSystem();
        var existing = await ReadAsync(scope, id, cancellationToken);
        if (existing is null || existing.Value.Definition.Status != BootstrapApplicationStatus.Running) return;
        var current = existing.Value;
        var resources = current.Definition.Resources.Concat(results).ToArray();
        var final = status == BootstrapApplicationStatus.Failed && resources.Any(value => value.Disposition == BootstrapResourceDisposition.Create)
            ? BootstrapApplicationStatus.PartiallyApplied : status;
        await store.PutExactAsync(scope, current with
        {
            Definition = current.Definition with
            {
                Resources = resources,
                Status = final ?? current.Definition.Status,
                CompletedAt = final is null ? null : time.GetUtcNow()
            }
        }, existing.ETag, false, cancellationToken);
    }

    public async Task<IReadOnlyList<AppliedBootstrapProfile>> ListProfilesAsync(CancellationToken cancellationToken)
    {
        using var system = scopes.PushSystem();
        return (await store.ListAllAsync<ProfileRecord>(ProfileKind, cancellationToken)).Select(value => value.Value.Definition)
            .OrderBy(value => value.DisplayName, StringComparer.Ordinal).ToArray();
    }

    public async Task<IReadOnlyList<AppliedBootstrapVersion>> ListVersionsAsync(string profileId, CancellationToken cancellationToken)
    {
        using var system = scopes.PushSystem();
        return (await store.ListAllAsync<VersionMetadata>(VersionKind, cancellationToken)).Select(value => value.Value.Definition)
            .Where(value => value.ProfileId == profileId).OrderByDescending(value => value.RegisteredAt).ToArray();
    }

    public async Task<IReadOnlyList<ScopedBootstrapApplication>> ListHistoryAsync(ResourceScopeRef scope, string profileId,
        string version, CancellationToken cancellationToken)
    {
        using var system = scopes.PushSystem();
        if (await scopeResolver.ResolveAsync(scope, cancellationToken) is null) return [];
        var history = new List<ScopedBootstrapApplication>();
        const int pageSize = 100;
        for (var offset = 0; ; offset += pageSize)
        {
            var page = await store.ListExactAsync<ApplicationRecord>(scope, ApplicationKind, offset, pageSize, cancellationToken);
            history.AddRange(page.Select(value => Observe(value.Value))
                .Where(value => value.Versions.Any(candidate => candidate.ProfileId == profileId && candidate.Version == version))
                .Select(value => value with { Resources = value.Resources.Where(row => row.ProfileId == profileId && row.Version == version).ToArray() }));
            if (page.Count < pageSize) break;
        }
        var ordered = new List<ScopedBootstrapApplication>();
        var inventory = new Dictionary<ResourceScopeRef, IReadOnlyDictionary<Guid, ResourceInventoryEntry>>();
        foreach (var application in history.OrderByDescending(value => value.StartedAt))
            ordered.Add(await WithAvailableResourcesAsync(application, inventory, cancellationToken));
        return ordered;
    }

    public async Task<ScopedBootstrapApplication?> GetAsync(ResourceScopeRef scope, Guid id, CancellationToken cancellationToken)
    {
        using var system = scopes.PushSystem();
        if (await scopeResolver.ResolveAsync(scope, cancellationToken) is null) return null;
        return (await ReadAsync(scope, id, cancellationToken)) is { } saved
            ? await WithAvailableResourcesAsync(Observe(saved.Value), [], cancellationToken) : null;
    }

    private async Task<ScopedBootstrapApplication> WithAvailableResourcesAsync(ScopedBootstrapApplication application,
        Dictionary<ResourceScopeRef, IReadOnlyDictionary<Guid, ResourceInventoryEntry>> inventory, CancellationToken token)
    {
        var rows = new List<BootstrapAppliedResource>();
        foreach (var row in application.Resources)
        {
            var available = false;
            if (row.ResourceUid is Guid uid && row.OwnerScope is { } owner)
            {
                if (!inventory.TryGetValue(owner, out var entries))
                {
                    entries = await scopeResolver.ResolveAsync(owner, token) is null
                        ? new Dictionary<Guid, ResourceInventoryEntry>()
                        : (await identities.ListExactInventoryAsync(owner, token)).ToDictionary(value => value.Uid);
                    inventory.Add(owner, entries);
                }
                available = entries.TryGetValue(uid, out var identity)
                    && identity.Namespace == row.Namespace && identity.Kind == row.Kind && identity.Name == row.Name;
            }
            if (row.Disposition == BootstrapResourceDisposition.Create && row.Kind == EntryResourceKinds.Entry
                && row.OwnerScope is { Kind: ResourceScopeKind.Workspace, TargetId: Guid workspace } && row.Namespace is { } ns)
            {
                var entry = await workplace.GetEntryDraftAsync(new WorkspaceId(workspace), new(row.Name, ns), token);
                available = entry?.AppliedBy?.ApplicationId == application.Id;
            }
            rows.Add(row with { ResourceAvailable = available });
        }
        return application with { Resources = rows };
    }

    private ScopedBootstrapApplication Observe(ApplicationRecord value) =>
        value.Definition.Status == BootstrapApplicationStatus.Running && value.ExecutionOwner != execution.Owner
            ? value.Definition with { Status = BootstrapApplicationStatus.Interrupted }
            : value.Definition;

    private Task<StoredResource<ApplicationRecord>?> ReadAsync(ResourceScopeRef scope, Guid id, CancellationToken cancellationToken) =>
        store.GetExactAsync<ApplicationRecord>(Address(scope, ApplicationKind, id.ToString("N")), cancellationToken);

    private static ScopedResourceAddress Address(ResourceScopeRef scope, string kind, string name) =>
        ScopedResourceAddress.Create(scope, ResourceNamespace.Default, kind, name);

    private static void RequireSameContent(AppliedBootstrapVersion version, BootstrapProfileSummary profile)
    {
        if (!string.Equals(version.Digest, profile.Digest, StringComparison.Ordinal))
            throw new DeclarativeBootstrapException($"Bootstrap profile '{profile.Name}' version '{profile.Version}' already has different content.");
    }

    private static string ProfileId(BootstrapProfileSummary profile, BootstrapSourceProvenance? source) => source is null
        ? Id("local", profile.Name)
        : Id("source", source.ScopeRef.Value, source.SourceUid.ToString("N"), source.CatalogName, source.EntryName, source.Locale);

    private static string Id(params string[] parts) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(parts))).ToLowerInvariant();

    private sealed record ProfileRecord : Resource, IImmutableResource
    {
        public required AppliedBootstrapProfile Definition { get; init; }
    }

    private record VersionMetadata : Resource, IImmutableResource
    {
        public required AppliedBootstrapVersion Definition { get; init; }
    }

    private sealed record VersionRecord : VersionMetadata
    {
        public byte[]? ProtectedArchive { get; init; }
    }

    private sealed record ApplicationRecord : Resource
    {
        public required string ExecutionOwner { get; init; }
        public required ScopedBootstrapApplication Definition { get; init; }
    }
}
