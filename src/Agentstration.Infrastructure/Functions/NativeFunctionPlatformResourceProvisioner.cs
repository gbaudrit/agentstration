using System.Text.Json;
using Agentstration.Functions;
using Agentstration.Functions.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;

namespace Agentstration.Infrastructure.Functions;

/// <summary>Reconciles the product's compiled standard catalogue through ordinary Core governance.</summary>
public sealed class NativeFunctionPlatformResourceProvisioner(
    TransformationFunctionService functions,
    NativeFunctionRegistry registry,
    IResourceAccessPolicyService access,
    IResourceGovernanceContext governance)
{
    public const string Component = "functions.native";
    public const string KindName = "native";
    public const string KindVersion = "1.0.0";
    private static readonly string[] StandardImplementations =
        ["json.select", "json.project", "collection.filter", "collection.distinct", "artifact.explode"];
    private static readonly IReadOnlyDictionary<string, string> Descriptions = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["json.select"] = "Select a JSON value at a JSON Pointer path. Returns the selected object, array or scalar as one value. An empty path selects the root; a missing path fails.",
        ["json.project"] = "Build an object by selecting and renaming properties from a JSON value. Each name/path pair creates one output property. Selected arrays remain arrays inside that object.",
        ["collection.filter"] = "Keep array elements whose value at the JSON Pointer path equals the supplied JSON value. Preserves input order. An empty path compares whole elements; a missing path fails.",
        ["collection.distinct"] = "Remove duplicate array elements using the value at the JSON Pointer path as their key. Keeps the first occurrence and preserves input order. An empty path compares whole elements.",
        ["artifact.explode"] = "Read JSON Artifact contents and prepare one publishable Artifact per array element or object value at a JSON Pointer path. Expects an Artifact manifest or an object with a content property. arrayItems preserves array order; objectValues sorts property names. Storage happens later in the publication pipeline. itemSchema validates each child; maximumOutputs defaults to 100."
    };
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task<IReadOnlyList<Resource>> EnsureAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                try { return await ReconcileAsync(ct); }
                catch (ResourceConcurrencyException) when (attempt < 2) { }
            }
        }
        finally { gate.Release(); }
    }

    private async Task<IReadOnlyList<Resource>> ReconcileAsync(CancellationToken ct)
    {
        using var authority = governance.Push(ResourceGovernance.Core(Component));
        var scope = ResourceScopeRef.Instance;
        var kindVersion = new FunctionKindVersion(KindVersion, "native", NativeFunctionKindContract.DefinitionSchema);
        var kind = await functions.GetKindAsync(scope, ResourceNamespace.Default, KindName, ct);
        if (kind is not null) EnsureOwned(kind.Value);
        if (kind is null || kind.Value.Definition.Versions.All(version => version.Version != KindVersion))
        {
            var resource = kind?.Value ?? new FunctionKindResource
            {
                ApiVersion = ResourceApiVersions.CoreV1, Kind = FunctionResourceKinds.FunctionKind,
                ScopeRef = scope, Metadata = Metadata(KindName), Definition = new("Native", [])
            };
            kind = await functions.PutKindAsync(resource with
            { Definition = resource.Definition with { Versions = [.. resource.Definition.Versions, kindVersion] } }, kind?.ETag, kind is null, ct);
        }
        if (FunctionDigests.Compute(kind.Value.Definition.Versions.Single(version => version.Version == KindVersion)) != FunctionDigests.Compute(kindVersion))
            throw new InvalidOperationException("The built-in native FunctionKind contract has changed without a new version.");

        var result = new List<Resource> { kind.Value };
        var kindReference = new FunctionReference(KindName, KindVersion, scope, ResourceNamespace.Default,
            kind.Value.Uid, FunctionDigests.Compute(kindVersion));
        foreach (var id in StandardImplementations)
        {
            var descriptors = registry.List().Where(descriptor => descriptor.Id == id).ToArray();
            if (descriptors.Length == 0) throw new InvalidOperationException($"The standard native implementation '{id}' is unavailable.");
            var name = id.Replace('.', '-') + "-builtin";
            var stored = await functions.GetAsync(scope, ResourceNamespace.Default, name, ct);
            if (stored is not null) EnsureOwned(stored.Value);
            var desired = descriptors.Select(descriptor => new TransformationFunctionVersion(descriptor.Version, kindReference,
                JsonSerializer.SerializeToElement(new { implementation = new { id, version = descriptor.Version } }))
                { NativeContractDigest = FunctionDigests.Compute(descriptor) }).ToArray();
            var resource = stored?.Value ?? new TransformationFunctionResource
            {
                ApiVersion = ResourceApiVersions.CoreV1, Kind = FunctionResourceKinds.TransformationFunction,
                ScopeRef = scope, Metadata = Metadata(name), Definition = new(id, [])
            };
            foreach (var version in desired)
                if (resource.Definition.Versions.SingleOrDefault(existing => existing.Version == version.Version) is { } existing
                    && FunctionDigests.Compute(existing) != FunctionDigests.Compute(version))
                    throw new InvalidOperationException($"The built-in function '{name}:{version.Version}' changed without a new version.");
            var additions = desired.Where(version => resource.Definition.Versions.All(existing => existing.Version != version.Version)).ToArray();
            if (stored is null || additions.Length != 0 || resource.Definition.Description != Descriptions[id])
                stored = await functions.PutAsync(resource with
                { Definition = resource.Definition with { Versions = [.. resource.Definition.Versions, .. additions], Description = Descriptions[id] } }, stored?.ETag, stored is null, ct);
            result.Add(stored!.Value);
        }
        return result;
    }

    public async Task EnsureTenantAccessAsync(ResourceScopeRef tenant, CancellationToken ct)
    {
        if (tenant.Kind != ResourceScopeKind.Tenant) throw new ArgumentException("Built-in function access requires a Tenant scope.", nameof(tenant));
        foreach (var resource in await EnsureAsync(ct))
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var current = await access.GetAsync(resource.Uid, ct);
                var grants = current?.Policy.Grants.ToList() ?? [];
                var index = grants.FindIndex(grant => grant.TargetScopeRef == tenant);
                var desired = new ResourceAccessGrant(tenant, [ResourceAccessAction.Discover, ResourceAccessAction.Use], true);
                if (index >= 0 && grants[index].IncludeDescendants && desired.Actions.All(grants[index].Actions.Contains)) break;
                if (index >= 0) grants[index] = desired;
                else grants.Add(desired);
                try
                {
                    _ = await access.PutAsync(new(new(resource.Uid, ResourceScopeRef.Instance, resource.Namespace, resource.Kind), grants), current?.ETag, ct);
                    break;
                }
                catch (ResourceConcurrencyException) when (attempt < 2) { }
            }
        }
    }

    private static ResourceMetadata Metadata(string name) => new()
    { Name = name, Annotations = new Dictionary<string, string> { [ResourceProvenanceAnnotations.BuiltIn] = "true" } };

    private static void EnsureOwned(Resource resource)
    {
        if (resource.ScopeRef != ResourceScopeRef.Instance || resource.Namespace != ResourceNamespace.Default
            || resource.Governance.Origin.Kind != ResourceOriginKind.BuiltIn
            || resource.Governance.ManagedBy is not { Kind: ResourceManagementAuthorityKind.Core, Reference: Component })
            throw new InvalidOperationException($"Reserved built-in {resource.Kind} identity '{resource.Name}' is already in use.");
    }
}
