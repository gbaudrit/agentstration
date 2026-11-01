using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentstration.ResourceManagement;
using Agentstration.Resources;

namespace Agentstration.Triggers;

public sealed class TriggerValidationException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed class TriggerExecutionException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed class TriggerManagementService(
    IResourceStore store,
    ITriggerOperationalContext operationalContext,
    ITriggerScheduleCalculator schedules,
    ITriggerTargetValidator targets,
    ITriggerSchedulerProjection scheduler,
    TimeProvider timeProvider)
{
    private static readonly HashSet<string> RawCredentialFieldNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "apiKey",
        "authorization",
        "clientSecret",
        "credential",
        "credentials",
        "password",
        "passphrase",
        "refreshToken",
        "secret",
        "token"
    };

    public async Task<StoredResource<TriggerResource>> CreateAsync(TriggerResource resource, CancellationToken cancellationToken)
    {
        ValidateEnvelope(resource);
        var context = operationalContext.Current;
        EnsureOperationalScope(resource.ScopeRef, context.WorkspaceScopeRef);
        var definition = await ValidateAndBindAsync(resource.Namespace, resource.Definition, context, cancellationToken);
        using var workspace = operationalContext.EnterWorkspace();
        var stored = await store.PutExactAsync(context.WorkspaceScopeRef, resource with
        {
            ScopeRef = context.WorkspaceScopeRef,
            Generation = 1,
            Definition = definition,
            Status = Succeeded(),
            Observed = resource.Observed with { NextOccurrenceAt = definition.Enabled ? schedules.GetNextOccurrence(definition.Source.Schedule!, timeProvider.GetUtcNow()) : null }
        }, null, true, cancellationToken);
        await scheduler.ReconcileAsync(stored.Value, cancellationToken);
        return stored;
    }

    public async Task<StoredResource<TriggerResource>> UpdateAsync(ResourceNamespace @namespace, string name, TriggerProperties definition, string? ifMatch, CancellationToken cancellationToken)
    {
        var context = operationalContext.Current;
        using var workspace = operationalContext.EnterWorkspace();
        var current = await GetRequiredExactAsync(context.WorkspaceScopeRef, @namespace, name, cancellationToken);
        var validated = await ValidateAndBindAsync(@namespace, definition, context, cancellationToken);
        var stored = await store.PutExactAsync(context.WorkspaceScopeRef, current.Value with
        {
            Generation = checked(current.Value.Generation + 1),
            Definition = validated,
            Status = Succeeded(),
            Observed = current.Value.Observed with { NextOccurrenceAt = validated.Enabled ? schedules.GetNextOccurrence(validated.Source.Schedule!, timeProvider.GetUtcNow()) : null }
        }, ifMatch, false, cancellationToken);
        await scheduler.ReconcileAsync(stored.Value, cancellationToken);
        return stored;
    }

    public async Task<StoredResource<TriggerResource>?> GetAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken)
    {
        var context = operationalContext.Current;
        using var workspace = operationalContext.EnterWorkspace();
        return await store.GetExactAsync<TriggerResource>(Address(context.WorkspaceScopeRef, @namespace, name), cancellationToken);
    }

    public async Task<IReadOnlyList<StoredResource<TriggerResource>>> ListAsync(CancellationToken cancellationToken)
    {
        var context = operationalContext.Current;
        using var workspace = operationalContext.EnterWorkspace();
        const int pageSize = 1000;
        var values = new List<StoredResource<TriggerResource>>();
        while (true)
        {
            var page = await store.ListExactAsync<TriggerResource>(
                context.WorkspaceScopeRef,
                TriggerResourceKinds.Trigger,
                values.Count,
                pageSize,
                cancellationToken);
            values.AddRange(page);
            if (page.Count < pageSize) return values;
        }
    }

    public async Task DeleteAsync(ResourceNamespace @namespace, string name, string? ifMatch, CancellationToken cancellationToken)
    {
        var context = operationalContext.Current;
        using var workspace = operationalContext.EnterWorkspace();
        var current = await GetRequiredExactAsync(context.WorkspaceScopeRef, @namespace, name, cancellationToken);
        await store.DeleteExactAsync(Address(context.WorkspaceScopeRef, @namespace, name), ifMatch, cancellationToken);
        await scheduler.RemoveAsync(WorkspaceId(current.Value), current.Value.Uid, cancellationToken);
    }

    private async Task<TriggerProperties> ValidateAndBindAsync(ResourceNamespace ownerNamespace, TriggerProperties definition, TriggerOperationalContext context, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(definition.DisplayName)) throw Invalid("displayName", "A display name is required.");
        if (!string.Equals(definition.Source.Kind, "schedule", StringComparison.Ordinal)) throw Invalid("source.kind", "V1 supports only the 'schedule' source kind.");
        if (definition.Source.Schedule is null) throw Invalid("source.schedule", "A schedule is required.");
        schedules.Validate(definition.Source.Schedule);
        if (!string.Equals(definition.Target.Kind, "flow", StringComparison.Ordinal)) throw Invalid("target.kind", "V1 supports only the 'flow' target kind.");
        if (definition.Target.Flow is null || string.IsNullOrWhiteSpace(definition.Target.Flow.Name)) throw Invalid("target.flow", "A Flow target is required.");
        var target = await targets.ResolveAsync(ownerNamespace, definition.Target.Flow, context.LogicalScopeRef, definition.Enabled, cancellationToken);
        if (definition.Input.ValueKind is not JsonValueKind.Undefined and not JsonValueKind.Object) throw Invalid("input", "Static input must be a JSON object.");
        if (definition.Input.ValueKind != JsonValueKind.Undefined && JsonSerializer.SerializeToUtf8Bytes(definition.Input).Length > 65_536) throw Invalid("input", "Static input cannot exceed 64 KiB.");
        if (definition.Input.ValueKind != JsonValueKind.Undefined && ContainsRawCredential(definition.Input)) throw Invalid("input", "Static input cannot contain raw credentials; configure credentials on providers/tools and use explicit references when supported.");

        var scope = definition.Enabled ? context.ExecutionScope : definition.ExecutionScope;
        return definition with { Target = definition.Target with { Flow = target }, ExecutionScope = scope };
    }

    private static void ValidateEnvelope(TriggerResource resource)
    {
        if (resource.ApiVersion != ResourceApiVersions.CoreV1) throw Invalid("apiVersion", $"apiVersion must be '{ResourceApiVersions.CoreV1}'.");
        if (resource.Kind != TriggerResourceKinds.Trigger) throw Invalid("kind", $"kind must be '{TriggerResourceKinds.Trigger}'.");
        if (string.IsNullOrWhiteSpace(resource.Metadata.Name)) throw Invalid("metadata.name", "A resource name is required.");
    }

    private static bool ContainsRawCredential(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            return element.EnumerateArray().Any(ContainsRawCredential);
        }
        if (element.ValueKind != JsonValueKind.Object) return false;
        foreach (var property in element.EnumerateObject())
        {
            var isReference = property.Name.EndsWith("Ref", StringComparison.OrdinalIgnoreCase)
                || property.Name.EndsWith("Reference", StringComparison.OrdinalIgnoreCase);
            if (!isReference && RawCredentialFieldNames.Contains(property.Name) && property.Value.ValueKind != JsonValueKind.Null) return true;
            if (ContainsRawCredential(property.Value)) return true;
        }
        return false;
    }

    private static ResourceStatus Succeeded() => new() { ProvisioningState = ProvisioningState.Succeeded };
    private static TriggerValidationException Invalid(string field, string message) => new("trigger_invalid", $"{field}: {message}");

    private static void EnsureOperationalScope(ResourceScopeRef? requested, ResourceScopeRef operational)
    {
        if (requested is not null && requested != operational)
            throw new TriggerValidationException("trigger_scope_mismatch", "A Trigger is owned by the operational Workspace of the selected context.");
    }

    private static ScopedResourceAddress Address(ResourceScopeRef scopeRef, ResourceNamespace @namespace, string name) =>
        ScopedResourceAddress.Create(scopeRef, @namespace, TriggerResourceKinds.Trigger, name);

    private async Task<StoredResource<TriggerResource>> GetRequiredExactAsync(ResourceScopeRef scopeRef, ResourceNamespace @namespace, string name, CancellationToken cancellationToken) =>
        await store.GetExactAsync<TriggerResource>(Address(scopeRef, @namespace, name), cancellationToken)
        ?? throw new ResourceNotFoundException(new(TriggerResourceKinds.Trigger, name, @namespace));

    private static Guid WorkspaceId(Resource resource) =>
        resource.ScopeRef is { } scopeRef
        && scopeRef.Kind == ResourceScopeKind.Workspace
        && scopeRef.TargetId is Guid workspaceId
            ? workspaceId
            : throw new InvalidOperationException("A Trigger must belong to a workspace scope.");
}

