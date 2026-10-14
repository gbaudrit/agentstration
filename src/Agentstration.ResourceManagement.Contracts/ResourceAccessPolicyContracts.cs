using System.Text.Json.Serialization;
using Agentstration.Resources;

namespace Agentstration.ResourceManagement.Contracts;

[JsonConverter(typeof(JsonStringEnumConverter<ResourceAccessAction>))]
public enum ResourceAccessAction
{
    [JsonStringEnumMemberName("discover")]
    Discover,
    [JsonStringEnumMemberName("use")]
    Use
}

public sealed record ResourceAccessIdentity(
    Guid Uid,
    ResourceScopeRef OwnerScopeRef,
    ResourceNamespace Namespace,
    string Kind,
    long? Generation = null,
    string? Version = null);

public sealed record ResourceAccessGrant(
    ResourceScopeRef TargetScopeRef,
    IReadOnlyList<ResourceAccessAction> Actions,
    bool IncludeDescendants = false);

public sealed record ResourceAccessPolicy(
    ResourceAccessIdentity Resource,
    IReadOnlyList<ResourceAccessGrant> Grants);

public sealed record ResourceAccessPolicyResponse(
    ResourceAccessPolicy Policy,
    string ETag,
    DateTimeOffset UpdatedAt);

public sealed record ResourceAccessDecision(
    bool Allowed,
    string ReasonCode);

public sealed record EvaluateResourceAccessRequest(
    ResourceAccessIdentity Resource,
    ResourceScopeRef ConsumerScopeRef,
    ResourceAccessAction Action);

public static class ResourceAccessDecisionReasons
{
    public const string SameScope = "same_scope";
    public const string ExactGrant = "exact_grant";
    public const string PropagatedGrant = "propagated_grant";
    public const string PolicyMissing = "policy_missing";
    public const string GrantMissing = "grant_missing";
    public const string ConsumerOutsideOwnerTree = "consumer_outside_owner_tree";
    public const string ResourceNotFound = "resource_not_found";
    public const string ResourceIdentityStale = "resource_identity_stale";
    public const string PolicyIdentityStale = "policy_identity_stale";
    public const string RequestInvalid = "request_invalid";
}

public interface IResourceAccessPolicyService
{
    Task<IReadOnlyList<ResourceAccessPolicyResponse>> ListAsync(
        ResourceScopeRef ownerScopeRef,
        CancellationToken cancellationToken);

    Task<ResourceAccessPolicyResponse?> GetAsync(
        Guid resourceUid,
        CancellationToken cancellationToken);

    Task<ResourceAccessPolicyResponse> PutAsync(
        ResourceAccessPolicy policy,
        string? ifMatch,
        CancellationToken cancellationToken);

    Task DeleteAsync(
        Guid resourceUid,
        string? ifMatch,
        CancellationToken cancellationToken);

    Task<ResourceAccessDecision> EvaluateAsync(
        ResourceAccessIdentity resource,
        ResourceScopeRef consumerScopeRef,
        ResourceAccessAction action,
        CancellationToken cancellationToken);
}
