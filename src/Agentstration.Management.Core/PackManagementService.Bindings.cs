using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Agentstration.Management.Abstractions;
using Agentstration.Resources;

namespace Agentstration.Management.Core;

public sealed partial class PackManagementService
{
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
            PackBindingTargetKind.RuntimeProfile => await store.GetAsync<RuntimeProfileResource>(new(ResourceKinds.RuntimeProfile, target.Name, @namespace), cancellationToken) is not null,
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
}

