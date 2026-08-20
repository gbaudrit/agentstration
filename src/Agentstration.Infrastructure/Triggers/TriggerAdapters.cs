using System.Globalization;
using System.Xml;
using Agentstration.Application.Work;
using Agentstration.Flow;
using Agentstration.Flow.Application;
using Agentstration.Management.Abstractions;
using Agentstration.Management.Core;
using Agentstration.Resources;
using Agentstration.Work;
using Agentstration.Work.Storage.Abstractions;
using Quartz;

namespace Agentstration.Infrastructure.Triggers;

public sealed class QuartzTriggerScheduleCalculator : ITriggerScheduleCalculator
{
    public void Validate(TriggerSchedule schedule)
    {
        switch (schedule.Type)
        {
            case TriggerScheduleType.Once:
                if (schedule.At is null) throw Invalid("A one-shot schedule requires 'at'.");
                break;
            case TriggerScheduleType.Cron:
                if (string.IsNullOrWhiteSpace(schedule.Expression) || !CronExpression.IsValidExpression(schedule.Expression))
                    throw Invalid("A valid Quartz cron expression is required.");
                _ = ResolveTimeZone(schedule.TimeZone);
                break;
            case TriggerScheduleType.Interval:
                if (schedule.StartAt is null) throw Invalid("An interval schedule requires the deterministic 'startAt' anchor.");
                if (ParseInterval(schedule.Every) <= TimeSpan.Zero) throw Invalid("An interval must be positive.");
                break;
            default:
                throw Invalid("The schedule type is not supported.");
        }
    }

    public DateTimeOffset? GetNextOccurrence(TriggerSchedule schedule, DateTimeOffset after)
    {
        Validate(schedule);
        return schedule.Type switch
        {
            TriggerScheduleType.Once => schedule.At > after ? schedule.At : null,
            TriggerScheduleType.Cron => new CronExpression(schedule.Expression!) { TimeZone = ResolveTimeZone(schedule.TimeZone) }.GetNextValidTimeAfter(after),
            TriggerScheduleType.Interval => NextInterval(schedule.StartAt!.Value, ParseInterval(schedule.Every), after),
            _ => null
        };
    }

    public static TimeSpan ParseInterval(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw Invalid("An ISO-8601 'every' duration is required.");
        try { return XmlConvert.ToTimeSpan(value); }
        catch (FormatException exception) { throw Invalid($"The interval is invalid: {exception.Message}"); }
    }

    public static TimeZoneInfo ResolveTimeZone(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) throw Invalid("An explicit IANA time zone is required for cron schedules.");
        if (id.Contains("Standard Time", StringComparison.OrdinalIgnoreCase)) throw Invalid("Use an IANA time zone identifier, not a Windows identifier.");
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (TimeZoneNotFoundException) { throw Invalid($"Time zone '{id}' was not found."); }
        catch (InvalidTimeZoneException) { throw Invalid($"Time zone '{id}' is invalid."); }
    }

    private static DateTimeOffset NextInterval(DateTimeOffset anchor, TimeSpan interval, DateTimeOffset after)
    {
        if (anchor > after) return anchor;
        var elapsedTicks = (after - anchor).Ticks;
        var steps = checked(elapsedTicks / interval.Ticks + 1);
        return anchor.AddTicks(checked(steps * interval.Ticks));
    }

    private static TriggerValidationException Invalid(string message) => new("schedule_invalid", message);
}

public sealed class FlowTriggerTargetValidator(FlowService flows, ICurrentRequestContext context) : ITriggerTargetValidator
{
    public async Task ValidateAsync(ResourceNamespace ownerNamespace, TriggerTarget target, CancellationToken cancellationToken)
    {
        if (!context.IsInitialized) throw new TriggerValidationException("trigger_context_missing", "Flow target validation requires a workspace context.");
        var flow = target.Flow ?? throw new TriggerValidationException("target_invalid", "A Flow target is required.");
        var reference = new FlowReference(new FlowId(flow.Name, flow.Namespace ?? ownerNamespace), flow.Version, flow.Version is null, flow.Namespace);
        _ = await flows.ResolveAsync(new WorkspaceId(context.Current.WorkspaceId), reference, ownerNamespace, cancellationToken);
    }
}

