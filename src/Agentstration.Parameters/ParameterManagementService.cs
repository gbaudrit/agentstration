using System.Text;
using System.Text.Json;
using Agentstration.Identity.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;

namespace Agentstration.Parameters;

public sealed class ParameterManagementService(
    IResourceStore store,
    IResourceScopeOperations scopeOperations,
    DescendantResourceUseAuthorizer useAuthorizer,
    IEnumerable<IParameterUsageProvider> usageProviders) : IParameterResolver
{
    public const int MaximumValueBytes = 65_536;
    public const int MaximumDisplayNameLength = 200;
    public const int MaximumDescriptionLength = 2_000;

    public Task<IReadOnlyList<StoredResource<ParameterResource>>> ListAsync(CancellationToken cancellationToken) =>
        store.ListAllAsync<ParameterResource>(ParameterResourceKinds.Parameter, cancellationToken);

    public Task<StoredResource<ParameterResource>?> GetAsync(string name, CancellationToken cancellationToken) =>
        store.GetAsync<ParameterResource>(new(ParameterResourceKinds.Parameter, name), cancellationToken);

    public Task<StoredResource<ParameterResource>?> GetExactAsync(ResourceScopeRef scopeRef, string name,
        CancellationToken cancellationToken) => store.GetExactAsync<ParameterResource>(Scoped(scopeRef, name), cancellationToken);

    public async Task ValidateForCreateAsync(ParameterResource resource, CancellationToken cancellationToken)
    {
        var scopeRef = resource.ScopeRef ?? scopeOperations.DefaultScopeRef(ParameterResourceKinds.Parameter);
        ResourceScopePolicy.EnsureAllowed(resource, scopeRef);
        await ValidateAsync(resource with { ScopeRef = scopeRef }, cancellationToken);
    }

    public async Task<StoredResource<ParameterResource>> CreateAsync(ParameterResource resource,
        CancellationToken cancellationToken)
    {
        var scopeRef = resource.ScopeRef ?? scopeOperations.DefaultScopeRef(ParameterResourceKinds.Parameter);
        var scoped = resource with { ScopeRef = scopeRef };
        return await scopeOperations.WriteAsync(scoped, scopeRef, AuthorizationPermissions.ResourcesWrite, async token =>
        {
            await ValidateForCreateAsync(scoped, token);
            return await store.PutExactAsync(scopeRef,
                scoped with { Generation = 1, Status = Succeeded() }, null, true, token);
        }, cancellationToken);
    }

    public async Task<StoredResource<ParameterResource>> PutAsync(string name, ParameterProperties definition,
        string? etag, CancellationToken cancellationToken)
    {
        var existing = await GetAsync(name, cancellationToken) ?? throw new ParameterResourceNotFoundException(name);
        return await PutAsync(existing, definition, etag, cancellationToken);
    }

    public async Task<StoredResource<ParameterResource>> PutExactAsync(ResourceScopeRef scopeRef, string name,
        ParameterProperties definition, string? etag, CancellationToken cancellationToken)
    {
        var existing = await GetExactAsync(scopeRef, name, cancellationToken)
            ?? throw new ParameterResourceNotFoundException(name);
        return await PutAsync(existing, definition, etag, cancellationToken);
    }

    public async Task DeleteAsync(string name, string? etag, CancellationToken cancellationToken)
    {
        var existing = await GetAsync(name, cancellationToken) ?? throw new ParameterResourceNotFoundException(name);
        await DeleteAsync(existing, etag, cancellationToken);
    }

    public async Task DeleteExactAsync(ResourceScopeRef scopeRef, string name, string? etag,
        CancellationToken cancellationToken)
    {
        var existing = await GetExactAsync(scopeRef, name, cancellationToken)
            ?? throw new ParameterResourceNotFoundException(name);
        await DeleteAsync(existing, etag, cancellationToken);
    }

    public async Task<IReadOnlyList<ParameterUsage>> GetUsagesAsync(ResourceScopeRef scopeRef, string name,
        CancellationToken cancellationToken)
    {
        _ = await GetExactAsync(scopeRef, name, cancellationToken) ?? throw new ParameterResourceNotFoundException(name);
        var address = Scoped(scopeRef, name);
        var usages = new List<ParameterUsage>();
        foreach (var provider in usageProviders)
            usages.AddRange(await provider.GetUsagesAsync(address, cancellationToken));
        return usages;
    }

    public async Task<ResolvedParameter?> ResolveAsync(ParameterReference parameter, ParameterResolutionContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parameter);
        ArgumentNullException.ThrowIfNull(context);
        if (parameter.Address.Kind != ParameterResourceKinds.Parameter)
            throw new ParameterManagementException("The referenced resource must be a Parameter.");
        if (!await useAuthorizer.IsSameOrDescendantAsync(parameter.ScopeRef, context.ConsumerScopeRef, cancellationToken))
            throw new ParameterAccessDeniedException(parameter.Address);
        var stored = await store.GetExactAsync<ParameterResource>(ScopedResourceAddress.Create(
            parameter.ScopeRef, parameter.Address.Namespace, ParameterResourceKinds.Parameter, parameter.Address.Name), cancellationToken);
        if (stored is null) return null;
        if (!await useAuthorizer.CanUseAsync(parameter.ScopeRef, context.ConsumerScopeRef,
                stored.Value.Definition.UsePolicy, cancellationToken))
            throw new ParameterAccessDeniedException(parameter.Address);
        return new(parameter.Address, stored.Value.Definition.Value.Clone());
    }

    private async Task<StoredResource<ParameterResource>> PutAsync(StoredResource<ParameterResource> existing,
        ParameterProperties definition, string? etag, CancellationToken cancellationToken)
    {
        var scopeRef = RequireScope(existing.Value);
        var changed = existing.Value with { Definition = definition };
        return await scopeOperations.WriteAsync(existing.Value, scopeRef, AuthorizationPermissions.ResourcesWrite, async token =>
        {
            await ValidateAsync(changed, token);
            return await store.PutExactAsync(scopeRef, changed with
            {
                Generation = checked(existing.Value.Generation + 1),
                Status = Succeeded()
            }, etag, false, token);
        }, cancellationToken);
    }

    private async Task DeleteAsync(StoredResource<ParameterResource> existing, string? etag,
        CancellationToken cancellationToken)
    {
        var scopeRef = RequireScope(existing.Value);
        await scopeOperations.WriteAsync(ParameterResourceKinds.Parameter, scopeRef,
            AuthorizationPermissions.ResourcesDelete, async token =>
            {
                if ((await GetUsagesAsync(scopeRef, existing.Value.Name, token)).Count > 0)
                    throw new ParameterInUseException(existing.Value.Name);
                await store.DeleteExactAsync(Scoped(scopeRef, existing.Value.Name), etag, token);
                return true;
            }, cancellationToken);
    }

    private async Task ValidateAsync(ParameterResource resource, CancellationToken cancellationToken)
    {
        if (resource.Kind != ParameterResourceKinds.Parameter || resource.ApiVersion != ResourceApiVersions.CoreV1)
            throw new ParameterManagementException("Invalid Parameter resource envelope.");
        ValidateName(resource.Metadata.Name);
        if (string.IsNullOrWhiteSpace(resource.Definition?.DisplayName)
            || resource.Definition.DisplayName.Length > MaximumDisplayNameLength)
            throw new ParameterManagementException($"Parameter display names must contain 1 to {MaximumDisplayNameLength} characters.");
        if (resource.Definition.Description?.Length > MaximumDescriptionLength)
            throw new ParameterManagementException($"Parameter descriptions cannot exceed {MaximumDescriptionLength} characters.");
        ValidateValue(resource.Definition.ValueType, resource.Definition.Value);
        await useAuthorizer.ValidateAsync(RequireScope(resource), resource.Definition.UsePolicy, cancellationToken);
    }

    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 128 || !char.IsLetterOrDigit(name[0])
            || name.Any(character => !char.IsLower(character) && !char.IsDigit(character) && character is not '-' and not '.'))
            throw new ParameterManagementException("Parameter names must contain 1 to 128 lowercase letters, digits, '-' or '.', and start with a letter or digit.");
    }

    private static void ValidateValue(ParameterValueType type, JsonElement value)
    {
        var matches = type switch
        {
            ParameterValueType.Text => value.ValueKind == JsonValueKind.String,
            ParameterValueType.WholeNumber => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
            ParameterValueType.DecimalNumber => value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)
                && double.IsFinite(number),
            ParameterValueType.Logical => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            _ => false
        };
        if (!matches) throw new ParameterManagementException($"The Parameter value does not match type '{type}'.");
        if (Encoding.UTF8.GetByteCount(value.GetRawText()) > MaximumValueBytes)
            throw new ParameterManagementException($"Parameter values cannot exceed {MaximumValueBytes} UTF-8 bytes.");
    }

    private static ResourceScopeRef RequireScope(Resource resource) => resource.ScopeRef
        ?? throw new ParameterManagementException($"Resource '{resource.Address}' has no ownership scope.");
    private static ScopedResourceAddress Scoped(ResourceScopeRef scopeRef, string name) =>
        ScopedResourceAddress.Create(scopeRef, ResourceNamespace.Default, ParameterResourceKinds.Parameter, name);
    private static ResourceStatus Succeeded() => new() { ProvisioningState = ProvisioningState.Succeeded };
}
