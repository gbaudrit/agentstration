using Agentstration.Resources;

namespace Agentstration.Agents;

public sealed record AgentProfileUsageWindow(IReadOnlyList<AgentResource> Value, int? NextOffset);

public interface IAgentProfileUsageQueries
{
    Task<AgentProfileUsageWindow> FindProfileCandidatesAsync(ResourceScopeRef scope, string profileName, string? search, int offset, int take, CancellationToken cancellationToken);
}
