using Agentstration.Resources;

namespace Agentstration.Systems.Contracts;

public static class SystemResourceKinds
{
    public const string System = "System";
}

public sealed record SystemMember(Guid Uid, ResourceScopeRef ScopeRef, ResourceNamespace Namespace, string Kind, string Name);
public sealed record SystemProperties(string DisplayName, string? Description, IReadOnlyList<SystemMember> Members);
public sealed record SystemResource : Resource
{
    public required SystemProperties Definition { get; init; }
}

// Public views never return the persisted unfiltered membership document or private totals.
public sealed record SystemView(Guid Uid, ResourceScopeRef ScopeRef, ResourceNamespace Namespace, string Name,
    string DisplayName, string? Description, long Generation, string ETag, IReadOnlyList<SystemMember> Members);
public sealed record CreateSystemRequest(string Name, string DisplayName, string? Description = null,
    ResourceScopeRef? ScopeRef = null, ResourceNamespace? Namespace = null);
public sealed record RenameSystemRequest(string DisplayName, string? Description = null);
public sealed record AddSystemMemberRequest(SystemMember Member);
