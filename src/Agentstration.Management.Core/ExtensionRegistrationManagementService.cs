using Agentstration.Management.Abstractions;
using Agentstration.ModelProviders;
using Agentstration.Resources;

namespace Agentstration.Management.Core;

public sealed class ExtensionRegistrationValidationException(string message) : Exception(message);
public sealed class ExtensionRegistrationNotFoundException(ResourceAddress address) : Exception($"Extension registration '{address}' was not found.");

public sealed class ExtensionRegistrationManagementService(IControlPlaneStore store) : IExtensionEndpointSource
{
    public Task<StoredResource<ExtensionRegistrationResource>?> GetAsync(
        ResourceNamespace @namespace,
        string name,
        CancellationToken cancellationToken) =>
        store.GetAsync<ExtensionRegistrationResource>(
            new(ResourceKinds.ExtensionRegistration, name, @namespace),
            cancellationToken);

    public Task<IReadOnlyList<StoredResource<ExtensionRegistrationResource>>> ListAsync(
        CancellationToken cancellationToken) =>
        store.ListAllAsync<ExtensionRegistrationResource>(ResourceKinds.ExtensionRegistration, cancellationToken);

    public async Task<StoredResource<ExtensionRegistrationResource>> CreateAsync(
        ExtensionRegistrationResource resource,
        CancellationToken cancellationToken)
    {
        ValidateIdentity(resource);
        var definition = await ValidateDefinitionAsync(resource.Namespace, resource.Metadata.Name, resource.Definition, cancellationToken);
        if (await GetAsync(resource.Namespace, resource.Name, cancellationToken) is not null)
            throw new ControlPlaneConcurrencyException($"Extension registration '{resource.Address}' already exists.");
        return await store.PutAsync(
            resource with
            {
                Generation = 1,
                Definition = definition,
                Status = new ResourceStatus { ProvisioningState = ProvisioningState.Succeeded }
            },
            null,
            true,
            cancellationToken);
    }

    public async Task<StoredResource<ExtensionRegistrationResource>> PutAsync(
        ResourceNamespace @namespace,
        string name,
        ExtensionRegistrationProperties definition,
        string? ifMatch,
        CancellationToken cancellationToken)
    {
        var existing = await GetAsync(@namespace, name, cancellationToken)
            ?? throw new ExtensionRegistrationNotFoundException(new(@namespace, ResourceKinds.ExtensionRegistration, name));
        var validated = await ValidateDefinitionAsync(@namespace, name, definition, cancellationToken);
        return await store.PutAsync(
            existing.Value with
            {
                Generation = checked(existing.Value.Generation + 1),
                Definition = validated,
                Status = new ResourceStatus { ProvisioningState = ProvisioningState.Succeeded }
            },
            ifMatch,
            false,
            cancellationToken);
    }

    public async Task DeleteAsync(
        ResourceNamespace @namespace,
        string name,
        string? ifMatch,
        CancellationToken cancellationToken)
    {
        _ = await GetAsync(@namespace, name, cancellationToken)
            ?? throw new ExtensionRegistrationNotFoundException(new(@namespace, ResourceKinds.ExtensionRegistration, name));
        await store.DeleteAsync(new(ResourceKinds.ExtensionRegistration, name, @namespace), ifMatch, cancellationToken);
    }

    public async ValueTask<IReadOnlyList<ExtensionEndpointRegistration>> ListEndpointsAsync(
        CancellationToken cancellationToken = default) =>
        (await ListAsync(cancellationToken))
            .Where(value => value.Value.Definition.Enabled)
            .Select(value => new ExtensionEndpointRegistration(
                value.Value.Name,
                value.Value.Definition.Endpoint,
                "registration"))
            .ToArray();

    private async Task<ExtensionRegistrationProperties> ValidateDefinitionAsync(
        ResourceNamespace @namespace,
        string name,
        ExtensionRegistrationProperties definition,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (string.IsNullOrWhiteSpace(definition.DisplayName))
            throw new ExtensionRegistrationValidationException("A display name is required.");
        if (definition.Endpoint is null || !definition.Endpoint.IsAbsoluteUri || definition.Endpoint.Scheme is not ("http" or "https"))
            throw new ExtensionRegistrationValidationException("Extension endpoint must be an absolute HTTP(S) URL.");
        if (!string.IsNullOrEmpty(definition.Endpoint.UserInfo)
            || !string.IsNullOrEmpty(definition.Endpoint.Query)
            || !string.IsNullOrEmpty(definition.Endpoint.Fragment))
            throw new ExtensionRegistrationValidationException("Extension endpoint cannot contain credentials, a query string, or a fragment.");
        var endpoint = Normalize(definition.Endpoint);
        var duplicate = (await ListAsync(cancellationToken)).FirstOrDefault(value =>
            value.Value.Namespace == @namespace
            && !string.Equals(value.Value.Name, name, StringComparison.Ordinal)
            && Uri.Compare(
                value.Value.Definition.Endpoint,
                endpoint,
                UriComponents.HttpRequestUrl,
                UriFormat.SafeUnescaped,
                StringComparison.OrdinalIgnoreCase) == 0);
        if (duplicate is not null)
            throw new ExtensionRegistrationValidationException(
                $"Endpoint '{endpoint}' is already registered as '{duplicate.Value.Address}'.");
        return definition with
        {
            DisplayName = definition.DisplayName.Trim(),
            Endpoint = endpoint
        };
    }

    private static void ValidateIdentity(ExtensionRegistrationResource resource)
    {
        if (resource.Kind != ResourceKinds.ExtensionRegistration)
            throw new ExtensionRegistrationValidationException($"Kind must be '{ResourceKinds.ExtensionRegistration}'.");
        if (resource.ApiVersion != ManagementApiVersions.CoreV1)
            throw new ExtensionRegistrationValidationException($"ApiVersion must be '{ManagementApiVersions.CoreV1}'.");
        ArgumentException.ThrowIfNullOrWhiteSpace(resource.Metadata.Name);
    }

    private static Uri Normalize(Uri endpoint) =>
        new(endpoint.AbsoluteUri.TrimEnd('/') + '/', UriKind.Absolute);
}
