using Agentstration.Identity.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;

namespace Agentstration.Tools;

public sealed class ToolCategoryValidationException(string message) : Exception(message);

public sealed class ToolCategoryService(
    IResourceStore store,
    IResourceReferenceResolver references,
    IResourceScopeOperations scopeOperations)
{
    public const int MaximumDisplayNameLength = 200;
    public const int MaximumDescriptionLength = 2_000;

    public Task<IReadOnlyList<StoredResource<ToolCategoryResource>>> ListAsync(CancellationToken cancellationToken) =>
        store.ListAllAsync<ToolCategoryResource>(ToolResourceKinds.ToolCategory, cancellationToken);

    public Task<StoredResource<ToolCategoryResource>?> GetAsync(
        ResourceNamespace @namespace,
        string name,
        CancellationToken cancellationToken) =>
        store.GetAsync<ToolCategoryResource>(new(ToolResourceKinds.ToolCategory, name, @namespace), cancellationToken);

    public async Task<StoredResource<ToolCategoryResource>> CreateAsync(
        ToolCategoryResource resource,
        CancellationToken cancellationToken)
    {
        Validate(resource);
        var scopeRef = resource.ScopeRef ?? scopeOperations.DefaultScopeRef(ToolResourceKinds.ToolCategory);
        ResourceScopePolicy.EnsureAllowed(resource, scopeRef);
        await ValidateReferencesAsync(resource, scopeRef, [], cancellationToken);
        var desired = resource with
        {
            ScopeRef = scopeRef,
            Generation = 1,
            Status = Succeeded()
        };
        return await scopeOperations.WriteAsync(desired, scopeRef, AuthorizationPermissions.ResourcesWrite,
            token => store.PutExactAsync(scopeRef, desired, null, true, token), cancellationToken);
    }

    public async Task<StoredResource<ToolCategoryResource>> PutAsync(
        ResourceNamespace @namespace,
        string name,
        ToolCategoryProperties definition,
        string? ifMatch,
        CancellationToken cancellationToken)
    {
        var existing = await GetAsync(@namespace, name, cancellationToken)
            ?? throw new ResourceNotFoundException(new(ToolResourceKinds.ToolCategory, name, @namespace));
        var scopeRef = existing.Value.ScopeRef
            ?? throw new ToolCategoryValidationException($"ToolCategory '{name}' has no ownership scope.");
        var updated = existing.Value with
        {
            Definition = definition,
            Generation = checked(existing.Value.Generation + 1),
            Status = Succeeded()
        };
        Validate(updated);
        await ValidateReferencesAsync(updated, scopeRef, existing.Value.Definition.Tools, cancellationToken);
        return await scopeOperations.WriteAsync(existing.Value, scopeRef, AuthorizationPermissions.ResourcesWrite,
            token => store.PutExactAsync(scopeRef, updated, ifMatch, false, token), cancellationToken);
    }

    public async Task DeleteAsync(
        ResourceNamespace @namespace,
        string name,
        string? ifMatch,
        CancellationToken cancellationToken)
    {
        var existing = await GetAsync(@namespace, name, cancellationToken)
            ?? throw new ResourceNotFoundException(new(ToolResourceKinds.ToolCategory, name, @namespace));
        var scopeRef = existing.Value.ScopeRef
            ?? throw new ToolCategoryValidationException($"ToolCategory '{name}' has no ownership scope.");
        await scopeOperations.WriteAsync(existing.Value, scopeRef, AuthorizationPermissions.ResourcesDelete, async token =>
        {
            await store.DeleteExactAsync(
                ScopedResourceAddress.Create(scopeRef, @namespace, ToolResourceKinds.ToolCategory, name),
                ifMatch,
                token);
            return true;
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<ToolCategoryMember>> GetMembersAsync(
        ResourceNamespace @namespace,
        string name,
        CancellationToken cancellationToken)
    {
        var category = await GetAsync(@namespace, name, cancellationToken)
            ?? throw new ResourceNotFoundException(new(ToolResourceKinds.ToolCategory, name, @namespace));
        var scopeRef = category.Value.ScopeRef
            ?? throw new ToolCategoryValidationException($"ToolCategory '{name}' has no ownership scope.");
        var members = new List<ToolCategoryMember>(category.Value.Definition.Tools.Count);
        foreach (var reference in category.Value.Definition.Tools)
        {
            var tool = await references.ResolveAsync<ToolResource>(
                reference,
                category.Value.Namespace,
                ToolResourceKinds.Tool,
                scopeRef,
                cancellationToken);
            members.Add(new(
                reference,
                tool?.Value,
                tool is null ? "missing" : tool.Value.Definition.Discovery?.Available == false ? "unavailable" : "available"));
        }
        return members;
    }

    private async Task ValidateReferencesAsync(
        ToolCategoryResource resource,
        ResourceScopeRef scopeRef,
        IReadOnlyList<ResourceReference> previouslyStored,
        CancellationToken cancellationToken)
    {
        var previous = previouslyStored.Select(reference => ReferenceKey(reference, resource.Namespace)).ToHashSet(StringComparer.Ordinal);
        foreach (var reference in resource.Definition.Tools)
        {
            if (await references.ResolveAsync<ToolResource>(
                    reference,
                    resource.Namespace,
                    ToolResourceKinds.Tool,
                    scopeRef,
                    cancellationToken) is not null)
                continue;
            if (previous.Contains(ReferenceKey(reference, resource.Namespace))) continue;
            var address = reference.Resolve(resource.Namespace, ToolResourceKinds.Tool);
            throw new ResourceNotFoundException(new(address.Kind, address.Name, address.Namespace));
        }
    }

    private static void Validate(ToolCategoryResource resource)
    {
        if (resource.Kind != ToolResourceKinds.ToolCategory)
            throw new ToolCategoryValidationException($"Kind must be '{ToolResourceKinds.ToolCategory}'.");
        if (resource.ApiVersion != ResourceApiVersions.CoreV1)
            throw new ToolCategoryValidationException($"ApiVersion must be '{ResourceApiVersions.CoreV1}'.");
        ValidateName(resource.Name);
        if (string.IsNullOrWhiteSpace(resource.Definition?.DisplayName)
            || resource.Definition.DisplayName.Length > MaximumDisplayNameLength)
            throw new ToolCategoryValidationException($"ToolCategory display names must contain 1 to {MaximumDisplayNameLength} characters.");
        if (resource.Definition.Description?.Length > MaximumDescriptionLength)
            throw new ToolCategoryValidationException($"ToolCategory descriptions cannot exceed {MaximumDescriptionLength} characters.");

        var tools = new HashSet<string>(StringComparer.Ordinal);
        foreach (var reference in resource.Definition.Tools)
        {
            ValidateName(reference.Name);
            var key = ReferenceKey(reference, resource.Namespace);
            if (!tools.Add(key))
                throw new ToolCategoryValidationException($"Tool reference '{reference.Name}' is duplicated.");
        }
    }

    private static void ValidateName(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128
            || value.Any(character => !(char.IsLetterOrDigit(character) || character is '.' or '-' or '_')))
            throw new ToolCategoryValidationException("ToolCategory and Tool reference names must contain only letters, digits, '.', '-' or '_' and be at most 128 characters.");
    }

    private static string ReferenceKey(ResourceReference reference, ResourceNamespace ownerNamespace) =>
        $"{reference.ScopeRef?.Value ?? string.Empty}|{(reference.Namespace ?? ownerNamespace).Value}|{reference.Name}";

    private static ResourceStatus Succeeded() => new() { ProvisioningState = ProvisioningState.Succeeded };
}
