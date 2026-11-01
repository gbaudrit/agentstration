using System.Globalization;
using System.Xml;
using Agentstration.Application.Work;
using Agentstration.Flows;
using Agentstration.Flows.Application;
using Agentstration.Flows.Storage.Abstractions;
using Agentstration.Identity.Contracts;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;
using Agentstration.Triggers;
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

public sealed class TriggerOperationalContextAdapter(
    ICurrentRequestContext context,
    IRequestContextScopeFactory scopes) : ITriggerOperationalContext
{
    public TriggerOperationalContext Current
    {
        get
        {
            if (context.AccessMode is not (ControlPlaneAccessMode.Workspace or ControlPlaneAccessMode.Instance or ControlPlaneAccessMode.TenantAdministration))
                throw new TriggerValidationException("trigger_operational_workspace_unavailable", "The selected context has no governed operational Workspace.");
            var current = context.Current;
            if (current.WorkspaceId == Guid.Empty)
                throw new TriggerValidationException("trigger_operational_workspace_unavailable", "The selected context has no governed operational Workspace.");
            var logicalScope = context.AccessMode switch
            {
                ControlPlaneAccessMode.Instance => ResourceScopeRef.Instance,
                ControlPlaneAccessMode.TenantAdministration => ResourceScopeRef.Tenant(current.TenantId),
                _ => ResourceScopeRef.Workspace(current.WorkspaceId)
            };
            return new(current.TenantId, current.WorkspaceId, current.PrincipalId, logicalScope);
        }
    }

    public IDisposable EnterWorkspace()
    {
        var current = Current;
        return scopes.Push(new RequestContext(current.PrincipalId, current.TenantId, current.WorkspaceId));
    }

    public IDisposable EnterWorkspace(TriggerExecutionScope executionScope) =>
        scopes.Push(new RequestContext(
            executionScope.PrincipalId,
            executionScope.TenantId,
            executionScope.WorkspaceId));
}

public sealed class FlowTriggerTargetValidator(
    FlowService flows,
    IResourceAccessPolicyService accessPolicies) : ITriggerTargetValidator
{
    public async Task<TriggerFlowTarget> ResolveAsync(
        ResourceNamespace ownerNamespace,
        TriggerFlowTarget target,
        ResourceScopeRef consumerScopeRef,
        bool requireUse,
        CancellationToken cancellationToken)
    {
        var flowId = new FlowId(target.Name, target.Namespace ?? ownerNamespace);
        StoredFlow? stored;
        try
        {
            stored = await flows.GetVisibleAsync(consumerScopeRef, flowId, target.ScopeRef, cancellationToken);
        }
        catch (FlowReferenceOutsideScopeException exception)
        {
            throw new TriggerValidationException("trigger_target_not_discoverable", exception.Message);
        }
        catch (FlowReferenceAmbiguousException exception)
        {
            throw new TriggerValidationException("trigger_target_ambiguous", exception.Message);
        }
        if (stored is null)
            throw new TriggerValidationException("trigger_target_not_discoverable", "The selected Flow is not discoverable from this context.");
        var version = target.Version ?? stored.Value.ActiveVersion
            ?? throw new TriggerValidationException("trigger_target_version_missing", "The selected Flow has no active published version.");
        var published = await flows.GetVersionExactAsync(stored.Value.ScopeRef, flowId, version, cancellationToken)
            ?? throw new TriggerValidationException("trigger_target_version_missing", "The selected Flow version is no longer published.");
        var definitionHash = published.Value.DefinitionHash;
        if (string.IsNullOrWhiteSpace(definitionHash))
            throw new TriggerValidationException("trigger_target_identity_missing", "The selected Flow version has no immutable integrity identity.");

        if (target.Uid is { } expectedUid && expectedUid != stored.Value.Uid
            || target.DefinitionHash is { } expectedHash
                && !string.Equals(expectedHash, definitionHash, StringComparison.Ordinal))
            throw new TriggerValidationException("trigger_target_identity_stale", "The selected Flow identity no longer matches the saved Trigger target.");

        if (requireUse && stored.Value.ScopeRef != consumerScopeRef)
        {
            var access = await accessPolicies.EvaluateAsync(new ResourceAccessIdentity(
                stored.Value.Uid,
                stored.Value.ScopeRef,
                stored.Value.Id.Namespace,
                FlowResourceKinds.Flow,
                Version: stored.Value.ActiveVersion ?? stored.Value.Version), consumerScopeRef, ResourceAccessAction.Use, cancellationToken);
            if (!access.Allowed)
                throw new TriggerValidationException("trigger_target_use_denied", "The selected Flow cannot be used from this context.");
        }

        return target with
        {
            Namespace = stored.Value.Id.Namespace,
            ScopeRef = stored.Value.ScopeRef,
            Version = version,
            Uid = stored.Value.Uid,
            DefinitionHash = definitionHash
        };
    }
}

