using Agentstration.Application;
using Agentstration.Application.Workflows;

namespace Agentstration.Web.Hosting;

public sealed class ItemProcessingWorker(IItemProcessingQueue queue, ContentProcessingWorkflow workflow, ILogger<ItemProcessingWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var work = await queue.DequeueAsync(stoppingToken);
            try
            {
                await workflow.ExecuteAsync(work.WorkspaceId, work.ItemId, stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "Item processing failed for workspace {WorkspaceId}, item {ItemId}", work.WorkspaceId, work.ItemId);
            }
        }
    }
}
