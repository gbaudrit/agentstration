using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;

namespace Agentstration.Web.Hosting;

internal sealed record BootstrapExtensionRequirement(
    string Profile, string Location, string ResourceKind, string ResourceName,
    ResourceScopeRef ConsumerScope, BootstrapContributionSelector Selector);

internal sealed class BootstrapExtensionReferencePreparation(IBootstrapExtensionReferenceResolver? resolver)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    internal static string BindingKind(BootstrapBindingTargetKind kind) => kind switch
    {
        BootstrapBindingTargetKind.ExtensionRegistration => "ExtensionRegistration",
        BootstrapBindingTargetKind.Tool => "Tool",
        BootstrapBindingTargetKind.ConnectedServiceType => "ConnectedServiceType",
        BootstrapBindingTargetKind.StorageType => "StorageType",
        BootstrapBindingTargetKind.DataSourceProfile => "DataSourceProfile",
        _ => throw new BootstrapDependencyException("contribution_kind_unsupported", $"Binding kind '{kind}' cannot select an extension contribution.")
    };

    internal static void Validate(BootstrapContributionSelector selector)
    {
        if (string.IsNullOrWhiteSpace(selector.ExtensionId) || selector.ExtensionId.Length > 256
            || selector.InstanceId == Guid.Empty || string.IsNullOrWhiteSpace(selector.Kind)
            || (selector.Kind != "ExtensionRegistration" && string.IsNullOrWhiteSpace(selector.ContributionId))
            || selector.ContributionId.Length > 256 || selector.Version?.Length > 128)
            throw new BootstrapDependencyException("contribution_selector_invalid", "An extension contribution selector requires bounded public extension, kind and contribution identities.");
    }

    internal static ResourceScopeRef ConsumerScope(BootstrapResourceOperationContext operation) => operation.ProfileScope switch
    {
        BootstrapProfileScope.Instance => ResourceScopeRef.Instance,
        BootstrapProfileScope.Tenant => ResourceScopeRef.Tenant(operation.Target!.TenantId!.Value),
        BootstrapProfileScope.Workspace => ResourceScopeRef.Workspace(operation.Target!.WorkspaceId!.Value),
        _ => throw new InvalidOperationException("Unsupported Bootstrap scope.")
    };

    internal async Task<BootstrapResolvedReference> ResolveAsync(
        BootstrapContributionSelector selector, ResourceScopeRef scope, CancellationToken cancellationToken)
    {
        Validate(selector);
        var service = resolver ?? throw new BootstrapDependencyException("extension_resolution_unconfigured", "Bootstrap extension resolution is not configured.");
        return selector.Kind == "ExtensionRegistration"
            ? await service.ResolveExtensionAsync(new() { Id = selector.ExtensionId, InstanceId = selector.InstanceId }, scope, cancellationToken)
            : await service.ResolveContributionAsync(selector, scope, cancellationToken);
    }

    internal async Task<BootstrapResourceDocument> ResolveDocumentAsync(
        BootstrapResourceDocument resource, IReadOnlyList<string> extensionPaths,
        ResourceScopeRef scope, List<string> fingerprints, CancellationToken cancellationToken)
    {
        var root = JsonNode.Parse(resource.Definition.GetRawText())!;
        foreach (var path in extensionPaths)
        {
            if (Property(root, path) is not { } property || property.Value is not JsonObject value
                || !value.ContainsKey("id")) continue;
            var selector = ReadExtension(value);
            var resolved = await ResolveAsync(selector, scope, cancellationToken);
            fingerprints.Add(resolved.Fingerprint);
            property.Parent[property.Name] = ReferenceNode(resolved);
        }
        root = await ResolveNodeAsync(root, scope, fingerprints, cancellationToken);
        return resource with { Definition = JsonSerializer.SerializeToElement(root, JsonOptions) };
    }

    private async Task<JsonNode> ResolveNodeAsync(JsonNode node, ResourceScopeRef scope,
        List<string> fingerprints, CancellationToken cancellationToken)
    {
        if (node is JsonObject value)
        {
            if (value.ContainsKey("extensionContribution"))
            {
                if (value.Count != 1 || value["extensionContribution"] is not JsonObject target)
                    throw new BootstrapDependencyException("contribution_selector_invalid", "An extensionContribution placeholder must contain only its structured selector.");
                var selected = target.Deserialize<BootstrapContributionSelector>(JsonOptions)!;
                var resolved = await ResolveAsync(selected, scope, cancellationToken);
                fingerprints.Add(resolved.Fingerprint);
                return ReferenceNode(resolved);
            }
            foreach (var item in value.ToArray())
            {
                if (item.Value is null) continue;
                var resolved = await ResolveNodeAsync(item.Value, scope, fingerprints, cancellationToken);
                if (!ReferenceEquals(resolved, item.Value)) value[item.Key] = resolved;
            }
        }
        else if (node is JsonArray array)
        {
            for (var index = 0; index < array.Count; index++)
            {
                if (array[index] is not { } item) continue;
                var resolved = await ResolveNodeAsync(item, scope, fingerprints, cancellationToken);
                if (!ReferenceEquals(resolved, item)) array[index] = resolved;
            }
        }
        return node;
    }

    internal static IReadOnlySet<string> UsedBindings(LoadedBootstrapProfile profile) => profile.Resources
        .SelectMany(source => Nodes(JsonNode.Parse(source.Resource.Definition.GetRawText())!))
        .OfType<JsonObject>().Where(value => value.Count == 1 && value["binding"] is JsonValue)
        .Select(value => value["binding"]!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);

    internal static IReadOnlyList<BootstrapExtensionRequirement> Requirements(
        LoadedBootstrapProfile profile, BootstrapResourceOperationContext operation,
        IReadOnlyDictionary<string, IBootstrapResourceHandler> handlers,
        IReadOnlyList<BootstrapBindingSelection> explicitBindings)
    {
        var requirements = new List<BootstrapExtensionRequirement>();
        var scope = ConsumerScope(operation);
        foreach (var source in profile.Resources)
        {
            var root = JsonNode.Parse(source.Resource.Definition.GetRawText())!;
            var selectors = new List<BootstrapContributionSelector>();
            if (handlers.TryGetValue(source.Resource.Kind, out var handler))
                foreach (var path in handler.ExtensionReferencePaths)
                    if (Property(root, path)?.Value is JsonObject value && value.ContainsKey("id"))
                        selectors.Add(ReadExtension(value));
            foreach (var value in Nodes(root).OfType<JsonObject>())
            {
                if (value["extensionContribution"] is JsonObject contribution)
                    selectors.Add(contribution.Deserialize<BootstrapContributionSelector>(JsonOptions)!);
                if (value.Count == 1 && value["binding"] is JsonValue binding)
                {
                    var name = binding.GetValue<string>();
                    if (explicitBindings.Any(item => item.Profile == profile.Summary.Name && item.Name == name)) continue;
                    var target = profile.Summary.Bindings.SingleOrDefault(item => item.Name == name)?.ExtensionTarget;
                    if (target is not null) selectors.Add(target);
                }
            }
            foreach (var selector in selectors)
            {
                Validate(selector);
                requirements.Add(new(profile.Summary.Name, source.Location, source.Resource.Kind,
                    source.Resource.Metadata.Name, scope, selector));
            }
        }
        return requirements;
    }

    private static BootstrapContributionSelector ReadExtension(JsonObject value)
    {
        var selector = value.Deserialize<BootstrapExtensionSelector>(JsonOptions)!;
        var result = new BootstrapContributionSelector
        {
            ExtensionId = selector.Id, InstanceId = selector.InstanceId,
            Kind = "ExtensionRegistration", ContributionId = string.Empty
        };
        Validate(result);
        return result;
    }

    private static JsonNode ReferenceNode(BootstrapResolvedReference resolved) => resolved.TargetValue is { } value
        ? JsonNode.Parse(value.GetRawText())!
        : JsonSerializer.SerializeToNode(resolved.Reference, JsonOptions)!;

    private static IEnumerable<JsonNode> Nodes(JsonNode root)
    {
        yield return root;
        var children = root switch
        {
            JsonObject value => value.Select(item => item.Value),
            JsonArray array => array.AsEnumerable(),
            _ => []
        };
        foreach (var child in children)
            if (child is not null)
                foreach (var descendant in Nodes(child)) yield return descendant;
    }

    private sealed record JsonProperty(JsonObject Parent, string Name, JsonNode? Value);
    private static JsonProperty? Property(JsonNode root, string path)
    {
        var segments = path.Split('.');
        JsonNode? parent = root;
        foreach (var segment in segments[..^1]) parent = parent?[segment];
        return parent is JsonObject value ? new(value, segments[^1], value[segments[^1]]) : null;
    }
}
