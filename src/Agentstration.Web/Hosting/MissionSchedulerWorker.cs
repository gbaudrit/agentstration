using Agentstration.Application;
using Agentstration.Application.Missions;
using Agentstration.Domain;

namespace Agentstration.Web.Hosting;

public sealed class MissionSchedulerWorker(IPlatformStore store, MissionService missions, TimeProvider timeProvider, ILogger<MissionSchedulerWorker> logger) : BackgroundService, IScheduler
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30), timeProvider);
        while (await timer.WaitForNextTickAsync(stoppingToken)) await TriggerDueMissionsAsync(stoppingToken);
    }

    public async Task TriggerDueMissionsAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        foreach (var workspace in await store.ListWorkspacesAsync(cancellationToken))
        {
            var due = (await store.ListMissionsAsync(workspace.Id, cancellationToken)).Where(mission => mission.Status == MissionStatus.Active && mission.NextRunAt <= now);
            foreach (var mission in due)
            {
                var result = await missions.RunAsync(workspace.Id, mission.Id, cancellationToken);
                if (!result.IsSuccess) logger.LogWarning("Scheduled mission {MissionId} failed: {ErrorCode}", mission.Id, result.Error?.Code);
            }
        }
    }
}
