using System.Text.Json;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;

namespace Agentstration.ResourceManagement;

public sealed record ResourceAccessDocument(ResourceIdentitySnapshot Identity, ResourceGovernance Governance,
    JsonElement Payload, ResourceAccessPolicy? Policy)
{
    public Guid? PolicySourceUid { get; init; }
    public string? PolicyETag { get; init; }
    public DateTimeOffset PolicyUpdatedAt { get; init; }
    public bool? OperationalEnabled { get; init; }
    public bool? Available { get; init; }
}

public sealed record ResourceAccessRelationships
{
    public bool Collection { get; init; }
    public IReadOnlyList<Guid> Members { get; init; } = [];
    public Guid? PolicySourceUid { get; init; }
    public bool? Available { get; init; }
    public bool Valid { get; init; } = true;
    public IReadOnlyList<Guid> Prerequisites { get; init; } = [];
    public IReadOnlyList<Guid> InheritFrom { get; init; } = [];
}

/// <summary>Family-owned rules over trusted saved documents, without storage or service dependencies.</summary>
public interface IResourceAccessRelationshipProvider
{
    ResourceAccessRelationships Describe(ResourceAccessDocument resource,
        IReadOnlyDictionary<Guid, ResourceAccessDocument> documents);
}

public interface IResourceAccessGraphReader
{
    Task<ResourceAccessGraph?> GetAccessGraphAsync(Guid resourceUid, CancellationToken cancellationToken);
}

public interface IResourceAccessGraphContributor
{
    Task<ResourceAccessDocument?> GetAccessDocumentAsync(Guid uid, CancellationToken cancellationToken);
    Task<IReadOnlyList<ResourceAccessDocument>> ListAccessDocumentsAsync(ResourceScopeRef scope, CancellationToken cancellationToken);
}

public sealed class ResourceAccessGraph(IReadOnlyDictionary<Guid, ResourceAccessDocument> documents,
    IEnumerable<IResourceAccessRelationshipProvider> providers)
{
    public IReadOnlyDictionary<Guid, ResourceAccessDocument> Documents => documents;
    public ResourceAccessGraph WithDocuments(IEnumerable<ResourceAccessDocument> additional) =>
        new(documents.Values.Concat(additional).ToDictionary(value => value.Identity.Uid), providers);

    public ResourceAccessRelationships Describe(Guid uid)
    {
        if (!documents.TryGetValue(uid, out var document)) return new() { Valid = false };
        var relations = providers.Select(provider => provider.Describe(document, documents)).ToArray();
        return new()
        {
            Valid = relations.All(value => value.Valid), Collection = relations.Any(value => value.Collection),
            Members = relations.SelectMany(value => value.Members).Distinct().Order().ToArray(),
            Prerequisites = relations.SelectMany(value => value.Prerequisites).Distinct().ToArray(),
            InheritFrom = relations.SelectMany(value => value.InheritFrom).Distinct().ToArray(),
            PolicySourceUid = relations.Select(value => value.PolicySourceUid).FirstOrDefault(value => value is not null),
            Available = document.Available ?? (relations.Any(value => value.Available == false) ? false
                : relations.Any(value => value.Available == true) ? true : null)
        };
    }

    public ResourceAccessDecision Evaluate(ResourceAccessIdentity requested, ResourceScopeRef consumer,
        IReadOnlyList<ResourceScopeRef> ancestors, ResourceAccessAction action) =>
        EvaluateCore(requested, consumer, ancestors, action, new HashSet<Guid>());

    private ResourceAccessDecision EvaluateCore(ResourceAccessIdentity requested, ResourceScopeRef consumer,
        IReadOnlyList<ResourceScopeRef> ancestors, ResourceAccessAction action, HashSet<Guid> path)
    {
        if (!documents.TryGetValue(requested.Uid, out var document))
            return new(false, ResourceAccessDecisionReasons.ResourceNotFound);
        var current = document.Identity;
        if (!Matches(requested, current)) return new(false, ResourceAccessDecisionReasons.ResourceIdentityStale);
        if (consumer != current.ScopeRef && !ancestors.Contains(current.ScopeRef))
            return new(false, ResourceAccessDecisionReasons.ConsumerOutsideOwnerTree);
        if (consumer == current.ScopeRef) return new(true, ResourceAccessDecisionReasons.SameScope);
        if (path.Count >= 32 || !path.Add(current.Uid))
            return new(false, ResourceAccessDecisionReasons.RelationshipInvalid);
        try
        {
            var relations = providers.Select(provider => provider.Describe(document, documents)).ToArray();
            if (relations.Any(value => !value.Valid))
                return new(false, ResourceAccessDecisionReasons.RelationshipInvalid);
            foreach (var uid in relations.SelectMany(value => value.Prerequisites).Distinct())
            {
                if (!documents.TryGetValue(uid, out var parent)
                    || parent.Identity.ScopeRef != current.ScopeRef
                    || !EvaluateCore(Identity(parent.Identity), consumer, ancestors, action, path).Allowed)
                    return new(false, ResourceAccessDecisionReasons.ParentAccessDenied);
            }
            if ((document.PolicySourceUid ?? relations.Select(value => value.PolicySourceUid).FirstOrDefault(value => value is not null)) is { } source)
                return documents.TryGetValue(source, out var sourceDocument)
                    && sourceDocument.Identity.ScopeRef == current.ScopeRef
                    ? EvaluateCore(Identity(sourceDocument.Identity), consumer, ancestors, action, path)
                    : new(false, ResourceAccessDecisionReasons.RelationshipInvalid);
            if (document.Policy is { } policy && !Matches(policy.Resource, current))
                return new(false, ResourceAccessDecisionReasons.PolicyIdentityStale);
            var local = ResourceAccessRules.Evaluate(document.Policy, consumer, ancestors, action);
            if (local.Allowed || local.ReasonCode == ResourceAccessDecisionReasons.ExplicitDeny) return local;
            foreach (var uid in relations.SelectMany(value => value.InheritFrom).Distinct())
            {
                if (documents.TryGetValue(uid, out var sourceDocument)
                    && sourceDocument.Identity.ScopeRef == current.ScopeRef
                    && EvaluateCore(Identity(sourceDocument.Identity), consumer, ancestors, action, path).Allowed)
                    return new(true, ResourceAccessDecisionReasons.ToolSetInherited);
            }
            return local;
        }
        finally { path.Remove(current.Uid); }
    }

    public static ResourceAccessIdentity Identity(ResourceIdentitySnapshot identity) =>
        new(identity.Uid, identity.ScopeRef, identity.Namespace, identity.Kind);

    public static bool Matches(ResourceAccessIdentity requested, ResourceIdentitySnapshot current) =>
        requested.Uid == current.Uid && requested.OwnerScopeRef == current.ScopeRef
        && requested.Namespace == current.Namespace && requested.Kind == current.Kind
        && (requested.Generation is null || requested.Generation == current.Generation)
        && (requested.Version is null || requested.Version == current.Version);
}