public sealed class WorkspaceTriggerExecutionAuthorizer(
    IIdentityStore identities,
    IAuthorizationService authorization,
    IRequestContextScopeFactory scopes) : ITriggerExecutionAuthorizer
{
    public async Task AuthorizeAsync(TriggerExecutionScope executionScope, CancellationToken cancellationToken)
    {
        var principal = await identities.GetPrincipalAsync(executionScope.PrincipalId, cancellationToken);
        var workspace = await identities.GetWorkspaceAsync(executionScope.TenantId, executionScope.WorkspaceId, cancellationToken);
        if (principal?.Status != PrincipalStatus.Active || workspace?.Status != WorkspaceStatus.Active)
            throw new TriggerExecutionException("trigger_authorization_denied", "The Trigger owner or Workspace is disabled.");
        var context = new RequestContext(executionScope.PrincipalId, executionScope.TenantId, executionScope.WorkspaceId);
        try { await authorization.EnsurePermissionAsync(context, AuthorizationPermissions.RunsExecute, cancellationToken); }
        catch (AuthorizationDeniedException) { throw new TriggerExecutionException("trigger_authorization_denied", "The Trigger owner no longer has runs/execute permission."); }
    }

    public IDisposable Enter(TriggerExecutionScope executionScope) =>
        scopes.Push(new RequestContext(executionScope.PrincipalId, executionScope.TenantId, executionScope.WorkspaceId));
}

public sealed class TriggerWorkSubmitter(FlowService flows, WorkItemService work, IWorkItemRepository repository) : ITriggerWorkSubmitter
{
    public async Task<TriggerSubmission?> GetExistingAsync(Guid workspaceId, Guid occurrenceId, CancellationToken cancellationToken)
    {
        var existing = await repository.GetAsync(new WorkspaceId(workspaceId), new WorkItemId(occurrenceId), cancellationToken);
        return existing is null ? null : new(existing.Value.Id.ToString());
    }

    public async Task<bool> HasActiveWorkAsync(Guid workspaceId, Guid triggerUid, CancellationToken cancellationToken)
    {
        var page = await repository.QueryAsync(new WorkItemQuery(new WorkspaceId(workspaceId), Take: 200, Type: "trigger"), cancellationToken);
        return page.Items.Any(item => item.Value.Metadata.TryGetValue("triggerUid", out var value)
            && string.Equals(value, triggerUid.ToString("N"), StringComparison.Ordinal)
            && item.Value.Status is WorkItemStatus.Pending or WorkItemStatus.Queued or WorkItemStatus.Running or WorkItemStatus.WaitingForInput or WorkItemStatus.WaitingForApproval or WorkItemStatus.Paused);
    }

    public async Task<TriggerSubmission> SubmitAsync(TriggerResource trigger, TriggerOccurrence occurrence, CancellationToken cancellationToken)
    {
        var target = trigger.Definition.Target.Flow ?? throw new TriggerExecutionException("trigger_target_invalid", "The Trigger Flow target is missing.");
        var workspaceId = new WorkspaceId(trigger.WorkspaceId);
        var ownerNamespace = trigger.Namespace;
        var reference = new FlowReference(new FlowId(target.Name, target.Namespace ?? ownerNamespace), target.Version, target.Version is null, target.Namespace);
        var resolved = await flows.ResolveAsync(workspaceId, reference, ownerNamespace, cancellationToken);
        var immutable = new FlowReference(resolved.FlowId, resolved.Version, false, resolved.FlowId.Namespace);
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["origin"] = "trigger",
            ["triggerUid"] = trigger.Uid.ToString("N"),
            ["triggerName"] = trigger.Name,
            ["triggerNamespace"] = trigger.Namespace.Value,
            ["triggerGeneration"] = trigger.Generation.ToString(CultureInfo.InvariantCulture),
            ["triggerOccurrenceId"] = occurrence.Id.ToString("N"),
            ["triggerScheduledAt"] = occurrence.ScheduledAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)
        };
        IReadOnlyList<WorkInput> inputs = trigger.Definition.Input.ValueKind == System.Text.Json.JsonValueKind.Undefined
            ? []
            : [new WorkInput(Structured: trigger.Definition.Input)];
        var stored = await work.SubmitAsync(new SubmitWorkItemCommand(
            workspaceId,
            "trigger",
            $"Triggered execution of Flow '{resolved.FlowId}'.",
            trigger.Definition.DisplayName,
            trigger.Definition.Description,
            trigger.Definition.ExecutionScope?.PrincipalId.ToString("D"),
            new WorkCorrelationId($"trigger:{trigger.Uid:N}:{occurrence.Id:N}"),
            Metadata: metadata,
            Inputs: inputs,
            Flow: immutable,
            Id: new WorkItemId(occurrence.Id)), cancellationToken);
        return new(stored.Value.Id.ToString());
    }
}
