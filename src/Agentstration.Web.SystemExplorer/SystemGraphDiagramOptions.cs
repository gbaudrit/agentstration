namespace Agentstration.Web.SystemExplorer;

public sealed record SystemGraphDiagramOptions(string Mode, string? Root = null,
    IReadOnlyList<SystemGraphObservedPlacement>? Placements = null);
public sealed record SystemGraphObservedPlacement(Guid WorkerId, Guid SessionId, string RunId, Guid AssignmentId,
    Guid AttemptId, DateTimeOffset LastHeartbeatAt, DateTimeOffset LeaseExpiresAt, string Presence);
