using Agentstration.Application.Work;
using Agentstration.Flows.Application;
using Agentstration.Work.Storage.Abstractions;

namespace Agentstration.Web.Hosting;

public sealed class WorkPlanExecutionWorker(IWorkPlanRepository plans, WorkPlanExecutionService executions,
    IFlowRunExecutionScope scopes, TimeProvider clock, ILogger<WorkPlanExecutionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var skip = 0;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2), clock);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var page = await plans.ListExecutionsAsync(skip, 100, stoppingToken);
                foreach (var plan in page.Where(plan => plan.Execution is not null))
                {
                    try
                    {
                        await scopes.ValidateAsync(plan.Execution!.Scope, stoppingToken);
                        using var scope = scopes.Enter(plan.Execution.Scope);
                        var result = await executions.AdvanceAsync(plan.WorkspaceId, plan.Id, stoppingToken);
                        if (!result.IsSuccess && logger.IsEnabled(LogLevel.Warning))
                            logger.LogWarning("WorkPlan {PlanId} in Workspace {WorkspaceId} could not advance: {Code}", plan.Id.Value, plan.WorkspaceId.Value, result.ErrorCode);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                    catch (Exception exception)
                    {
                        // Provider exception messages can contain content or credentials.
                        if (logger.IsEnabled(LogLevel.Warning)) logger.LogWarning("WorkPlan {PlanId} in Workspace {WorkspaceId} will be reconsidered after {FailureType}",
                            plan.Id.Value, plan.WorkspaceId.Value, exception.GetType().Name);
                    }
                }
                skip = page.Count < 100 ? 0 : skip + page.Count;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                skip = 0;
                if (logger.IsEnabled(LogLevel.Warning)) logger.LogWarning("WorkPlan enumeration failed with {FailureType}", exception.GetType().Name);
            }
            try { if (!await timer.WaitForNextTickAsync(stoppingToken)) break; }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