public sealed class WorkspaceTriggerExecutionAuthorizer(
    IFlowRunExecutionScope flowExecutionScope,
    IRequestContextScopeFactory requestScopes) : ITriggerExecutionAuthorizer
{
    public async Task AuthorizeAsync(TriggerExecutionScope executionScope, CancellationToken cancellationToken)
    {
        try
        {
            await flowExecutionScope.ValidateAsync(FlowScope(executionScope), cancellationToken);
        }
        catch (FlowValidationException exception)
        {
            throw new TriggerExecutionException("trigger_authorization_denied", exception.Message);
        }
    }

    public IDisposable Enter(TriggerExecutionScope triggerScope)
    {
        var flowScope = flowExecutionScope.Enter(FlowScope(triggerScope));
        try
        {
            var workspaceScope = requestScopes.Push(new RequestContext(
                triggerScope.PrincipalId, triggerScope.TenantId, triggerScope.WorkspaceId));
            return new CompositeScope(workspaceScope, flowScope);
        }
        catch
        {
            flowScope.Dispose();
            throw;
        }
    }

    private static FlowRunScope FlowScope(TriggerExecutionScope scope) =>
        FlowRunScope.Workspace(scope.TenantId, new WorkspaceId(scope.WorkspaceId), scope.PrincipalId) with
        {
            LogicalScopeRef = scope.LogicalScopeRef
        };

    private sealed class CompositeScope(IDisposable workspaceScope, IDisposable flowScope) : IDisposable
    {
        private IDisposable? workspace = workspaceScope;
        private IDisposable? flow = flowScope;

        public void Dispose()
        {
            Interlocked.Exchange(ref workspace, null)?.Dispose();
            Interlocked.Exchange(ref flow, null)?.Dispose();
        }
    }
}

public sealed class TriggerWorkSubmitter(RootFlowSubmissionService rootFlows, IWorkItemRepository repository, IWorkExecutionScopeAccessor executionScopeAccessor) : ITriggerWorkSubmitter
{
    public async Task<TriggerSubmission?> GetExistingAsync(Guid workspaceId, Guid occurrenceId, CancellationToken cancellationToken)
    {
        var existing = await repository.GetAsync(new WorkspaceId(workspaceId), CurrentPrincipalId(), new WorkItemId(occurrenceId), cancellationToken);
        return existing is null ? null : new(existing.Value.Id.ToString());
    }

    public async Task<bool> HasActiveWorkAsync(Guid workspaceId, Guid triggerUid, CancellationToken cancellationToken)
    {
        var page = await repository.QueryAsync(new WorkItemQuery(new WorkspaceId(workspaceId), CurrentPrincipalId(), Take: 200, Type: "trigger"), cancellationToken);
        return page.Items.Any(item => item.Value.Metadata.TryGetValue("triggerUid", out var value)
            && string.Equals(value, triggerUid.ToString("N"), StringComparison.Ordinal)
            && item.Value.Status is WorkItemStatus.Pending or WorkItemStatus.Queued or WorkItemStatus.Running or WorkItemStatus.WaitingForInput or WorkItemStatus.WaitingForApproval or WorkItemStatus.Paused);
    }

    public async Task<TriggerSubmission> SubmitAsync(TriggerResource trigger, TriggerOccurrence occurrence, CancellationToken cancellationToken)
    {
        var target = trigger.Definition.Target.Flow ?? throw new TriggerExecutionException("trigger_target_invalid", "The Trigger Flow target is missing.");
        var workspaceId = new WorkspaceId(occurrence.WorkspaceId);
        var ownerNamespace = trigger.Namespace;
        var reference = new FlowReference(new FlowId(target.Name, target.Namespace ?? ownerNamespace), target.Version, target.Version is null, target.Namespace, target.ScopeRef);
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
        var instruction = $"Triggered execution of Flow '{reference.FlowId}'.";
        var flowInput = System.Text.Json.JsonSerializer.SerializeToElement(new
        {
            prompt = instruction,
            inputs = inputs.Select(value => value.Structured ?? System.Text.Json.JsonSerializer.SerializeToElement(value.Text)).ToArray()
        });
        var submission = await rootFlows.SubmitAsync(new SubmitRootFlowCommand(
            workspaceId,
            reference,
            flowInput,
            FlowInvocationOrigin.Trigger,
            trigger.Definition.ExecutionScope?.PrincipalId.ToString("D") ?? "trigger-system",
            FlowRunTrigger.Schedule,
            occurrence.Id.ToString("N"),
            occurrence.Id.ToString("D"),
            $"trigger:{trigger.Uid:N}:{occurrence.Id:N}",
            "trigger",
            instruction,
            trigger.Definition.DisplayName,
            trigger.Definition.Description,
            Metadata: metadata,
            WorkInputs: inputs,
            WorkItemId: new WorkItemId(occurrence.Id)), cancellationToken);
        return new(submission.WorkItem.Value.Id.ToString());
    }

    private Guid CurrentPrincipalId() => executionScopeAccessor.Current?.PrincipalId
        ?? throw new TriggerExecutionException("trigger_execution_scope_required", "Trigger work access requires an authenticated execution scope.");
}
