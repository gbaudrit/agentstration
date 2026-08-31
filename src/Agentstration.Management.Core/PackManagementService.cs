using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Agentstration.Management.Abstractions;
using Agentstration.Resources;

namespace Agentstration.Management.Core;

public sealed partial class PackValidationException(string code, string message) : ArgumentException(message)
{
    public string Code { get; } = code;
}
public sealed class PackNotFoundException(PackIdentity identity) : KeyNotFoundException($"Pack '{identity}' is not installed.");
public sealed class PackAlreadyInstalledException(PackIdentity identity) : InvalidOperationException($"Pack '{identity}' is already registered.");
public sealed class PackResourceConflictException(string kind, string name) : InvalidOperationException($"Resource '{kind}/{name}' already exists and cannot be replaced by Pack V1.");
public sealed class PackResourceModifiedException(string kind, string name) : InvalidOperationException($"Resource '{kind}/{name}' changed after installation and was preserved.");

public sealed partial class PackManagementService
{
    private readonly IControlPlaneStore store;
    private readonly TimeProvider timeProvider;
    private readonly IPackArtifactStore? artifacts;
    private readonly IReadOnlyDictionary<string, IPackResourceHandler> handlers;

    public PackManagementService(IControlPlaneStore store, IEnumerable<IPackResourceHandler> resourceHandlers, TimeProvider timeProvider)
        : this(store, resourceHandlers, timeProvider, null) { }

    public PackManagementService(IControlPlaneStore store, IEnumerable<IPackResourceHandler> resourceHandlers, TimeProvider timeProvider, IPackArtifactStore? artifacts)
    {
        this.store = store;
        this.timeProvider = timeProvider;
        this.artifacts = artifacts;
        handlers = resourceHandlers.ToDictionary(value => value.Kind, StringComparer.Ordinal);
    }

















    private async Task<(PackInstallationPreview Preview, IReadOnlyDictionary<PackResourceDocument, IPackResourceHandler> Handlers)> PrepareAsync(
        PackArchive archive,
        IReadOnlyList<PackBindingSelection> requestedBindings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(archive);
        ValidateManifest(archive);
        var identity = new PackIdentity(archive.Manifest.Metadata.Publisher, archive.Manifest.Metadata.Name);
        var @namespace = identity.Namespace;
        var bindingPreviews = await PrepareBindingsAsync(archive, identity, requestedBindings, cancellationToken);
        var validationTargets = bindingPreviews.ToDictionary(
            binding => binding.Name,
            binding => binding.TargetAvailable && binding.Target is not null
                ? binding.Target
                : binding.Required
                    ? new ResourceReference($"binding-{binding.Name}", @namespace: ResourceNamespace.Default)
                    : null,
            StringComparer.Ordinal);
        var resolvedResources = archive.Resources
            .Select(resource => ResolveBindings(resource, validationTargets))
            .ToArray();
        var existingInstallation = await GetAsync(identity, cancellationToken);
        var managedKeys = existingInstallation?.Value.Definition.ManagedResources
            .Select(value => (value.Kind, value.Name)).ToHashSet() ?? [];
        var selectedHandlers = new Dictionary<PackResourceDocument, IPackResourceHandler>();
        var resources = new List<PackResourcePreview>();
        foreach (var resource in resolvedResources)
        {
            if (resource.ApiVersion != ManagementApiVersions.CoreV1)
                throw new PackValidationException("pack_resource_api_version_unsupported", $"Resource '{resource.Path}' uses unsupported apiVersion '{resource.ApiVersion}'.");
            if (!handlers.TryGetValue(resource.Kind, out var handler))
                throw new PackValidationException("pack_resource_kind_unsupported", $"Resource kind '{resource.Kind}' is not supported by this installation.");
            await handler.ValidateAsync(resource, archive.Resources, cancellationToken);
            var exists = await handler.ExistsAsync(@namespace, resource.Name, cancellationToken);
            var managed = managedKeys.Contains((resource.Kind, resource.Name));
            resources.Add(new(resource.Path, resource.Kind, resource.Name, exists, managed ? PackResourceChange.Update : exists ? PackResourceChange.Conflict : PackResourceChange.Add));
            selectedHandlers.Add(resource, handler);
        }
        if (existingInstallation is not null)
        {
            var incoming = resolvedResources.Select(value => (value.Kind, value.Name)).ToHashSet();
            resources.AddRange(existingInstallation.Value.Definition.ManagedResources
                .Where(value => !incoming.Contains((value.Kind, value.Name)))
                .Select(value => new PackResourcePreview(value.Path, value.Kind, value.Name, true, PackResourceChange.Remove)));
        }

        var preview = new PackInstallationPreview(
            archive.Manifest.Metadata,
            resources,
            existingInstallation is not null)
        {
            Namespace = @namespace,
            Bindings = bindingPreviews
        };
        return (preview, selectedHandlers);
    }

































}
