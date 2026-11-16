using Agentstration.Resources;
using Agentstration.SystemGraph.Contracts;

namespace Agentstration.ResourceManagement;

/// <summary>Builds metadata edges from family-extracted UID pins. Does not resolve historical names.</summary>
public sealed class SystemGraphContributionBuilder(SystemGraphNode source, IResourceIdentityCatalog identities)
{
    private readonly List<SystemGraphNode> nodes = [source];
    private readonly List<SystemGraphRelation> relations = [];

    public SystemGraphContribution Build() => new(nodes, relations) { BindingOwner = source.Identity, BindingOwnerNodeId = source.Id };

    public void PublicationOf(Guid ownerUid) => relations.Add(new(SystemGraphIds.Definition(ownerUid), source.Id, "publication"));

    public async Task ValueReferenceAsync(ScopedResourceAddress address, string field, CancellationToken ct)
    {
        // An already exact logical address, not ancestor/name resolution. Inventory contains metadata only.
        var inventory = await identities.ListExactInventoryAsync(address.ScopeRef, ct);
        var target = inventory.FirstOrDefault(value => value.Namespace == address.Namespace && value.Kind == address.Kind && value.Name == address.Name);
        if (target is not null)
            await PinAsync(target.Uid, field, ct, kind: "value-reference", resolution: "exact-address-live-value", identityOnly: true);
    }

    public void Link(string target, string field, string kind = "exact-dependency", string resolution = "recorded-pin", string? assessment = null, string? selectedPublication = null,
        string? targetField = null, string? expectedHash = null, string? parentHash = null, string? bindingHash = null, string? capabilityHash = null) =>
        relations.Add(new(source.Id, target, kind, field, resolution)
        {
            Assessment = assessment,
            SelectedPublication = selectedPublication,
            TargetField = targetField,
            ExpectedHash = expectedHash,
            ParentHash = parentHash,
            BindingHash = bindingHash,
            CapabilityHash = capabilityHash
        });

    public async Task PinAsync(Guid uid, string field, CancellationToken ct, long? generation = null,
        string? version = null, string kind = "exact-dependency", string resolution = "recorded-pin", bool identityOnly = false, string? assessment = null,
        string? targetField = null, string? expectedHash = null, string? parentHash = null, string? bindingHash = null, string? capabilityHash = null,
        string? selectedPublication = null)
    {
        ct.ThrowIfCancellationRequested();
        var identity = await identities.GetIdentityByUidAsync(uid, ct);
        if (identity is null) return; // Missing historical UID is unknown, never rebound to its old name.
        var targetIdentity = new SystemGraphIdentity(uid, identity.ScopeRef, identity.Namespace, identity.Kind, identity.Name);
        var target = version is not null ? SystemGraphIds.Publication(uid, version) : generation is { } value
            ? SystemGraphIds.Generation(uid, value) : SystemGraphIds.Definition(uid);
        if (generation is { } pinned && version is null)
        {
            nodes.Add(new(target, targetIdentity, "recorded-generation", new(pinned)));
            relations.Add(new(target, SystemGraphIds.Definition(uid), "generation-of"));
        }
        else if (identityOnly)
            nodes.Add(new(target, targetIdentity, "reference-identity"));
        Link(target, field, kind, resolution, assessment, selectedPublication ?? version, targetField, expectedHash, parentHash, bindingHash, capabilityHash);
    }
}
