using Agentstration.DataSources;
using Agentstration.DataSources.Contracts;
using Agentstration.Infrastructure.Flows;
using Agentstration.Resources;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Agentstration.Infrastructure.DataSources;

internal sealed class SourceSampleStore(DirectExecutionPayloadPersistence payloads) : ISourceSampleStore
{
    public void Put(SourceSample sample) => payloads.StoreProcessing(ResourceScopeRef.Workspace(sample.WorkspaceId), "source-sample/" + sample.Id, sample, sample.ExpiresAt);
    public SourceSample? Get(Guid workspaceId, string id) => payloads.GetProcessing<SourceSample>(ResourceScopeRef.Workspace(workspaceId), "source-sample/" + id);
    public void Delete(Guid workspaceId, string id) => payloads.DeleteProcessing(ResourceScopeRef.Workspace(workspaceId), "source-sample/" + id);
}
internal sealed class SourceSampleWorker(SourceSampleService samples, DirectExecutionPayloadPersistence payloads,
    TimeProvider clock, ILogger<SourceSampleWorker> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.WhenAll(ProcessAsync(stoppingToken), PurgeAsync(stoppingToken));

    private async Task ProcessAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await samples.ProcessNextAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error) { logger.LogError(error, "Authoring sample processing failed"); }
        }
    }

    private async Task PurgeAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1), clock);
        while (await timer.WaitForNextTickAsync(stoppingToken)) payloads.PurgeExpired();
    }
}
