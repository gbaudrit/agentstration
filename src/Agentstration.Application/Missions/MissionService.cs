using System.Diagnostics;
using Agentstration.Application.Common;
using Agentstration.Contracts;
using Agentstration.Domain;

namespace Agentstration.Application.Missions;

public sealed class MissionService(IPlatformStore store, IObservationTool observationTool, IEventBus eventBus, TimeProvider timeProvider)
{
    public static readonly ActivitySource ActivitySource = new("Agentstration.Missions");

    public async Task<Result<Mission>> CreateAsync(WorkspaceId workspaceId, CreateMissionRequest request, CancellationToken cancellationToken)
    {
        if (await store.GetWorkspaceAsync(workspaceId, cancellationToken) is null)
        {
            return Result<Mission>.Failure("workspace.not_found", "Workspace was not found.");
        }

        if (!Uri.TryCreate(request.SourceUrl, UriKind.Absolute, out var source) || (source.Scheme != "demo" && source.Scheme is not "http" and not "https"))
        {
            return Result<Mission>.Failure("validation.source", "Source must be an HTTP, HTTPS, or demo URL.");
        }

        if (request.FrequencyMinutes is < 1 or > 525600)
        {
            return Result<Mission>.Failure("validation.frequency", "Frequency must be between 1 and 525600 minutes.");
        }

        var now = timeProvider.GetUtcNow();
        var mission = new Mission(MissionId.New(), workspaceId, request.Name.Trim(), request.Objective.Trim(), source, TimeSpan.FromMinutes(request.FrequencyMinutes), request.Threshold, MissionStatus.Active, now.AddMinutes(request.FrequencyMinutes), now);
        await store.AddMissionAsync(mission, cancellationToken);
        await store.AddAuditEntryAsync(new AuditEntry(Guid.NewGuid(), workspaceId, "mission.created", mission.Id.ToString(), now), cancellationToken);
        await eventBus.PublishAsync(new MissionCreated(workspaceId, mission.Id, now), cancellationToken);
        return Result<Mission>.Success(mission);
    }

    public Task<IReadOnlyList<Mission>> ListAsync(WorkspaceId workspaceId, CancellationToken cancellationToken) => store.ListMissionsAsync(workspaceId, cancellationToken);

    public async Task<Result<MissionDetails>> GetAsync(WorkspaceId workspaceId, MissionId missionId, CancellationToken cancellationToken)
    {
        var mission = await store.GetMissionAsync(workspaceId, missionId, cancellationToken);
        if (mission is null) return Result<MissionDetails>.Failure("mission.not_found", "Mission was not found in this workspace.");
        var runs = await store.ListMissionRunsAsync(workspaceId, missionId, cancellationToken);
        var notifications = await store.ListNotificationsAsync(workspaceId, missionId, cancellationToken);
        return Result<MissionDetails>.Success(new MissionDetails(mission, runs, notifications));
    }

    public async Task<Result<MissionRun>> RunAsync(WorkspaceId workspaceId, MissionId missionId, CancellationToken cancellationToken)
    {
        using var activity = ActivitySource.StartActivity("mission.run");
        activity?.SetTag("workspace.id", workspaceId.ToString());
        activity?.SetTag("mission.id", missionId.ToString());
        var mission = await store.GetMissionAsync(workspaceId, missionId, cancellationToken);
        if (mission is null) return Result<MissionRun>.Failure("mission.not_found", "Mission was not found in this workspace.");

        var now = timeProvider.GetUtcNow();
        var run = new MissionRun(MissionRunId.New(), workspaceId, missionId, MissionRunStatus.Running, null, false, null, now, null);
        await store.AddMissionRunAsync(run, cancellationToken);
        await eventBus.PublishAsync(new MissionRunStarted(workspaceId, missionId, run.Id, now), cancellationToken);
        try
        {
            var priorRuns = await store.ListMissionRunsAsync(workspaceId, missionId, cancellationToken);
            var observation = await observationTool.ObserveAsync(mission, Math.Max(0, priorRuns.Count - 1), cancellationToken);
            var previous = priorRuns.Where(candidate => candidate.Id != run.Id && candidate.Observation.HasValue).OrderByDescending(candidate => candidate.StartedAt).FirstOrDefault();
            var changed = previous?.Observation != observation;
            run = run with { Status = MissionRunStatus.Completed, Observation = observation, Changed = changed, CompletedAt = timeProvider.GetUtcNow() };
            await store.UpdateMissionRunAsync(run, cancellationToken);
            await memoryStoreObservationAsync(run, cancellationToken);
            mission = mission with { NextRunAt = timeProvider.GetUtcNow().Add(mission.Frequency) };
            await store.UpdateMissionAsync(mission, cancellationToken);

            if (changed && mission.Threshold is decimal threshold && observation <= threshold)
            {
                var message = $"Mission '{mission.Name}': observation {observation} reached threshold {threshold}.";
                await store.AddNotificationAsync(new Notification(Guid.NewGuid(), workspaceId, missionId, message, timeProvider.GetUtcNow()), cancellationToken);
                await eventBus.PublishAsync(new NotificationRequested(workspaceId, missionId, message, timeProvider.GetUtcNow()), cancellationToken);
            }

            await eventBus.PublishAsync(new MissionRunCompleted(workspaceId, missionId, run.Id, timeProvider.GetUtcNow()), cancellationToken);
            return Result<MissionRun>.Success(run);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            run = run with { Status = MissionRunStatus.Failed, Error = exception.Message, CompletedAt = timeProvider.GetUtcNow() };
            await store.UpdateMissionRunAsync(run, cancellationToken);
            await eventBus.PublishAsync(new MissionRunFailed(workspaceId, missionId, run.Id, exception.Message, timeProvider.GetUtcNow()), cancellationToken);
            return Result<MissionRun>.Failure("mission.run_failed", exception.Message);
        }
    }

    private Task memoryStoreObservationAsync(MissionRun run, CancellationToken cancellationToken) =>
        store.AddMemoryEntryAsync(new MemoryEntry(Guid.NewGuid(), run.WorkspaceId, null, run.MissionId, "observation", $"Observed value: {run.Observation}", Array.Empty<string>(), timeProvider.GetUtcNow()), cancellationToken);
}
