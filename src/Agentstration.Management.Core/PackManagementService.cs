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

    public Task<IReadOnlyList<StoredResource<InstalledPackResource>>> ListAsync(CancellationToken cancellationToken) =>
        store.ListAllAsync<InstalledPackResource>(ResourceKinds.InstalledPack, cancellationToken);

    public Task<StoredResource<InstalledPackResource>?> GetAsync(PackIdentity identity, CancellationToken cancellationToken) =>
        store.GetAsync<InstalledPackResource>(new(ResourceKinds.InstalledPack, identity.ResourceName), cancellationToken);

    public async Task<PackInstallationPreview> PreviewAsync(PackArchive archive, CancellationToken cancellationToken)
    {
        var prepared = await PrepareAsync(archive, [], cancellationToken);
        return prepared.Preview;
    }

    public async Task<StoredResource<InstalledPackResource>> InstallAsync(PackArchive archive, CancellationToken cancellationToken)
        => await InstallAsync(archive, [], cancellationToken);

    public async Task<StoredResource<InstalledPackResource>> InstallAsync(
        PackArchive archive,
        IReadOnlyList<PackBindingSelection> bindings,
        CancellationToken cancellationToken)
    {
        var prepared = await PrepareAsync(archive, bindings, cancellationToken);
        var identity = new PackIdentity(archive.Manifest.Metadata.Publisher, archive.Manifest.Metadata.Name);
        var @namespace = identity.Namespace;
        if (prepared.Preview.AlreadyInstalled) throw new PackAlreadyInstalledException(identity);
        var conflict = prepared.Preview.Resources.FirstOrDefault(resource => resource.AlreadyExists);
        if (conflict is not null) throw new PackResourceConflictException(conflict.Kind, conflict.Name);
        var unresolved = prepared.Preview.Bindings.FirstOrDefault(binding => !binding.IsResolved);
        if (unresolved is not null)
            throw new PackValidationException("pack_binding_unresolved", $"Pack binding '{unresolved.Name}' requires an available {unresolved.TargetKind} resource.");
        var resolutions = prepared.Preview.Bindings
            .Where(binding => binding.Target is not null && binding.TargetAvailable)
            .Select(binding => new PackBindingResolution(binding.Name, binding.TargetKind, binding.Target!))
            .ToArray();

        var now = timeProvider.GetUtcNow();
        var sourceArtifact = artifacts is not null && !archive.Content.IsEmpty
            ? await artifacts.SaveAsync(archive.Content, archive.Source, cancellationToken)
            : null;
        var installed = await store.PutAsync(new InstalledPackResource
        {
            ApiVersion = ManagementApiVersions.CoreV1,
            Kind = ResourceKinds.InstalledPack,
            Metadata = new ResourceMetadata { Name = identity.ResourceName },
            Generation = 1,
            Status = new ResourceStatus { ProvisioningState = ProvisioningState.Creating },
            Definition = new InstalledPackProperties
            {
                Publisher = identity.Publisher,
                PackName = identity.Name,
                Namespace = @namespace,
                Version = archive.Manifest.Metadata.Version,
                Audience = archive.Manifest.Metadata.Audience,
                Purpose = archive.Manifest.Metadata.Purpose,
                DisplayName = archive.Manifest.Metadata.DisplayName,
                Description = archive.Manifest.Metadata.Description,
                Source = archive.Source,
                SourceArtifact = sourceArtifact,
                InstalledAt = now,
                State = InstalledPackState.Installing,
                Bindings = resolutions
            }
        }, null, true, cancellationToken);

        var applied = new List<(ManagedPackResource Resource, IPackResourceHandler Handler)>();
        try
        {
            foreach (var pair in prepared.Handlers.OrderBy(value => value.Value.InstallOrder).ThenBy(value => value.Key.Path, StringComparer.Ordinal))
            {
                var managed = await pair.Value.InstallAsync(pair.Key, identity, @namespace, archive.Manifest.Metadata.Version, cancellationToken);
                applied.Add((managed, pair.Value));
                installed = await UpdateAsync(installed, installed.Value.Definition with { ManagedResources = applied.Select(value => value.Resource).ToArray() }, ProvisioningState.Creating, cancellationToken);
            }

            installed = await UpdateAsync(installed, installed.Value.Definition with
            {
                State = InstalledPackState.Installed,
                ErrorCode = null,
                ErrorMessage = null
            }, ProvisioningState.Succeeded, cancellationToken);
            await SaveConfigurationAsync(identity, resolutions, cancellationToken);
            return installed;
        }
        catch (Exception exception)
        {
            var remaining = new List<ManagedPackResource>();
            foreach (var item in applied.AsEnumerable().Reverse())
            {
                try { await item.Handler.DeleteAsync(item.Resource, CancellationToken.None); }
                catch { remaining.Add(item.Resource); }
            }

            _ = await UpdateAsync(installed, installed.Value.Definition with
            {
                State = remaining.Count == 0 ? InstalledPackState.Failed : InstalledPackState.Degraded,
                ManagedResources = remaining,
                ErrorCode = "pack_installation_failed",
                ErrorMessage = exception.Message
            }, ProvisioningState.Failed, CancellationToken.None);
            throw;
        }
    }

    public async Task<StoredResource<InstalledPackResource>> AttachSourceAsync(PackIdentity identity, PackArchive archive, string etag, CancellationToken cancellationToken)
    {
        var installed = await GetAsync(identity, cancellationToken) ?? throw new PackNotFoundException(identity);
        if (installed.Value.Definition.SourceArtifact is not null)
            throw new PackValidationException("pack_source_already_retained", "This installed Pack already has an immutable source archive.");
        if (artifacts is null || archive.Content.IsEmpty)
            throw new PackValidationException("pack_source_retention_unavailable", "Pack source retention is not available.");
        if (!string.Equals(archive.Manifest.Metadata.Publisher, identity.Publisher, StringComparison.Ordinal)
            || !string.Equals(archive.Manifest.Metadata.Name, identity.Name, StringComparison.Ordinal)
            || !string.Equals(archive.Manifest.Metadata.Version, installed.Value.Definition.Version, StringComparison.Ordinal))
            throw new PackValidationException("pack_source_identity_mismatch", "The selected archive must have the same publisher, name, and version as the installed Pack.");

        _ = await PrepareAsync(archive, installed.Value.Definition.Bindings.Select(binding => new PackBindingSelection(binding.Name, binding.Target)).ToArray(), cancellationToken);
        var expected = installed.Value.Definition.ManagedResources
            .Select(resource => (resource.Kind, resource.Name, resource.Path))
            .OrderBy(resource => resource.Kind, StringComparer.Ordinal)
            .ThenBy(resource => resource.Name, StringComparer.Ordinal)
            .ThenBy(resource => resource.Path, StringComparer.Ordinal)
            .ToArray();
        var supplied = archive.Resources
            .Select(resource => (resource.Kind, resource.Name, resource.Path))
            .OrderBy(resource => resource.Kind, StringComparer.Ordinal)
            .ThenBy(resource => resource.Name, StringComparer.Ordinal)
            .ThenBy(resource => resource.Path, StringComparer.Ordinal)
            .ToArray();
        if (!expected.SequenceEqual(supplied))
            throw new PackValidationException("pack_source_resources_mismatch", "The selected archive does not contain the same resource inventory as the installed Pack.");

        var sourceArtifact = await artifacts.SaveAsync(archive.Content, archive.Source, cancellationToken);
        return await store.PutAsync(installed.Value with
        {
            Generation = checked(installed.Value.Generation + 1),
            Definition = installed.Value.Definition with { Source = archive.Source, SourceArtifact = sourceArtifact },
            Status = new ResourceStatus { ProvisioningState = ProvisioningState.Succeeded }
        }, etag, false, cancellationToken);
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
        var selectedHandlers = new Dictionary<PackResourceDocument, IPackResourceHandler>();
        var resources = new List<PackResourcePreview>();
        foreach (var resource in resolvedResources)
        {
            if (resource.ApiVersion != ManagementApiVersions.CoreV1)
                throw new PackValidationException("pack_resource_api_version_unsupported", $"Resource '{resource.Path}' uses unsupported apiVersion '{resource.ApiVersion}'.");
            if (!handlers.TryGetValue(resource.Kind, out var handler))
                throw new PackValidationException("pack_resource_kind_unsupported", $"Resource kind '{resource.Kind}' is not supported by this installation.");
            await handler.ValidateAsync(resource, archive.Resources, cancellationToken);
            resources.Add(new(resource.Path, resource.Kind, resource.Name, await handler.ExistsAsync(@namespace, resource.Name, cancellationToken)));
            selectedHandlers.Add(resource, handler);
        }

        var preview = new PackInstallationPreview(
            archive.Manifest.Metadata,
            resources,
            await GetAsync(identity, cancellationToken) is not null)
        {
            Namespace = @namespace,
            Bindings = bindingPreviews
        };
        return (preview, selectedHandlers);
    }

    private async Task<IReadOnlyList<PackBindingPreview>> PrepareBindingsAsync(
        PackArchive archive,
        PackIdentity identity,
        IReadOnlyList<PackBindingSelection> requestedBindings,
        CancellationToken cancellationToken)
    {
        var duplicates = requestedBindings.GroupBy(binding => binding.Name, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1);
        if (duplicates is not null)
            throw new PackValidationException("pack_binding_selection_duplicate", $"Pack binding '{duplicates.Key}' was selected more than once.");
        var declaredNames = archive.Manifest.Definition.Bindings.Select(binding => binding.Name).ToHashSet(StringComparer.Ordinal);
        var unknownSelection = requestedBindings.FirstOrDefault(binding => !declaredNames.Contains(binding.Name));
        if (unknownSelection is not null)
            throw new PackValidationException("pack_binding_selection_unknown", $"Pack binding '{unknownSelection.Name}' is not declared by this Pack.");

        var stored = await GetConfigurationAsync(identity, cancellationToken);
        var targets = stored?.Value.Definition.Bindings.ToDictionary(binding => binding.Name, binding => binding.Target, StringComparer.Ordinal)
            ?? new Dictionary<string, ResourceReference>(StringComparer.Ordinal);
        foreach (var selection in requestedBindings)
        {
            if (!string.IsNullOrWhiteSpace(selection.Target.WorkspaceRef))
                throw new PackValidationException("pack_binding_cross_workspace_unsupported", $"Pack binding '{selection.Name}' cannot target another workspace.");
            targets[selection.Name] = Normalize(selection.Target);
        }

        var result = new List<PackBindingPreview>(archive.Manifest.Definition.Bindings.Count);
        foreach (var requirement in archive.Manifest.Definition.Bindings)
        {
            var uses = archive.Resources
                .Where(resource => BindingNames(resource.Manifest).Contains(requirement.Name, StringComparer.Ordinal))
                .Select(resource => new PackBindingUsage(resource.Kind, resource.Name, resource.Path))
                .ToArray();
            targets.TryGetValue(requirement.Name, out var target);
            var available = target is not null && await BindingTargetExistsAsync(requirement.TargetKind, target, cancellationToken);
            result.Add(new(
                requirement.Name,
                requirement.TargetKind,
                requirement.DisplayName ?? requirement.Name,
                requirement.Description,
                requirement.Required,
                uses,
                target,
                available));
        }
        return result;
    }

    private async Task<bool> BindingTargetExistsAsync(PackBindingTargetKind kind, ResourceReference target, CancellationToken cancellationToken)
    {
        var @namespace = target.Namespace ?? ResourceNamespace.Default;
        return kind switch
        {
            PackBindingTargetKind.ModelProfile => await store.GetAsync<ModelProfileResource>(new(ResourceKinds.ModelProfile, target.Name, @namespace), cancellationToken) is not null,
            PackBindingTargetKind.ModelProvider => await store.GetAsync<ModelProviderResource>(new(ResourceKinds.ModelProvider, target.Name, @namespace), cancellationToken) is not null,
            PackBindingTargetKind.ExtensionRegistration => await store.GetAsync<ExtensionRegistrationResource>(new(ResourceKinds.ExtensionRegistration, target.Name, @namespace), cancellationToken) is not null,
            PackBindingTargetKind.Secret => await store.GetAsync<SecretResource>(new(ResourceKinds.Secret, target.Name, @namespace), cancellationToken) is not null,
            _ => false
        };
    }

    private Task<StoredResource<PackConfigurationResource>?> GetConfigurationAsync(PackIdentity identity, CancellationToken cancellationToken) =>
        store.GetAsync<PackConfigurationResource>(new(ResourceKinds.PackConfiguration, identity.ResourceName), cancellationToken);

    private async Task SaveConfigurationAsync(PackIdentity identity, IReadOnlyList<PackBindingResolution> bindings, CancellationToken cancellationToken)
    {
        var current = await GetConfigurationAsync(identity, cancellationToken);
        var resource = new PackConfigurationResource
        {
            ApiVersion = ManagementApiVersions.CoreV1,
            Kind = ResourceKinds.PackConfiguration,
            Metadata = new ResourceMetadata { Name = identity.ResourceName },
            Generation = current is null ? 1 : checked(current.Value.Generation + 1),
            Status = new ResourceStatus { ProvisioningState = ProvisioningState.Succeeded },
            Definition = new PackConfigurationProperties
            {
                Publisher = identity.Publisher,
                PackName = identity.Name,
                Bindings = bindings,
                UpdatedAt = timeProvider.GetUtcNow()
            }
        };
        _ = await store.PutAsync(resource, current?.ETag, current is null, cancellationToken);
    }

    private static ResourceReference Normalize(ResourceReference target)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target.Name);
        return new(target.Name.Trim(), @namespace: target.Namespace ?? ResourceNamespace.Default);
    }

    private static PackResourceDocument ResolveBindings(PackResourceDocument resource, IReadOnlyDictionary<string, ResourceReference?> targets)
    {
        var node = JsonNode.Parse(resource.Manifest.GetRawText())
            ?? throw new PackValidationException("pack_resource_invalid", $"Resource '{resource.Path}' is empty.");
        var resolved = ResolveNode(node, targets);
        return resource with { Manifest = JsonSerializer.SerializeToElement(resolved) };
    }

    private static JsonNode? ResolveNode(JsonNode? node, IReadOnlyDictionary<string, ResourceReference?> targets)
    {
        if (node is JsonObject bindingObject
            && bindingObject.Count == 1
            && bindingObject["binding"] is JsonValue bindingValue
            && bindingValue.TryGetValue<string>(out var bindingName))
        {
            if (!targets.TryGetValue(bindingName, out var target))
                throw new PackValidationException("pack_binding_reference_unknown", $"Resource references undeclared Pack binding '{bindingName}'.");
            if (target is null) return null;
            return new JsonObject
            {
                ["name"] = target.Name,
                ["namespace"] = (target.Namespace ?? ResourceNamespace.Default).Value
            };
        }
        if (node is JsonObject objectNode)
        {
            foreach (var property in objectNode.ToArray())
            {
                var resolved = ResolveNode(property.Value, targets);
                if (!ReferenceEquals(resolved, property.Value)) objectNode[property.Key] = resolved;
            }
        }
        else if (node is JsonArray arrayNode)
        {
            for (var index = 0; index < arrayNode.Count; index++)
            {
                var resolved = ResolveNode(arrayNode[index], targets);
                if (!ReferenceEquals(resolved, arrayNode[index])) arrayNode[index] = resolved;
            }
        }
        return node;
    }

    private static IReadOnlyList<string> BindingNames(JsonElement manifest)
    {
        var names = new List<string>();
        Visit(manifest, names);
        return names;
    }

    private static void Visit(JsonElement element, List<string> names)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var properties = element.EnumerateObject().ToArray();
            if (properties.Length == 1
                && properties[0].NameEquals("binding")
                && properties[0].Value.ValueKind == JsonValueKind.String)
            {
                names.Add(properties[0].Value.GetString()!);
                return;
            }
            foreach (var property in properties) Visit(property.Value, names);
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray()) Visit(item, names);
        }
    }

    public async Task UninstallAsync(PackIdentity identity, CancellationToken cancellationToken)
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
                    await handler.DeleteAsync(resource, cancellationToken);
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

    private async Task<StoredResource<InstalledPackResource>> UpdateAsync(
        StoredResource<InstalledPackResource> current,
        InstalledPackProperties definition,
        ProvisioningState state,
        CancellationToken cancellationToken) =>
        await store.PutAsync(current.Value with
        {
            Generation = checked(current.Value.Generation + 1),
            Definition = definition,
            Status = new ResourceStatus { ProvisioningState = state }
        }, current.ETag, false, cancellationToken);

    private void ValidateManifest(PackArchive archive)
    {
        var manifest = archive.Manifest;
        if (manifest.ApiVersion != ManagementApiVersions.CoreV1)
            throw new PackValidationException("pack_api_version_unsupported", $"Supported Pack apiVersion is '{ManagementApiVersions.CoreV1}'.");
        if (manifest.Kind != PackKinds.Pack) throw new PackValidationException("pack_kind_invalid", $"Pack manifest kind must be '{PackKinds.Pack}'.");
        ValidateName(manifest.Metadata.Publisher, "publisher");
        ValidateName(manifest.Metadata.Name, "name");
        if (!Enum.IsDefined(manifest.Metadata.Audience))
            throw new PackValidationException("pack_audience_invalid", "Pack audience must be universal, personal, or professional.");
        if (!Enum.IsDefined(manifest.Metadata.Purpose))
            throw new PackValidationException("pack_purpose_invalid", "Pack purpose must be sample, template, or standard.");
        if (!SemanticVersionRegex().IsMatch(manifest.Metadata.Version))
            throw new PackValidationException("pack_version_invalid", "Pack version must use Semantic Versioning.");
        if (manifest.Definition.Requirements.Count > 0)
            throw new PackValidationException("pack_requirements_unsupported", "Pack requirements are declared but dependency resolution is not available in V1.");
        var duplicateBinding = manifest.Definition.Bindings.GroupBy(binding => binding.Name, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1);
        if (duplicateBinding is not null)
            throw new PackValidationException("pack_binding_duplicate", $"Pack binding '{duplicateBinding.Key}' is declared more than once.");
        foreach (var binding in manifest.Definition.Bindings)
        {
            ValidateBindingName(binding.Name);
            if (!Enum.IsDefined(binding.TargetKind))
                throw new PackValidationException("pack_binding_kind_invalid", $"Pack binding '{binding.Name}' has an unsupported target kind.");
        }
        var referencedBindings = archive.Resources.SelectMany(resource => BindingNames(resource.Manifest)).ToArray();
        var undeclaredBinding = referencedBindings.FirstOrDefault(name => !manifest.Definition.Bindings.Any(binding => string.Equals(binding.Name, name, StringComparison.Ordinal)));
        if (undeclaredBinding is not null)
            throw new PackValidationException("pack_binding_reference_unknown", $"Resource references undeclared Pack binding '{undeclaredBinding}'.");
        var unusedBinding = manifest.Definition.Bindings.FirstOrDefault(binding => !referencedBindings.Contains(binding.Name, StringComparer.Ordinal));
        if (unusedBinding is not null)
            throw new PackValidationException("pack_binding_unused", $"Pack binding '{unusedBinding.Name}' is not referenced by a contained resource.");
        if (manifest.Definition.Resources.Count != archive.Resources.Count)
            throw new PackValidationException("pack_resource_count_mismatch", "The Pack resource list does not match the validated archive content.");
        if (!manifest.Definition.Resources.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(archive.Resources.Select(value => value.Path)))
            throw new PackValidationException("pack_resource_path_mismatch", "The Pack resource paths do not match the validated archive content.");
        var duplicates = archive.Resources.GroupBy(value => (value.Kind, value.Name)).FirstOrDefault(value => value.Count() > 1);
        if (duplicates is not null)
            throw new PackValidationException("pack_resource_duplicate", $"Resource '{duplicates.Key.Kind}/{duplicates.Key.Name}' is declared more than once.");
    }

    private static void ValidateName(string value, string field)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 60 || !char.IsAsciiLetterOrDigit(value[0]) || value.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-') || value.Any(char.IsUpper))
            throw new PackValidationException($"pack_{field}_invalid", $"Pack {field} must contain 1 to 60 lowercase ASCII letters, digits or '-' and start with a letter or digit.");
    }

    private static void ValidateBindingName(string value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > 128
            || !char.IsAsciiLetterOrDigit(value[0])
            || value.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '.'))
            throw new PackValidationException("pack_binding_name_invalid", "Pack binding names must contain 1 to 128 ASCII letters, digits, '-' or '.' and start with a letter or digit.");
    }

    [GeneratedRegex(@"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$", RegexOptions.CultureInvariant)]
    private static partial Regex SemanticVersionRegex();
}
