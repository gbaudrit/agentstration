using Agentstration.Coding.Contracts;
using Agentstration.Flows;
using Agentstration.Work;

namespace Agentstration.Coding;

public sealed record CodingDeliveryExecution(CodingNativeResult Result, CodingExecutionConfiguration Configuration, string Digest)
{
    public string Repository => Configuration.Repository;
}

public interface ICodingDeliveryExecutionReader
{
    Task<Result<CodingDeliveryExecution>> ReadAsync(FlowRunScope scope, WorkProjectId projectId, WorkPlanId planId,
        WorkPlanAttempt attempt, CancellationToken cancellationToken);
}
