using System.Text.Json.Serialization;
using Agentstration.Resources;

namespace Agentstration.ResourceManagement.Contracts;

[JsonConverter(typeof(JsonStringEnumConverter<ResourceRelatedAccessMode>))]
public enum ResourceRelatedAccessMode { Inherit, Allow, Deny }

public sealed record ResourceRelatedAccessItem(ResourceAccessIdentity Resource, string Name,
    ResourceAccessPolicyResponse? Policy)
{
    public string? DisplayName { get; init; }
}
public sealed record ResourceRelatedAccessSnapshot(ResourceAccessIdentity Parent,
    string MembershipToken, IReadOnlyList<ResourceRelatedAccessItem> Resources)
{
    public bool CanManage { get; init; }
}
public sealed record ResourceRelatedAccessRequest(ResourceScopeRef ConsumerScopeRef,
    ResourceRelatedAccessMode Mode, string MembershipToken, IReadOnlyDictionary<Guid, string?> PolicyETags)
{
    public IReadOnlyList<Guid>? ResourceUids { get; init; }
}
public sealed record ResourceRelatedAccessResult(Guid ResourceUid, bool Applied, string? ErrorCode);
public sealed record ResourceRelatedAccessEvaluation(bool Eligible, int Discoverable, int Usable,
    int Total, int Exceptions, IReadOnlyList<string> Reasons)
{
    public int Unavailable { get; init; }
}
