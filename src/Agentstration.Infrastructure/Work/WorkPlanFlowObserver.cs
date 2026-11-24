using Agentstration.Application.Work;
using Agentstration.Flows;
using Agentstration.Flows.Application;

namespace Agentstration.Infrastructure.Work;

public sealed class WorkPlanFlowObserver(FlowRunService runs) : IWorkPlanFlowObserver
{
    public async Task<FlowRun?> GetAsync(FlowRunScope scope, string runId, CancellationToken cancellationToken) =>
        (await runs.GetAsync(runId, scope, cancellationToken))?.Value;

    public async Task CancelAsync(FlowRunScope scope, string runId, CancellationToken cancellationToken) =>
        await runs.CancelAsync(runId, scope, cancellationToken);
}
