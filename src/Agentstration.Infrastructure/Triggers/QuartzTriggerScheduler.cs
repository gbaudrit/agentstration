using Agentstration.Management.Abstractions;
using Agentstration.Management.Core;
using Agentstration.Resources;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Quartz;

namespace Agentstration.Infrastructure.Triggers;

public sealed class QuartzTriggerScheduler(ISchedulerFactory schedulers) : ITriggerSchedulerProjection
{
    private const string Group = "agentstration-triggers";

    public async Task ReconcileAsync(TriggerResource trigger, CancellationToken cancellationToken)
    {
        var scheduler = await schedulers.GetScheduler(cancellationToken);
        var key = JobKey(trigger);
        if (await scheduler.CheckExists(key, cancellationToken)) await scheduler.DeleteJob(key, cancellationToken);
        if (!trigger.Definition.Enabled) return;
        var schedule = trigger.Definition.Source.Schedule ?? throw new TriggerValidationException("schedule_missing", "The schedule is missing.");
        var job = JobBuilder.Create<TriggerQuartzJob>()
            .WithIdentity(key)
            .UsingJobData("namespace", trigger.Namespace.Value)
            .UsingJobData("name", trigger.Name)
            .StoreDurably(false)
            .Build();
        var quartzTrigger = BuildTrigger(trigger, schedule);
        await scheduler.ScheduleJob(job, quartzTrigger, cancellationToken);
    }

    public async Task RemoveAsync(Guid workspaceId, Guid triggerUid, CancellationToken cancellationToken)
    {
        var scheduler = await schedulers.GetScheduler(cancellationToken);
        await scheduler.DeleteJob(new JobKey($"{workspaceId:N}-{triggerUid:N}", Group), cancellationToken);
    }

    private static JobKey JobKey(TriggerResource trigger) => new($"{trigger.WorkspaceId:N}-{trigger.Uid:N}", Group);

    private static ITrigger BuildTrigger(TriggerResource trigger, TriggerSchedule schedule)
    {
        var builder = TriggerBuilder.Create().WithIdentity(JobKey(trigger).Name, Group).ForJob(JobKey(trigger));
        return schedule.Type switch
        {
            TriggerScheduleType.Once => builder.StartAt(schedule.At!.Value).WithSimpleSchedule(value => ApplySimpleMisfire(value, trigger.Definition.MisfirePolicy)).Build(),
            TriggerScheduleType.Interval => builder.StartAt(schedule.StartAt!.Value).WithSimpleSchedule(value =>
            {
                value.WithInterval(QuartzTriggerScheduleCalculator.ParseInterval(schedule.Every)).RepeatForever();
                ApplySimpleMisfire(value, trigger.Definition.MisfirePolicy);
            }).Build(),
            TriggerScheduleType.Cron => builder.WithCronSchedule(schedule.Expression!, value =>
            {
                value.InTimeZone(QuartzTriggerScheduleCalculator.ResolveTimeZone(schedule.TimeZone));
                ApplyCronMisfire(value, trigger.Definition.MisfirePolicy);
            }).Build(),
            _ => throw new TriggerValidationException("schedule_invalid", "Unsupported schedule type.")
        };
    }

    private static void ApplySimpleMisfire(SimpleScheduleBuilder builder, TriggerMisfirePolicy policy)
    {
        if (policy == TriggerMisfirePolicy.FireOnce) builder.WithMisfireHandlingInstructionFireNow();
        else builder.WithMisfireHandlingInstructionNextWithRemainingCount();
    }

    private static void ApplyCronMisfire(CronScheduleBuilder builder, TriggerMisfirePolicy policy)
    {
        if (policy == TriggerMisfirePolicy.FireOnce) builder.WithMisfireHandlingInstructionFireAndProceed();
        else builder.WithMisfireHandlingInstructionDoNothing();
    }
}

public sealed class TriggerQuartzJob(
    TriggerFiringService firing,
    IRequestContextScopeFactory scopes,
    ILogger<TriggerQuartzJob> logger) : IJob
{
    public async Task Execute(IJobExecutionContext context)
    {
        var @namespace = new ResourceNamespace(context.MergedJobDataMap.GetString("namespace") ?? ResourceNamespace.DefaultValue);
        var name = context.MergedJobDataMap.GetString("name") ?? throw new JobExecutionException("Trigger name is missing.");
        var scheduledAt = context.ScheduledFireTimeUtc ?? context.FireTimeUtc;
        using var system = scopes.PushSystem();
        var occurrence = await firing.FireScheduledAsync(@namespace, name, scheduledAt, context.CancellationToken);
        if (occurrence.Outcome == TriggerOccurrenceOutcome.Failed)
            logger.LogWarning("Trigger {TriggerNamespace}/{TriggerName} occurrence {OccurrenceId} failed before Work submission with {ErrorCode}", @namespace.Value, name, occurrence.Id, occurrence.ErrorCode);
    }
}

public sealed class TriggerSchedulerReconciler(
    IControlPlaneStore store,
    ITriggerSchedulerProjection scheduler,
    IRequestContextScopeFactory scopes,
    ILogger<TriggerSchedulerReconciler> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var system = scopes.PushSystem();
        var triggers = await store.ListAllAsync<TriggerResource>(ResourceKinds.Trigger, cancellationToken);
        foreach (var trigger in triggers)
        {
            try { await scheduler.ReconcileAsync(trigger.Value, cancellationToken); }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "Failed to reconcile Trigger {TriggerUid}", trigger.Value.Uid);
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
