using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;
using Agentstration.ConnectedServices.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;

namespace Agentstration.ConnectedServices;

public sealed class ConnectedServiceTypeManagementService(
    IResourceStore store,
    IResourceScopeOperations scopeOperations,
    DescendantResourceUseAuthorizer useAuthorizer,
    IResourceGovernanceContext governance)
{
    public const int MaximumDisplayNameLength = 200;
    public const int MaximumDescriptionLength = 2_000;
    public const int MaximumSchemaBytes = 256 * 1024;
    public const int MaximumVersions = 32;
    public const int MaximumInputs = 64;
    public const int MaximumExports = 64;
    private static readonly JsonSerializerOptions SchemaDigestJsonOptions =
        new(JsonSerializerDefaults.Web);
    private static readonly SearchValues<char> MemberIdCharacters =
        SearchValues.Create("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789._-");

    public async Task<IReadOnlyList<StoredResource<ConnectedServiceTypeResource>>> ListAsync(
        CancellationToken cancellationToken) =>
        (await store.ListAllAsync<ConnectedServiceTypeResource>(
            ConnectedServiceResourceKinds.ConnectedServiceType, cancellationToken))
        .OrderBy(value => value.Value.Definition.DisplayName, StringComparer.OrdinalIgnoreCase)
        .ThenBy(value => value.Value.Namespace.Value, StringComparer.Ordinal)
        .ThenBy(value => value.Value.Name, StringComparer.Ordinal)
        .ToArray();

    public Task<StoredResource<ConnectedServiceTypeResource>?> GetExactAsync(
        ResourceScopeRef scopeRef,
        ResourceNamespace @namespace,
        string name,
        CancellationToken cancellationToken) =>
        store.GetExactAsync<ConnectedServiceTypeResource>(Scoped(scopeRef, @namespace, name), cancellationToken);

    public async Task ValidateForCreateAsync(
        ConnectedServiceTypeResource resource,
        CancellationToken cancellationToken)
    {
        var scopeRef = resource.ScopeRef
            ?? scopeOperations.DefaultScopeRef(ConnectedServiceResourceKinds.ConnectedServiceType);
        await ValidateAsync(resource with { ScopeRef = scopeRef }, null, cancellationToken);
    }

    public async Task<StoredResource<ConnectedServiceTypeResource>> CreateAsync(
        ConnectedServiceTypeResource resource,
        CancellationToken cancellationToken)
    {
        var scopeRef = resource.ScopeRef
            ?? scopeOperations.DefaultScopeRef(ConnectedServiceResourceKinds.ConnectedServiceType);
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

    public async Task<StoredResource<ConnectedServiceTypeResource>> PutExactAsync(
        ResourceScopeRef scopeRef,
        ResourceNamespace @namespace,
        string name,
        ConnectedServiceTypeProperties definition,
        string? etag,
        CancellationToken cancellationToken)
    {
        if (definition is null)
            throw new ConnectedServiceTypeValidationException(
                "Connected Service type properties are required.");
        var existing = await GetExactAsync(scopeRef, @namespace, name, cancellationToken)
            ?? throw new ConnectedServiceTypeNotFoundException(@namespace, name);
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

    public async Task<StoredResource<ConnectedServiceTypeResource>> SynchronizeManagedAsync(
        ConnectedServiceTypeResource desired,
        CancellationToken cancellationToken)
    {
        _ = RequireManagementAuthority();
        var scopeRef = desired.ScopeRef
            ?? throw new ConnectedServiceTypeValidationException("Managed Connected Service types require an exact scope.");
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
            ?? throw new ConnectedServiceTypeNotFoundException(@namespace, name);
        var used = (await store.ListAllAsync<ConnectedServiceResource>(
                ConnectedServiceResourceKinds.ConnectedService, cancellationToken))
            .Any(value => References(value.Value, existing.Value));
        if (used) throw new ConnectedServiceTypeInUseException(name);
        await scopeOperations.WriteAsync(existing.Value, scopeRef, AuthorizationPermissions.ResourcesDelete, async token =>
        {
            await store.DeleteExactAsync(Scoped(scopeRef, @namespace, name), etag, token);
            return true;
        }, cancellationToken);
    }

    private async Task ValidateAsync(
        ConnectedServiceTypeResource resource,
        ConnectedServiceTypeResource? existing,
        CancellationToken cancellationToken)
    {
        if (resource.Kind != ConnectedServiceResourceKinds.ConnectedServiceType
            || resource.ApiVersion != ResourceApiVersions.CoreV1)
            throw new ConnectedServiceTypeValidationException("Invalid ConnectedServiceType resource envelope.");
        ValidateName(resource.Name);
        if (string.IsNullOrWhiteSpace(resource.Definition?.DisplayName)
            || resource.Definition.DisplayName.Length > MaximumDisplayNameLength)
            throw new ConnectedServiceTypeValidationException(
                $"Connected Service type display names must contain 1 to {MaximumDisplayNameLength} characters.");
        if (resource.Definition.Description?.Length > MaximumDescriptionLength)
            throw new ConnectedServiceTypeValidationException(
                $"Connected Service type descriptions cannot exceed {MaximumDescriptionLength} characters.");
        if (resource.Definition.Versions is null || resource.Definition.Versions.Count == 0)
            throw new ConnectedServiceTypeValidationException("Connected Service types require at least one version.");
        if (resource.Definition.Versions.Count > MaximumVersions)
            throw new ConnectedServiceTypeValidationException(
                $"Connected Service types may contain at most {MaximumVersions} versions.");
        if (resource.Definition.Versions.Any(value => value is null))
            throw new ConnectedServiceTypeValidationException("Connected Service type versions cannot be null.");
        if (resource.Definition.Versions.GroupBy(value => value.Version, StringComparer.Ordinal)
            .Any(group => group.Count() > 1))
            throw new ConnectedServiceTypeValidationException("Connected Service type versions must be unique.");
        if (resource.Definition.Versions.All(value =>
            !string.Equals(value.Version, resource.Definition.PreferredVersion, StringComparison.Ordinal)))
            throw new ConnectedServiceTypeValidationException("The preferred Connected Service type version was not published.");
        foreach (var version in resource.Definition.Versions) ValidateVersion(version);
        if (existing is not null)
            foreach (var previous in existing.Definition.Versions)
            {
                var candidate = resource.Definition.Versions.SingleOrDefault(value =>
                    string.Equals(value.Version, previous.Version, StringComparison.Ordinal));
                if (candidate is null || !VersionEquals(previous, candidate))
                    throw new ConnectedServiceTypeValidationException(
                        $"Connected Service type version '{previous.Version}' is immutable.");
            }
        var scopeRef = resource.ScopeRef
            ?? throw new ConnectedServiceTypeValidationException("Connected Service types require an exact scope.");
        ResourceScopePolicy.EnsureAllowed(resource, scopeRef);
        await useAuthorizer.ValidateAsync(scopeRef, resource.Definition.UsePolicy, cancellationToken);
    }

    private static void ValidateVersion(ConnectedServiceTypeVersionDescriptor version)
    {
        if (string.IsNullOrWhiteSpace(version.Version) || version.Version.Length > 64
            || version.Version.Any(character => !char.IsLetterOrDigit(character)
                && character is not '.' and not '-'))
            throw new ConnectedServiceTypeValidationException(
                "Connected Service type versions must contain only letters, digits, '.' or '-' and be at most 64 characters.");
        if (string.IsNullOrWhiteSpace(version.SchemaDigest))
            throw new ConnectedServiceTypeValidationException("Connected Service type versions require a schema digest.");
        if (version.Schema.ValueKind != JsonValueKind.Object)
            throw new ConnectedServiceTypeValidationException("Connected Service type schemas must be JSON objects.");
        if (JsonSerializer.SerializeToUtf8Bytes(version.Schema).Length > MaximumSchemaBytes)
            throw new ConnectedServiceTypeValidationException(
                $"Connected Service type schemas cannot exceed {MaximumSchemaBytes} bytes.");
        if (!string.Equals(version.SchemaDigest, SchemaDigest(version.Schema), StringComparison.Ordinal))
            throw new ConnectedServiceTypeValidationException(
                $"Connected Service type version '{version.Version}' has an invalid schema digest.");
        if (version.Inputs is null || version.Exports is null)
            throw new ConnectedServiceTypeValidationException("Connected Service type inputs and exports cannot be null.");
        if (version.Inputs.Count > MaximumInputs || version.Exports.Count > MaximumExports)
            throw new ConnectedServiceTypeValidationException(
                $"Connected Service type versions may contain at most {MaximumInputs} inputs and {MaximumExports} exports.");
        if (version.Inputs.Any(value => value is null) || version.Exports.Any(value => value is null))
            throw new ConnectedServiceTypeValidationException(
                "Connected Service type inputs and exports cannot contain null descriptors.");
        if (version.Inputs.GroupBy(value => value.Id, StringComparer.Ordinal).Any(group => group.Count() > 1)
            || version.Exports.GroupBy(value => value.Id, StringComparer.Ordinal).Any(group => group.Count() > 1))
            throw new ConnectedServiceTypeValidationException("Connected Service type input and export identifiers must be unique.");
        if (version.Inputs.Any(value => !ValidMemberId(value.Id))
            || version.Exports.Any(value => !ValidMemberId(value.Id)))
            throw new ConnectedServiceTypeValidationException(
                "Connected Service type input and export identifiers must start with a lowercase letter and contain at most 64 letters, digits, '.', '_' or '-'.");
        foreach (var export in version.Exports.Where(value => value.SourceInputId is not null))
        {
            var input = version.Inputs.SingleOrDefault(value =>
                string.Equals(value.Id, export.SourceInputId, StringComparison.Ordinal));
            if (input is null || input.Type != export.Type || input.Protection != export.Protection)
                throw new ConnectedServiceTypeValidationException(
                    $"Export '{export.Id}' must reference an input with the same type and protection.");
        }
        ValidateOperationDiscovery(version.OperationDiscovery);
    }

    private static void ValidateOperationDiscovery(ConnectedServiceOperationDiscoveryDescriptor? discovery)
    {
        if (discovery is null) return;
        var flow = discovery.Flow
            ?? throw new ConnectedServiceTypeValidationException("Operation discovery requires an exact Flow reference.");
        if (flow.Flow.ScopeRef is null || flow.FlowUid == Guid.Empty
            || string.IsNullOrWhiteSpace(flow.Version) || string.IsNullOrWhiteSpace(flow.DefinitionHash))
            throw new ConnectedServiceTypeValidationException(
                "Operation discovery Flow references require an exact scope, UID, version and definition hash.");
        if (!string.Equals(flow.Contract, ConnectedServiceOperationDiscoveryContracts.V1, StringComparison.Ordinal))
            throw new ConnectedServiceTypeValidationException(
                $"Operation discovery Flows must declare contract '{ConnectedServiceOperationDiscoveryContracts.V1}'.");
        if (discovery.ProducedTypes is null || discovery.ProducedTypes.Count == 0)
            throw new ConnectedServiceTypeValidationException(
                "Operation discovery must declare at least one produced operation type.");
        if (discovery.ProducedTypes.Any(reference => reference.Type.ScopeRef is null
                || reference.TypeUid == Guid.Empty || string.IsNullOrWhiteSpace(reference.Version)
                || string.IsNullOrWhiteSpace(reference.SchemaDigest)))
            throw new ConnectedServiceTypeValidationException(
                "Produced operation types require an exact scope, UID, version and schema digest.");
        if (discovery.ProducedTypes.GroupBy(reference => new
            {
                reference.Type.ScopeRef,
                Namespace = reference.Type.Namespace?.Value,
                reference.Type.Name,
                reference.TypeUid,
                reference.Version
            }).Any(group => group.Count() > 1))
            throw new ConnectedServiceTypeValidationException("Produced operation type references must be unique.");
    }

    private static ConnectedServiceTypeProperties MergeVersions(
        ConnectedServiceTypeProperties existing,
        ConnectedServiceTypeProperties desired)
    {
        if (desired is null || desired.Versions is null || desired.Versions.Any(value => value is null))
            throw new ConnectedServiceTypeValidationException(
                "Connected Service type properties require non-null version descriptors.");
        var versions = existing.Versions.ToDictionary(value => value.Version, StringComparer.Ordinal);
        foreach (var version in desired.Versions)
        {
            if (versions.TryGetValue(version.Version, out var previous) && !VersionEquals(previous, version))
                throw new ConnectedServiceTypeValidationException(
                    $"Connected Service type version '{version.Version}' is immutable.");
            versions[version.Version] = version;
        }
        return desired with
        {
            Versions = versions.Values.OrderBy(value => value.Version, StringComparer.Ordinal).ToArray()
        };
    }

    private static bool VersionEquals(
        ConnectedServiceTypeVersionDescriptor left,
        ConnectedServiceTypeVersionDescriptor right) =>
        string.Equals(JsonSerializer.Serialize(left), JsonSerializer.Serialize(right), StringComparison.Ordinal);

    private static bool DefinitionsEqual(
        ConnectedServiceTypeProperties left,
        ConnectedServiceTypeProperties right) =>
        string.Equals(JsonSerializer.Serialize(left), JsonSerializer.Serialize(right), StringComparison.Ordinal);

    private static string SchemaDigest(JsonElement schema)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(schema, SchemaDigestJsonOptions);
        return $"sha256:{Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()}";
    }

    private static bool ValidMemberId(string? id) =>
        id is { Length: >= 1 and <= 64 }
        && id[0] is >= 'a' and <= 'z'
        && id.AsSpan(1).IndexOfAnyExcept(MemberIdCharacters) < 0;

    private static bool References(
        ConnectedServiceResource service,
        ConnectedServiceTypeResource type)
    {
        var reference = service.Definition.Type.Type;
        var address = reference.Resolve(service.Namespace, ConnectedServiceResourceKinds.ConnectedServiceType);
        return reference.ScopeRef == type.ScopeRef
            && address.Namespace == type.Namespace
            && string.Equals(address.Name, type.Name, StringComparison.Ordinal)
            && service.Definition.Type.TypeUid == type.Uid;
    }

    private void EnsureManagedByCurrentAuthority(ConnectedServiceTypeResource existing)
    {
        var expected = RequireManagementAuthority();
        if (existing.Governance.ManagedBy?.Kind != expected.Kind
            || !string.Equals(existing.Governance.ManagedBy.Reference, expected.Reference, StringComparison.Ordinal))
            throw new ResourceGovernanceViolationException("resource_managed",
                $"Connected Service type '{existing.Address}' is not managed by '{expected.Reference}'.");
    }

    private ResourceManagementAuthority RequireManagementAuthority() =>
        governance.Current?.ManagedBy
        ?? throw new ResourceGovernanceViolationException("resource_governance_authority_required",
            "Managed Connected Service type synchronization requires an active management authority.");

    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 128 || !char.IsLetterOrDigit(name[0])
            || name.Any(character => !char.IsLower(character) && !char.IsDigit(character)
                && character is not '-' and not '.'))
            throw new ConnectedServiceTypeValidationException(
                "Connected Service type names must contain 1 to 128 lowercase letters, digits, '-' or '.', and start with a letter or digit.");
    }

    private static ScopedResourceAddress Scoped(
        ResourceScopeRef scopeRef,
        ResourceNamespace @namespace,
        string name) =>
        ScopedResourceAddress.Create(scopeRef, @namespace,
            ConnectedServiceResourceKinds.ConnectedServiceType, name);

    private static ResourceStatus Succeeded() => new() { ProvisioningState = ProvisioningState.Succeeded };
}

public sealed class ConnectedServiceTypeValidationException(string message) : Exception(message);
public sealed class ConnectedServiceTypeNotFoundException(ResourceNamespace @namespace, string name)
    : Exception($"Connected Service type '{@namespace}/{name}' was not found.");
public sealed class ConnectedServiceTypeInUseException(string name)
    : Exception($"Connected Service type '{name}' is referenced by one or more Connected Services.");
