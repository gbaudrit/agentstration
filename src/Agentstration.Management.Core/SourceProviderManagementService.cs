using Agentstration.Management.Abstractions;
using Agentstration.Resources;

namespace Agentstration.Management.Core;

public sealed class SourceProviderValidationException(string message) : Exception(message);
public sealed class SourceProviderNotFoundException(ResourceAddress address) : Exception($"Source provider '{address}' was not found.");

public sealed class SourceProviderManagementService(
    IControlPlaneStore store,
    IResourceReferenceResolver references,
    ResourceScopeOperationService scopeOperations)
{
    public Task<StoredResource<SourceProviderResource>?> GetAsync(
        ResourceNamespace @namespace,
        string name,
        CancellationToken cancellationToken) =>
        store.GetExactAsync<SourceProviderResource>(
            ScopedResourceAddress.Create(ResourceScopeRef.Instance, @namespace, ResourceKinds.SourceProvider, name),
            cancellationToken);

    public Task<IReadOnlyList<StoredResource<SourceProviderResource>>> ListAsync(CancellationToken cancellationToken) =>
        store.ListExactAsync<SourceProviderResource>(ResourceScopeRef.Instance, ResourceKinds.SourceProvider, 0, int.MaxValue, cancellationToken);

    public async Task<StoredResource<SourceProviderResource>> CreateAsync(
        SourceProviderResource resource,
        CancellationToken cancellationToken)
    {
        ValidateIdentity(resource);
        var scopeRef = resource.ScopeRef ?? ResourceScopeRef.Instance;
        return await scopeOperations.WriteAsync(resource, scopeRef, AuthorizationPermissions.ResourcesWrite, async token =>
        {
            if (await GetAsync(resource.Namespace, resource.Name, token) is not null)
                throw new ControlPlaneConcurrencyException($"Source provider '{resource.Address}' already exists.");
            var definition = await ValidateDefinitionAsync(resource.Namespace, resource.Definition, scopeRef, token);
            return await store.PutExactAsync(scopeRef, resource with
            {
                ScopeRef = scopeRef,
                Generation = 1,
                Definition = definition,
                Status = new ResourceStatus { ProvisioningState = ProvisioningState.Succeeded }
            }, null, true, token);
        }, cancellationToken);
    }

    public async Task<StoredResource<SourceProviderResource>> PutAsync(
        ResourceNamespace @namespace,
        string name,
        SourceProviderProperties definition,
        string? ifMatch,
        CancellationToken cancellationToken)
    {
        var existing = await GetAsync(@namespace, name, cancellationToken)
            ?? throw new SourceProviderNotFoundException(new(@namespace, ResourceKinds.SourceProvider, name));
        var scopeRef = existing.Value.ScopeRef
            ?? throw new SourceProviderValidationException("The Source Provider has no ownership scope.");
        return await scopeOperations.WriteAsync(existing.Value, scopeRef, AuthorizationPermissions.ResourcesWrite, async token =>
        {
            var validated = await ValidateDefinitionAsync(@namespace, definition, scopeRef, token);
            return await store.PutExactAsync(scopeRef, existing.Value with
            {
                Generation = checked(existing.Value.Generation + 1),
                Definition = validated,
                Status = new ResourceStatus { ProvisioningState = ProvisioningState.Succeeded }
            }, ifMatch, false, token);
        }, cancellationToken);
    }

    public async Task DeleteAsync(
        ResourceNamespace @namespace,
        string name,
        string? ifMatch,
        CancellationToken cancellationToken)
    {
        var existing = await GetAsync(@namespace, name, cancellationToken)
            ?? throw new SourceProviderNotFoundException(new(@namespace, ResourceKinds.SourceProvider, name));
        var scopeRef = existing.Value.ScopeRef
            ?? throw new SourceProviderValidationException("The Source Provider has no ownership scope.");
        await scopeOperations.WriteAsync(existing.Value, scopeRef, AuthorizationPermissions.ResourcesDelete, async token =>
        {
            await store.DeleteExactAsync(
                ScopedResourceAddress.Create(scopeRef, @namespace, ResourceKinds.SourceProvider, name),
                ifMatch,
                token);
            return true;
        }, cancellationToken);
    }

    private async Task<SourceProviderProperties> ValidateDefinitionAsync(
        ResourceNamespace ownerNamespace,
        SourceProviderProperties definition,
        ResourceScopeRef ownerScopeRef,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (string.IsNullOrWhiteSpace(definition.DisplayName))
            throw new SourceProviderValidationException("A display name is required.");
        if (string.IsNullOrWhiteSpace(definition.ContributionId))
            throw new SourceProviderValidationException("An AEP source-provider contribution id is required.");
        var extensionAddress = definition.Extension.Resolve(ownerNamespace, ResourceKinds.ExtensionRegistration);
        if (await references.ResolveAsync<ExtensionRegistrationResource>(
                definition.Extension,
                ownerNamespace,
                ResourceKinds.ExtensionRegistration,
                ownerScopeRef,
                cancellationToken) is null)
            throw new SourceProviderValidationException($"Referenced extension registration '{extensionAddress}' does not exist.");
        return definition with
        {
            DisplayName = definition.DisplayName.Trim(),
            ContributionId = definition.ContributionId.Trim()
        };
    }

    private static void ValidateIdentity(SourceProviderResource resource)
    {
        if (resource.Kind != ResourceKinds.SourceProvider)
            throw new SourceProviderValidationException($"Kind must be '{ResourceKinds.SourceProvider}'.");
        if (resource.ApiVersion != ManagementApiVersions.CoreV1)
            throw new SourceProviderValidationException($"ApiVersion must be '{ManagementApiVersions.CoreV1}'.");
        if (string.IsNullOrWhiteSpace(resource.Name))
            throw new SourceProviderValidationException("A resource name is required.");
    }
}
