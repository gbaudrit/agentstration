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

