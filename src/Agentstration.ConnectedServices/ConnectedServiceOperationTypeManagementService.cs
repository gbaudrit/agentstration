using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;
using Agentstration.ConnectedServices.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;

namespace Agentstration.ConnectedServices;

public sealed class ConnectedServiceOperationTypeManagementService(
    IResourceStore store,
    IResourceScopeOperations scopeOperations,
    DescendantResourceUseAuthorizer useAuthorizer,
    IResourceGovernanceContext governance)
{
    public const int MaximumDisplayNameLength = 200;
    public const int MaximumDescriptionLength = 2_000;
    public const int MaximumSchemaBytes = 256 * 1024;
    public const int MaximumVersions = 32;
    private static readonly JsonSerializerOptions DigestOptions = new(JsonSerializerDefaults.Web);
    private static readonly SearchValues<char> NameCharacters =
        SearchValues.Create("abcdefghijklmnopqrstuvwxyz0123456789-.");

    public async Task<IReadOnlyList<StoredResource<ConnectedServiceOperationTypeResource>>> ListAsync(
        CancellationToken cancellationToken) =>
        (await store.ListAllAsync<ConnectedServiceOperationTypeResource>(
            ConnectedServiceResourceKinds.ConnectedServiceOperationType, cancellationToken))
        .OrderBy(value => value.Value.Definition.DisplayName, StringComparer.OrdinalIgnoreCase)
        .ThenBy(value => value.Value.Namespace.Value, StringComparer.Ordinal)
        .ThenBy(value => value.Value.Name, StringComparer.Ordinal)
        .ToArray();

    public Task<StoredResource<ConnectedServiceOperationTypeResource>?> GetExactAsync(
        ResourceScopeRef scopeRef,
        ResourceNamespace @namespace,
        string name,
        CancellationToken cancellationToken) =>
        store.GetExactAsync<ConnectedServiceOperationTypeResource>(Scoped(scopeRef, @namespace, name), cancellationToken);

    public async Task ValidateForCreateAsync(
        ConnectedServiceOperationTypeResource resource,
        CancellationToken cancellationToken)
    {
        var scopeRef = resource.ScopeRef
            ?? scopeOperations.DefaultScopeRef(ConnectedServiceResourceKinds.ConnectedServiceOperationType);
        await ValidateAsync(resource with { ScopeRef = scopeRef }, null, cancellationToken);
    }

    public async Task<StoredResource<ConnectedServiceOperationTypeResource>> CreateAsync(
        ConnectedServiceOperationTypeResource resource,
        CancellationToken cancellationToken)
    {
        var scopeRef = resource.ScopeRef
            ?? scopeOperations.DefaultScopeRef(ConnectedServiceResourceKinds.ConnectedServiceOperationType);
        var scoped = resource with { ScopeRef = scopeRef };
        return await scopeOperations.WriteAsync(scoped, scopeRef, AuthorizationPermissions.ResourcesWrite, async token =>
        {
            await ValidateAsync(scoped, null, token);
            return await store.PutExactAsync(scopeRef, scoped with
            {
                Generation = 1,
                Status = Succeeded()
            }, null, true, token);
        }, cancellationToken);
    }

    public async Task<StoredResource<ConnectedServiceOperationTypeResource>> PutExactAsync(
        ResourceScopeRef scopeRef,
        ResourceNamespace @namespace,
        string name,
        ConnectedServiceOperationTypeProperties definition,
        string? etag,
        CancellationToken cancellationToken)
    {
        var existing = await GetExactAsync(scopeRef, @namespace, name, cancellationToken)
            ?? throw new ConnectedServiceOperationTypeNotFoundException(@namespace, name);
        var merged = MergeVersions(existing.Value.Definition, definition);
        var changed = existing.Value with { Definition = merged };
        return await scopeOperations.WriteAsync(existing.Value, scopeRef,
            AuthorizationPermissions.ResourcesWrite, async token =>
            {
                await ValidateAsync(changed, existing.Value, token);
                return await store.PutExactAsync(scopeRef, changed with
                {
                    Generation = checked(existing.Value.Generation + 1),
                    Status = Succeeded()
                }, etag, false, token);
            }, cancellationToken);
    }

    public async Task<StoredResource<ConnectedServiceOperationTypeResource>> SynchronizeManagedAsync(
        ConnectedServiceOperationTypeResource desired,
        CancellationToken cancellationToken)
    {
        _ = RequireManagementAuthority();
        var scopeRef = desired.ScopeRef
            ?? throw new ConnectedServiceOperationTypeValidationException("Managed operation types require an exact scope.");
        var existing = await GetExactAsync(scopeRef, desired.Namespace, desired.Name, cancellationToken);
        if (existing is null)
        {
            try { return await CreateAsync(desired, cancellationToken); }
            catch (ResourceConcurrencyException)
            {
                existing = await GetExactAsync(scopeRef, desired.Namespace, desired.Name, cancellationToken);
                if (existing is null) throw;
            }
        }
        EnsureManagedByCurrentAuthority(existing.Value);
        var merged = MergeVersions(existing.Value.Definition, desired.Definition);
        if (DefinitionsEqual(existing.Value.Definition, merged)) return existing;
        return await PutExactAsync(scopeRef, desired.Namespace, desired.Name, merged, existing.ETag, cancellationToken);
    }

    public async Task DeleteExactAsync(
        ResourceScopeRef scopeRef,
        ResourceNamespace @namespace,
        string name,
        string? etag,
        CancellationToken cancellationToken)
    {
        var existing = await GetExactAsync(scopeRef, @namespace, name, cancellationToken)
            ?? throw new ConnectedServiceOperationTypeNotFoundException(@namespace, name);
        var usedByOperation = (await store.ListAllAsync<ConnectedServiceOperationResource>(
                ConnectedServiceResourceKinds.ConnectedServiceOperation, cancellationToken))
            .Any(value => References(value.Value.Definition.Type, existing.Value));
        var usedByServiceType = (await store.ListAllAsync<ConnectedServiceTypeResource>(
                ConnectedServiceResourceKinds.ConnectedServiceType, cancellationToken))
            .Any(value => value.Value.Definition.Versions.Any(version =>
                version.OperationDiscovery?.ProducedTypes.Any(reference => References(reference, existing.Value)) == true));
        if (usedByOperation || usedByServiceType)
            throw new ConnectedServiceOperationTypeInUseException(name);
        await scopeOperations.WriteAsync(existing.Value, scopeRef, AuthorizationPermissions.ResourcesDelete, async token =>
        {
            await store.DeleteExactAsync(Scoped(scopeRef, @namespace, name), etag, token);
            return true;
        }, cancellationToken);
    }

    public async Task<(ConnectedServiceOperationTypeResource Type, ConnectedServiceOperationTypeVersionDescriptor Version)>
        ResolveExactAsync(
            ConnectedServiceOperationTypeReference reference,
            ResourceNamespace ownerNamespace,
            ResourceScopeRef consumerScopeRef,
            CancellationToken cancellationToken)
    {
        var typeScope = reference.Type.ScopeRef
            ?? throw new ConnectedServiceOperationTypeValidationException("Operation type references require an exact scope.");
        if (!await useAuthorizer.IsSameOrDescendantAsync(typeScope, consumerScopeRef, cancellationToken))
            throw new ConnectedServiceOperationTypeValidationException("The operation type is outside the consumer scope chain.");
        var address = reference.Type.Resolve(ownerNamespace, ConnectedServiceResourceKinds.ConnectedServiceOperationType);
        var stored = await store.GetExactAsync<ConnectedServiceOperationTypeResource>(
            new(typeScope, address.Namespace, address.Kind, address.Name), cancellationToken)
            ?? throw new ConnectedServiceOperationTypeNotFoundException(address.Namespace, address.Name);
        if (stored.Value.Uid != reference.TypeUid)
            throw new ConnectedServiceOperationTypeRevisionException("The pinned operation type UID no longer matches.");
        if (!stored.Value.Definition.Enabled)
            throw new ConnectedServiceOperationTypeValidationException($"Operation type '{address}' is disabled.");
        if (!await useAuthorizer.CanUseAsync(typeScope, consumerScopeRef,
                stored.Value.Definition.UsePolicy, cancellationToken))
            throw new ConnectedServiceOperationTypeValidationException($"Operation type '{address}' cannot be used from this scope.");
        var version = stored.Value.Definition.Versions.SingleOrDefault(value =>
            string.Equals(value.Version, reference.Version, StringComparison.Ordinal))
            ?? throw new ConnectedServiceOperationTypeRevisionException(
                $"Operation type version '{reference.Version}' is unavailable.");
        if (!string.Equals(version.SchemaDigest, reference.SchemaDigest, StringComparison.Ordinal))
            throw new ConnectedServiceOperationTypeRevisionException("The pinned operation type schema digest no longer matches.");
        return (stored.Value, version);
    }

    private async Task ValidateAsync(
        ConnectedServiceOperationTypeResource resource,
        ConnectedServiceOperationTypeResource? existing,
        CancellationToken cancellationToken)
    {
        if (resource.Kind != ConnectedServiceResourceKinds.ConnectedServiceOperationType
            || resource.ApiVersion != ResourceApiVersions.CoreV1)
            throw new ConnectedServiceOperationTypeValidationException("Invalid ConnectedServiceOperationType envelope.");
        ValidateName(resource.Name);
        if (string.IsNullOrWhiteSpace(resource.Definition?.DisplayName)
            || resource.Definition.DisplayName.Length > MaximumDisplayNameLength)
            throw new ConnectedServiceOperationTypeValidationException(
                $"Operation type display names must contain 1 to {MaximumDisplayNameLength} characters.");
        if (resource.Definition.Description?.Length > MaximumDescriptionLength)
            throw new ConnectedServiceOperationTypeValidationException(
                $"Operation type descriptions cannot exceed {MaximumDescriptionLength} characters.");
        if (resource.Definition.Versions is null || resource.Definition.Versions.Count == 0
            || resource.Definition.Versions.Count > MaximumVersions)
            throw new ConnectedServiceOperationTypeValidationException(
                $"Operation types require 1 to {MaximumVersions} versions.");
        if (resource.Definition.Versions.Any(value => value is null)
            || resource.Definition.Versions.GroupBy(value => value.Version, StringComparer.Ordinal).Any(group => group.Count() > 1))
            throw new ConnectedServiceOperationTypeValidationException("Operation type versions must be non-null and unique.");
        if (resource.Definition.Versions.All(value =>
            !string.Equals(value.Version, resource.Definition.PreferredVersion, StringComparison.Ordinal)))
            throw new ConnectedServiceOperationTypeValidationException("The preferred operation type version was not published.");
        foreach (var version in resource.Definition.Versions) ValidateVersion(version);
        if (existing is not null)
            foreach (var previous in existing.Definition.Versions)
            {
                var candidate = resource.Definition.Versions.SingleOrDefault(value =>
                    string.Equals(value.Version, previous.Version, StringComparison.Ordinal));
                if (candidate is null || !VersionEquals(previous, candidate))
                    throw new ConnectedServiceOperationTypeValidationException(
                        $"Operation type version '{previous.Version}' is immutable.");
            }
        var scopeRef = resource.ScopeRef
            ?? throw new ConnectedServiceOperationTypeValidationException("Operation types require an exact scope.");
        ResourceScopePolicy.EnsureAllowed(resource, scopeRef);
        await useAuthorizer.ValidateAsync(scopeRef, resource.Definition.UsePolicy, cancellationToken);
    }

    private static void ValidateVersion(ConnectedServiceOperationTypeVersionDescriptor version)
    {
        if (string.IsNullOrWhiteSpace(version.Version) || version.Version.Length > 64)
            throw new ConnectedServiceOperationTypeValidationException("Operation type versions require a bounded identifier.");
        if (version.DefinitionSchema.ValueKind != JsonValueKind.Object
            || JsonSerializer.SerializeToUtf8Bytes(version.DefinitionSchema).Length > MaximumSchemaBytes)
            throw new ConnectedServiceOperationTypeValidationException("Operation type definition schemas must be bounded JSON objects.");
        if (!string.Equals(version.SchemaDigest, SchemaDigest(version.DefinitionSchema), StringComparison.Ordinal))
            throw new ConnectedServiceOperationTypeValidationException(
                $"Operation type version '{version.Version}' has an invalid schema digest.");
        if (!Enum.IsDefined(version.Interaction)
            || string.IsNullOrWhiteSpace(version.Invocation?.ProviderOperationId)
            || string.IsNullOrWhiteSpace(version.Invocation.ContractVersion))
            throw new ConnectedServiceOperationTypeValidationException(
                $"Operation type version '{version.Version}' requires an interaction and invocation contract.");
    }

    public static string SchemaDigest(JsonElement schema) =>
        $"sha256:{Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(schema, DigestOptions))).ToLowerInvariant()}";

    private static ConnectedServiceOperationTypeProperties MergeVersions(
        ConnectedServiceOperationTypeProperties existing,
        ConnectedServiceOperationTypeProperties desired)
    {
        if (desired is null || desired.Versions is null || desired.Versions.Any(value => value is null))
            throw new ConnectedServiceOperationTypeValidationException("Operation type properties require version descriptors.");
        var versions = existing.Versions.ToDictionary(value => value.Version, StringComparer.Ordinal);
        foreach (var version in desired.Versions)
        {
            if (versions.TryGetValue(version.Version, out var previous) && !VersionEquals(previous, version))
                throw new ConnectedServiceOperationTypeValidationException(
                    $"Operation type version '{version.Version}' is immutable.");
            versions[version.Version] = version;
        }
        return desired with { Versions = versions.Values.OrderBy(value => value.Version, StringComparer.Ordinal).ToArray() };
    }

    private static bool VersionEquals(ConnectedServiceOperationTypeVersionDescriptor left,
        ConnectedServiceOperationTypeVersionDescriptor right) =>
        string.Equals(JsonSerializer.Serialize(left), JsonSerializer.Serialize(right), StringComparison.Ordinal);
    private static bool DefinitionsEqual(ConnectedServiceOperationTypeProperties left,
        ConnectedServiceOperationTypeProperties right) =>
        string.Equals(JsonSerializer.Serialize(left), JsonSerializer.Serialize(right), StringComparison.Ordinal);
    private static bool References(ConnectedServiceOperationTypeReference reference,
        ConnectedServiceOperationTypeResource type)
    {
        var address = reference.Type.Resolve(type.Namespace, ConnectedServiceResourceKinds.ConnectedServiceOperationType);
        return reference.Type.ScopeRef == type.ScopeRef && address.Namespace == type.Namespace
            && address.Name == type.Name && reference.TypeUid == type.Uid;
    }
    private void EnsureManagedByCurrentAuthority(ConnectedServiceOperationTypeResource existing)
    {
        var expected = RequireManagementAuthority();
        if (existing.Governance.ManagedBy?.Kind != expected.Kind
            || !string.Equals(existing.Governance.ManagedBy.Reference, expected.Reference, StringComparison.Ordinal))
            throw new ResourceGovernanceViolationException("resource_managed",
                $"Operation type '{existing.Address}' is not managed by '{expected.Reference}'.");
    }
    private ResourceManagementAuthority RequireManagementAuthority() => governance.Current?.ManagedBy
        ?? throw new ResourceGovernanceViolationException("resource_governance_authority_required",
            "Managed operation type synchronization requires an active management authority.");
    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 128 || !char.IsLetterOrDigit(name[0])
            || name.AsSpan().IndexOfAnyExcept(NameCharacters) >= 0)
            throw new ConnectedServiceOperationTypeValidationException(
                "Operation type names must contain lowercase letters, digits, '-' or '.'.");
    }
    private static ScopedResourceAddress Scoped(ResourceScopeRef scopeRef, ResourceNamespace @namespace, string name) =>
        ScopedResourceAddress.Create(scopeRef, @namespace, ConnectedServiceResourceKinds.ConnectedServiceOperationType, name);
    private static ResourceStatus Succeeded() => new() { ProvisioningState = ProvisioningState.Succeeded };
}

public sealed class ConnectedServiceOperationTypeValidationException(string message) : Exception(message);
public sealed class ConnectedServiceOperationTypeNotFoundException(ResourceNamespace @namespace, string name)
    : Exception($"Connected Service operation type '{@namespace}/{name}' was not found.");
public sealed class ConnectedServiceOperationTypeRevisionException(string message) : Exception(message);
public sealed class ConnectedServiceOperationTypeInUseException(string name)
    : Exception($"Connected Service operation type '{name}' is in use.");
