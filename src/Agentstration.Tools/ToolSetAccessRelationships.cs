using System.Text.Json;
using Agentstration.ResourceManagement;
using Agentstration.Resources;

namespace Agentstration.Tools;

public sealed class ToolSetAccessRelationships : IResourceAccessRelationshipProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public ResourceAccessRelationships Describe(ResourceAccessDocument resource,
        IReadOnlyDictionary<Guid, ResourceAccessDocument> documents)
    {
        if (resource.Identity.Kind == ToolResourceKinds.ToolSetVersion)
        {
            var version = resource.Payload.Deserialize<ToolSetVersionResource>(JsonOptions);
            if (version is null || !documents.TryGetValue(version.ToolSetUid, out var parent)
                || parent.Identity.Uid != version.ToolSetUid
                || parent.Identity.Kind != ToolResourceKinds.ToolSet || parent.Identity.ScopeRef != resource.Identity.ScopeRef
                || parent.Identity.Namespace != resource.Identity.Namespace || parent.Identity.Name != version.ToolSetName)
                return new() { Valid = false };
            return new() { PolicySourceUid = parent.Identity.Uid };
        }
        if (resource.Identity.Kind == ToolResourceKinds.ToolSet)
        {
            var set = resource.Payload.Deserialize<ToolSetResource>(JsonOptions)!;
            var members = new HashSet<Guid>();
            foreach (var member in set.Definition.Members)
            {
                // This is an explicit persisted composition reference, not an inferred name/prefix grouping.
                var match = documents.Values.SingleOrDefault(value => value.Identity.Kind == ToolResourceKinds.Tool
                    && value.Identity.ScopeRef == resource.Identity.ScopeRef
                    && value.Identity.ScopeRef == (member.Tool.ScopeRef ?? set.ScopeRef)
                    && value.Identity.Namespace == (member.Tool.Namespace ?? set.Namespace)
                    && value.Identity.Name == member.Tool.Name);
                if (match is null || member.ToolUid is { } uid && uid != match.Identity.Uid
                    || member.ToolGeneration is { } generation && generation != match.Identity.Generation) continue;
                if (resource.Governance.IsManaged && (member.ToolUid is null || member.ToolGeneration is null
                    || !SameManager(resource.Governance, match.Governance)
                    || resource.Governance.Origin.ResourceUid != match.Governance.Origin.ResourceUid)) continue;
                members.Add(match.Identity.Uid);
            }
            return new() { Collection = true, Members = members.ToArray(), Available = set.Definition.Enabled };
        }
        if (resource.Identity.Kind == ToolResourceKinds.ToolProvider)
        {
            var members = documents.Values.Where(value => value.Identity.Kind == ToolResourceKinds.Tool
                && value.Identity.ScopeRef == resource.Identity.ScopeRef
                && (!resource.Governance.IsManaged || SameManager(value.Governance, resource.Governance)
                    && value.Governance.Origin.ResourceUid == resource.Governance.Origin.ResourceUid))
                .Where(value => value.Payload.Deserialize<ToolResource>(JsonOptions) is { Definition.Provider: { } provider }
                    && provider.Name == resource.Identity.Name
                    && (provider.ScopeRef ?? value.Identity.ScopeRef) == resource.Identity.ScopeRef
                    && (provider.Namespace ?? value.Identity.Namespace) == resource.Identity.Namespace)
                .Select(value => value.Identity.Uid).ToArray();
            var provider = resource.Payload.Deserialize<ToolProviderResource>(JsonOptions)!;
            return new() { Collection = true, Members = members, Available = provider.Definition.Enabled
                && provider.Definition.Discovery.Status == "connected" };
        }
        if (resource.Identity.Kind != ToolResourceKinds.Tool) return new();
        var sources = new HashSet<Guid>();
        foreach (var candidate in documents.Values.Where(value => value.Identity.Kind == ToolResourceKinds.ToolSet
            && value.Identity.ScopeRef == resource.Identity.ScopeRef))
        {
            var set = candidate.Payload.Deserialize<ToolSetResource>(JsonOptions);
            if (set is null) continue;
            if (candidate.Governance.IsManaged && resource.Governance.IsManaged
                && SameManager(candidate.Governance, resource.Governance)
                && candidate.Governance.Origin.ResourceUid == resource.Governance.Origin.ResourceUid
                && set.Definition.Members.Any(member => member.ToolUid == resource.Identity.Uid
                    && member.ToolGeneration == resource.Identity.Generation
                    && member.Tool.Name == resource.Identity.Name
                    && (member.Tool.ScopeRef ?? set.ScopeRef) == resource.Identity.ScopeRef
                    && (member.Tool.Namespace ?? set.Namespace) == resource.Identity.Namespace))
            {
                // Same trusted manager may confer administrative inheritance before publication.
                sources.Add(candidate.Identity.Uid);
                continue;
            }
            // Every other composition needs published, exact same-owner member pins.
            foreach (var publication in documents.Values.Where(value => value.Identity.Kind == ToolResourceKinds.ToolSetVersion
                && value.Identity.ScopeRef == candidate.Identity.ScopeRef))
            {
                var version = publication.Payload.Deserialize<ToolSetVersionResource>(JsonOptions);
                if (version?.ToolSetUid == candidate.Identity.Uid && version.ToolSetGeneration == candidate.Identity.Generation
                    && version.Version == set.Definition.Version && version.Members.Any(member =>
                    member.ToolUid == resource.Identity.Uid && member.ToolGeneration == resource.Identity.Generation
                    && member.ToolScopeRef == resource.Identity.ScopeRef && member.ToolNamespace == resource.Identity.Namespace
                    && member.ToolName == resource.Identity.Name)) sources.Add(candidate.Identity.Uid);
            }
        }
        var tool = resource.Payload.Deserialize<ToolResource>(JsonOptions)!;
        var providerReference = tool.Definition.Provider;
        var providerDocument = providerReference is null ? null : documents.Values.SingleOrDefault(value =>
            value.Identity.Kind == ToolResourceKinds.ToolProvider && value.Identity.ScopeRef == (providerReference.ScopeRef ?? tool.ScopeRef)
            && value.Identity.Namespace == (providerReference.Namespace ?? tool.Namespace) && value.Identity.Name == providerReference.Name);
        var providerState = providerDocument?.Payload.Deserialize<ToolProviderResource>(JsonOptions);
        return new() { InheritFrom = sources.ToArray(), Available = (resource.OperationalEnabled ?? tool.Definition.Enabled)
            && tool.Definition.Discovery?.Available == true && providerState?.Definition.Enabled == true
            && providerState.Definition.Discovery.Status == "connected" };
    }
    private static bool SameManager(ResourceGovernance left, ResourceGovernance right) =>
        left.ManagedBy is { } manager && right.ManagedBy is { } other
        && manager.Kind == other.Kind && manager.Reference == other.Reference;

}
