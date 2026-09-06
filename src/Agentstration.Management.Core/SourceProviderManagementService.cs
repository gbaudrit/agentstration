using Agentstration.Management.Abstractions;
using Agentstration.Resources;

namespace Agentstration.Management.Core;

public sealed class SourceProviderValidationException(string message) : Exception(message);
public sealed class SourceProviderNotFoundException(ResourceAddress address) : Exception($"Source provider '{address}' was not found.");

public sealed class SourceProviderManagementService(IControlPlaneStore store)
{
    public Task<StoredResource<SourceProviderResource>?> GetAsync(
        ResourceNamespace @namespace,
        string name,
        CancellationToken cancellationToken) =>
        store.GetAsync<SourceProviderResource>(new(ResourceKinds.SourceProvider, name, @namespace), cancellationToken);

    public Task<IReadOnlyList<StoredResource<SourceProviderResource>>> ListAsync(CancellationToken cancellationToken) =>
        store.ListAllAsync<SourceProviderResource>(ResourceKinds.SourceProvider, cancellationToken);

    public async Task<StoredResource<SourceProviderResource>> CreateAsync(
        SourceProviderResource resource,
        CancellationToken cancellationToken)
    {
        ValidateIdentity(resource);
        if (await GetAsync(resource.Namespace, resource.Name, cancellationToken) is not null)
            throw new ControlPlaneConcurrencyException($"Source provider '{resource.Address}' already exists.");
        var definition = await ValidateDefinitionAsync(resource.Namespace, resource.Definition, cancellationToken);
        return await store.PutAsync(resource with
        {
            Generation = 1,
            Definition = definition,
            Status = new ResourceStatus { ProvisioningState = ProvisioningState.Succeeded }
        }, null, true, cancellationToken);
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
        var validated = await ValidateDefinitionAsync(@namespace, definition, cancellationToken);
        return await store.PutAsync(existing.Value with
        {
            Generation = checked(existing.Value.Generation + 1),
            Definition = validated,
            Status = new ResourceStatus { ProvisioningState = ProvisioningState.Succeeded }
        }, ifMatch, false, cancellationToken);
    }

    public async Task DeleteAsync(
        ResourceNamespace @namespace,
        string name,
        string? ifMatch,
        CancellationToken cancellationToken)
    {
        _ = await GetAsync(@namespace, name, cancellationToken)
            ?? throw new SourceProviderNotFoundException(new(@namespace, ResourceKinds.SourceProvider, name));
        await store.DeleteAsync(new(ResourceKinds.SourceProvider, name, @namespace), ifMatch, cancellationToken);
    }

    private async Task<SourceProviderProperties> ValidateDefinitionAsync(
        ResourceNamespace ownerNamespace,
        SourceProviderProperties definition,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (string.IsNullOrWhiteSpace(definition.DisplayName))
            throw new SourceProviderValidationException("A display name is required.");
        if (string.IsNullOrWhiteSpace(definition.ContributionId))
            throw new SourceProviderValidationException("An AEP source-provider contribution id is required.");
        if (definition.Extension.WorkspaceRef is not null)
            throw new SourceProviderValidationException("Cross-workspace extension references are not supported.");
        var extensionAddress = definition.Extension.Resolve(ownerNamespace, ResourceKinds.ExtensionRegistration);
        if (await store.GetAsync<ExtensionRegistrationResource>(new(extensionAddress.Kind, extensionAddress.Name, extensionAddress.Namespace), cancellationToken) is null)
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