public sealed class TriggerFiringService(
    IResourceStore store,
    ITriggerOperationalContext operationalContext,
    ITriggerOccurrenceStore occurrences,
    ITriggerTargetValidator targets,
    ITriggerExecutionAuthorizer authorizer,
    ITriggerWorkSubmitter workSubmitter,
    ITriggerScheduleCalculator schedules,
    TimeProvider timeProvider)
{
    public async Task<TriggerOccurrence> FireScheduledAsync(Guid workspaceId, ResourceNamespace @namespace, string name, DateTimeOffset scheduledAt, CancellationToken cancellationToken)
    {
        var trigger = await GetRequiredExactAsync(workspaceId, @namespace, name, cancellationToken);
        return await FireAsync(trigger.Value, TriggerOccurrenceKind.Scheduled, scheduledAt, DeterministicOccurrenceId(trigger.Value, scheduledAt), cancellationToken);
    }

    public async Task<TriggerOccurrence> RunNowAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken)
    {
        var context = operationalContext.Current;
        StoredResource<TriggerResource> trigger;
        using (operationalContext.EnterWorkspace())
            trigger = await GetRequiredExactAsync(context.WorkspaceId, @namespace, name, cancellationToken);
        return await FireAsync(trigger.Value, TriggerOccurrenceKind.Manual, timeProvider.GetUtcNow(), Guid.NewGuid(), cancellationToken);
    }

    public Task<IReadOnlyList<TriggerOccurrence>> ListHistoryAsync(Guid workspaceId, Guid triggerUid, int take, CancellationToken cancellationToken) =>
        occurrences.ListAsync(workspaceId, triggerUid, Math.Clamp(take, 1, 200), cancellationToken);

    private async Task<TriggerOccurrence> FireAsync(TriggerResource trigger, TriggerOccurrenceKind kind, DateTimeOffset scheduledAt, Guid occurrenceId, CancellationToken cancellationToken)
    {
        if (!trigger.Definition.Enabled) throw new TriggerExecutionException("trigger_disabled", "The Trigger is disabled.");
        var scope = trigger.Definition.ExecutionScope ?? throw new TriggerExecutionException("trigger_identity_missing", "The Trigger has no execution identity.");
        var workspaceId = WorkspaceId(trigger);
        if (scope.WorkspaceId != workspaceId) throw new TriggerExecutionException("trigger_scope_mismatch", "The Trigger execution identity does not match its resource scope.");
        var occurrence = new TriggerOccurrence
        {
            Id = occurrenceId,
            TenantId = scope.TenantId,
            WorkspaceId = workspaceId,
            TriggerUid = trigger.Uid,
            TriggerName = trigger.Name,
            TriggerNamespace = trigger.Namespace,
            TriggerGeneration = trigger.Generation,
            Kind = kind,
            ScheduledAt = scheduledAt
        };
        if (!await occurrences.TryCreateAsync(occurrence, cancellationToken))
        {
            var existing = (await occurrences.ListAsync(workspaceId, trigger.Uid, 200, cancellationToken)).Single(value => value.Id == occurrenceId);
            if (existing.Outcome == TriggerOccurrenceOutcome.Pending)
            {
                var recovered = await workSubmitter.GetExistingAsync(workspaceId, occurrenceId, cancellationToken);
                if (recovered is not null)
                {
                    var recoveredAt = timeProvider.GetUtcNow();
                    await occurrences.CompleteAsync(workspaceId, occurrenceId, TriggerOccurrenceOutcome.Submitted, recoveredAt, recovered.WorkItemId, null, null, cancellationToken);
                    var recoveredOccurrence = existing with { FiredAt = recoveredAt, Outcome = TriggerOccurrenceOutcome.Submitted, WorkItemId = recovered.WorkItemId };
                    await RecordObservedAsync(trigger, recoveredOccurrence, cancellationToken);
                    return recoveredOccurrence;
                }
            }
            return existing;
        }

        var firedAt = timeProvider.GetUtcNow();
        try
        {
            var target = trigger.Definition.Target.Flow
                ?? throw new TriggerExecutionException("trigger_target_invalid", "The Trigger Flow target is missing.");
            try
            {
                _ = await targets.ResolveAsync(trigger.Namespace, target, scope.AccessScopeRef, true, cancellationToken);
            }
            catch (TriggerValidationException exception)
            {
                throw new TriggerExecutionException(exception.Code, exception.Message);
            }
            await authorizer.AuthorizeAsync(scope, cancellationToken);
            using var executionScope = authorizer.Enter(scope);
            if (trigger.Definition.ConcurrencyPolicy == TriggerConcurrencyPolicy.Skip
                && await workSubmitter.HasActiveWorkAsync(workspaceId, trigger.Uid, cancellationToken))
            {
                await occurrences.CompleteAsync(workspaceId, occurrenceId, TriggerOccurrenceOutcome.Skipped, firedAt, null, "concurrency_skip", null, cancellationToken);
                var skipped = occurrence with { FiredAt = firedAt, Outcome = TriggerOccurrenceOutcome.Skipped, ErrorCode = "concurrency_skip" };
                await RecordObservedAsync(trigger, skipped, cancellationToken);
                return skipped;
            }
            var submission = await workSubmitter.SubmitAsync(trigger, occurrence, cancellationToken);
            await occurrences.CompleteAsync(workspaceId, occurrenceId, TriggerOccurrenceOutcome.Submitted, firedAt, submission.WorkItemId, null, null, cancellationToken);
            var submitted = occurrence with { FiredAt = firedAt, Outcome = TriggerOccurrenceOutcome.Submitted, WorkItemId = submission.WorkItemId };
            await RecordObservedAsync(trigger, submitted, cancellationToken);
            return submitted;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var code = exception is TriggerExecutionException known ? known.Code : "work_submission_failed";
            await occurrences.CompleteAsync(workspaceId, occurrenceId, TriggerOccurrenceOutcome.Failed, firedAt, null, code, exception.Message, cancellationToken);
            var failed = occurrence with { FiredAt = firedAt, Outcome = TriggerOccurrenceOutcome.Failed, ErrorCode = code, ErrorMessage = exception.Message };
            await RecordObservedAsync(trigger, failed, cancellationToken);
            return failed;
        }
    }

    private async Task RecordObservedAsync(TriggerResource trigger, TriggerOccurrence occurrence, CancellationToken cancellationToken)
    {
        var executionScope = trigger.Definition.ExecutionScope
            ?? throw new TriggerExecutionException("trigger_identity_missing", "The Trigger has no execution identity.");
        var lastOutcome = occurrence.Outcome switch
        {
            TriggerOccurrenceOutcome.Submitted => TriggerLastOutcome.Submitted,
            TriggerOccurrenceOutcome.Skipped => TriggerLastOutcome.Skipped,
            TriggerOccurrenceOutcome.Failed => TriggerLastOutcome.Failed,
            _ => TriggerLastOutcome.None
        };
        var schedule = trigger.Definition.Source.Schedule;
        var updated = trigger with
        {
            Observed = trigger.Observed with
            {
                LastScheduledAt = occurrence.ScheduledAt,
                LastFiredAt = occurrence.FiredAt,
                NextOccurrenceAt = trigger.Definition.Enabled && schedule is not null ? schedules.GetNextOccurrence(schedule, occurrence.ScheduledAt) : null,
                LastOutcome = lastOutcome,
                LastErrorCode = occurrence.ErrorCode
            }
        };
        using var workspace = operationalContext.EnterWorkspace(executionScope);
        try { await store.PutExactAsync(trigger.ScopeRef!.Value, updated, trigger.ETag, false, cancellationToken); }
        catch (ResourceConcurrencyException) { }
    }

    private async Task<StoredResource<TriggerResource>> GetRequiredExactAsync(Guid workspaceId, ResourceNamespace @namespace, string name, CancellationToken cancellationToken)
    {
        var scopeRef = ResourceScopeRef.Workspace(workspaceId);
        return await store.GetExactAsync<TriggerResource>(
            ScopedResourceAddress.Create(scopeRef, @namespace, TriggerResourceKinds.Trigger, name), cancellationToken)
            ?? throw new ResourceNotFoundException(new(TriggerResourceKinds.Trigger, name, @namespace));
    }

    public static Guid DeterministicOccurrenceId(TriggerResource trigger, DateTimeOffset scheduledAt)
    {
        var value = $"{WorkspaceId(trigger):N}:{trigger.Uid:N}:{scheduledAt.ToUniversalTime():O}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return new Guid(hash.AsSpan(0, 16));
    }

    private static Guid WorkspaceId(Resource resource) =>
        resource.ScopeRef is { } scopeRef
        && scopeRef.Kind == ResourceScopeKind.Workspace
        && scopeRef.TargetId is Guid workspaceId
            ? workspaceId
            : throw new TriggerExecutionException("trigger_scope_missing", "A Trigger must belong to a workspace scope.");
}
