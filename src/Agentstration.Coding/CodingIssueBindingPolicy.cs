using Agentstration.Resources;
using Agentstration.Work;

namespace Agentstration.Coding;

public interface ICodingIssueBindingPolicy
{
    Task<Result<CodingIssueBinding>> ResolveAsync(WorkspaceId workspaceId, Guid principalId,
        WorkProjectContextBinding binding, WorkSharingIssueReference issue, CancellationToken cancellationToken);
}
public sealed record CodingIssueBinding(string Provider, string ManagedLabelPrefix);
