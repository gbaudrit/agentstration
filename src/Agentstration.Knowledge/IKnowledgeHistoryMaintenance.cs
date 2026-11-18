using Agentstration.Knowledge.Contracts;

namespace Agentstration.Knowledge;

public interface IKnowledgeHistoryMaintenance
{
    Task MaintainAsync(KnowledgeSourceResource source, CancellationToken cancellationToken);
}
