using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Agentstration.Management.Abstractions;
using Agentstration.Resources;

namespace Agentstration.Management.Core;

public sealed partial class PackManagementService
{
    public Task UninstallAsync(PackIdentity identity, CancellationToken cancellationToken) =>
        UninstallAsync(identity, new PackRemovalOptions(), cancellationToken);

    public async Task UninstallAsync(PackIdentity identity, PackRemovalOptions removalOptions, CancellationToken cancellationToken)
    {
        var installed = await GetAsync(identity, cancellationToken) ?? throw new PackNotFoundException(identity);
        if (scopeOperations is not null && installed.Value.ScopeRef is { } scopeRef)
        {
            _ = await scopeOperations.WriteAsync(
                installed.Value,
                scopeRef,
                AuthorizationPermissions.ResourcesDelete,
                async token =>
                {
                    await UninstallCoreAsync(identity, installed, removalOptions, token);
                    return true;
                },
                cancellationToken);
            return;
        }
        await UninstallCoreAsync(identity, installed, removalOptions, cancellationToken);
    }

    private async Task UninstallCoreAsync(
        PackIdentity identity,
        StoredResource<InstalledPackResource> installed,
        PackRemovalOptions removalOptions,
        CancellationToken cancellationToken)
    {
        installed = await UpdateAsync(installed, installed.Value.Definition with { State = InstalledPackState.Uninstalling }, ProvisioningState.Deleting, cancellationToken);
        var remaining = installed.Value.Definition.ManagedResources.ToList();
        try
        {
            foreach (var resource in installed.Value.Definition.ManagedResources.Reverse())
            {
                if (!handlers.TryGetValue(resource.Kind, out var handler))
                    throw new PackValidationException("pack_resource_kind_unsupported", $"Resource kind '{resource.Kind}' has no installed handler.");
                var currentToken = await handler.GetVersionTokenAsync(resource.Namespace, resource.Name, cancellationToken);
                if (currentToken is not null)
                {
                    if (!string.Equals(currentToken, resource.VersionToken, StringComparison.Ordinal))
                        throw new PackResourceModifiedException(resource.Kind, resource.Name);
                    await handler.DeleteAsync(resource, removalOptions, cancellationToken);
                }
                remaining.Remove(resource);
                installed = await UpdateAsync(installed, installed.Value.Definition with { ManagedResources = remaining.ToArray() }, ProvisioningState.Deleting, cancellationToken);
            }

            if (installed.Value.ScopeRef is { } scopeRef)
                await store.DeleteExactAsync(ScopedResourceAddress.Create(scopeRef, installed.Value.Namespace, ResourceKinds.InstalledPack, identity.ResourceName), installed.ETag, cancellationToken);
            else
                await store.DeleteAsync(new(ResourceKinds.InstalledPack, identity.ResourceName), installed.ETag, cancellationToken);
        }
        catch (Exception exception)
        {
            _ = await UpdateAsync(installed, installed.Value.Definition with
            {
                State = InstalledPackState.Degraded,
                ManagedResources = remaining.ToArray(),
                ErrorCode = "pack_uninstallation_failed",
                ErrorMessage = exception.Message
            }, ProvisioningState.Failed, CancellationToken.None);
            throw;
        }
    }
}

