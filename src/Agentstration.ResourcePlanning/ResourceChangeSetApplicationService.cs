using System.Text.Json;
using System.Text.Json.Nodes;
using Agentstration.Identity.Contracts;
using Agentstration.Models;
using Agentstration.ResourceManagement;
using Agentstration.ResourcePlanning.Contracts;
using Agentstration.ResourcePlanning.Storage.Abstractions;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;
using Agentstration.Tools;
using Microsoft.Extensions.Logging;

namespace Agentstration.ResourcePlanning;

public interface IPlannedResourceApplier
{
    bool Supports(string kind);
    Task ApplyAsync(ResourceChange change, bool resume, CancellationToken cancellationToken);
}

public sealed class ResourceChangeSetApplicationException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed class ResourceChangeSetApplicationService(
    ResourcePlanService plans,
    ResourceChangeSetService changeSets,
    IResourceChangeSetRepository repository,
    IResourcePlanningStateReader stateReader,
    IResourceScopeOperations scopes,
    IEnumerable<IPlannedResourceValidator> validators,
    IEnumerable<IPlannedResourceApplier> appliers,
    TimeProvider timeProvider,
    ILogger<ResourceChangeSetApplicationService> logger)
{
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(15);

    public Task<ResourceChangeSetApplicationSnapshot?> GetAsync(ResourcePlanScope scope, ResourceChangeSetId id, CancellationToken cancellationToken) =>
        repository.GetApplicationAsync(scope, id, cancellationToken);

    public async Task<ResourceChangeSetApplicationSnapshot> ApplyAsync(ResourcePlanScope scope, ResourceChangeSetId id,
        ApplyResourceChangeSetRequest request, Guid actorPrincipalId, CancellationToken cancellationToken)
    {
        if (actorPrincipalId == Guid.Empty) throw new ArgumentException("An actor Principal is required.", nameof(actorPrincipalId));
        var changeSet = await changeSets.GetAsync(scope, id, cancellationToken);
        var plan = await plans.GetAsync(scope, changeSet.Value.PlanId, cancellationToken)
            ?? throw new ResourcePlanNotFoundException(changeSet.Value.PlanId);
        if (request.PlanId != plan.Value.Id || request.PlanRevision != plan.Value.Revision ||
            changeSet.Value.PlanRevision != plan.Value.Revision || request.ChangeSetDigest != changeSet.Value.Digest)
            throw new ResourceChangeSetApplicationException("resource_change_set_stale", "The plan or saved proposal changed. Review and verify the current proposal before applying it.");
        var latestSet = (await changeSets.ListAsync(scope, plan.Value.Id, 0, 1, cancellationToken)).Items.FirstOrDefault();
        if (latestSet?.Value.Id != id)
            throw new ResourceChangeSetApplicationException("resource_change_set_stale", "A newer proposal exists for this plan. Review and verify that proposal before applying it.");

        var validations = await repository.ListValidationsAsync(scope, id, cancellationToken);
        var validation = validations.LastOrDefault();
        if (validation is null || validation.Id != request.ValidationId || validation.Readiness != ResourceChangeSetReadiness.Ready ||
            validation.ChangeSetDigest != changeSet.Value.Digest || validation.PlanRevision != plan.Value.Revision)
            throw new ResourceChangeSetApplicationException("resource_change_set_validation_stale", "A current, successful verification is required before application.");

        var previous = await repository.GetApplicationAsync(scope, id, cancellationToken);
        if (previous?.Value.Status == ResourceChangeSetApplicationStatus.Applied)
        {
            if (changeSet.Value.Status != ResourceChangeSetStatus.Applied)
                _ = await repository.UpdateAsync(changeSet.Value with { Status = ResourceChangeSetStatus.Applied }, changeSet.ETag, cancellationToken);
            if (plan.Value.Status != ResourcePlanStatus.Applied)
                await plans.MarkAppliedAsync(scope, plan.Value.Id, actorPrincipalId, cancellationToken);
            return previous;
        }
        var now = timeProvider.GetUtcNow();
        if (previous?.Value.Status == ResourceChangeSetApplicationStatus.Applying && previous.Value.LeaseUntil > now)
            return previous;
        if (previous is not null && previous.Value.ValidationId != request.ValidationId)
            throw new ResourceChangeSetApplicationException("resource_change_set_application_conflict", "This proposal already has an application bound to another verification.");
        if (previous is not null && (previous.Value.ChangeSetDigest != changeSet.Value.Digest || previous.Value.PlanRevision != plan.Value.Revision))
            throw new ResourceChangeSetApplicationException("resource_change_set_application_conflict", "The recorded application targets another proposal revision.");
        if (previous is null && changeSet.Value.Status != ResourceChangeSetStatus.Validated)
            throw new ResourceChangeSetApplicationException("resource_change_set_not_validated", "The saved proposal must be verified before application.");

        var application = previous is null
            ? new ResourceChangeSetApplication(Guid.NewGuid(), plan.Value.Id, plan.Value.Revision, id, changeSet.Value.Digest,
                validation.Id, scope, ResourceChangeSetApplicationStatus.Applying, [], 1, actorPrincipalId, now, now, now.Add(Lease), null,
                [new ResourceChangeSetApplicationAttempt(1, actorPrincipalId, now, null, null)])
            : previous.Value with
            {
                Status = ResourceChangeSetApplicationStatus.Applying,
                Attempts = previous.Value.Attempts + 1,
                UpdatedAt = now,
                LeaseUntil = now.Add(Lease),
                CompletedAt = null,
                AttemptHistory = [.. previous.Value.AttemptHistory ?? [], new ResourceChangeSetApplicationAttempt(previous.Value.Attempts + 1, actorPrincipalId, now, null, null)]
            };
        ResourceChangeSetApplicationSnapshot stored;
        try { stored = await repository.SaveApplicationAsync(application, previous?.ETag, cancellationToken); }
        catch (ResourcePlanConcurrencyException)
        {
            return await repository.GetApplicationAsync(scope, id, cancellationToken)
                ?? throw new ResourceChangeSetApplicationException("resource_change_set_application_conflict", "Application state changed concurrently.");
        }
        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation("Resource ChangeSet application {ApplicationId} attempt {Attempt} started for Workspace {WorkspaceId}, plan {PlanId}, ChangeSet {ChangeSetId}",
                stored.Value.Id, stored.Value.Attempts, scope.WorkspaceId.Value, plan.Value.Id.Value, id.Value);
        var applyingSet = await repository.UpdateAsync(changeSet.Value with { Status = ResourceChangeSetStatus.Applying }, changeSet.ETag, cancellationToken);
        try
        {
            foreach (var change in changeSet.Value.Changes.OrderBy(value => value.Order))
            {
                if (stored.Value.Operations.Any(value => value.Order == change.Order && value.Outcome is
                    ResourceChangeApplicationOutcome.Applied or ResourceChangeApplicationOutcome.AlreadyApplied or ResourceChangeApplicationOutcome.Skipped)) continue;
                cancellationToken.ThrowIfCancellationRequested();
                var completed = await ApplyOneAsync(scope, changeSet.Value, stored.Value, change, cancellationToken);
                var operations = stored.Value.Operations.Where(value => value.Order != change.Order).Append(completed).OrderBy(value => value.Order).ToArray();
                var next = stored.Value with { Operations = operations, UpdatedAt = timeProvider.GetUtcNow(), LeaseUntil = timeProvider.GetUtcNow().Add(Lease) };
                stored = await repository.SaveApplicationAsync(next, stored.ETag, cancellationToken);
                if (logger.IsEnabled(LogLevel.Information))
                    logger.LogInformation("Resource ChangeSet application {ApplicationId} operation {Order} ({LogicalId}) finished with {Outcome} and code {ErrorCode}",
                        stored.Value.Id, completed.Order, completed.LogicalId, completed.Outcome, completed.ErrorCode);
                if (completed.Outcome == ResourceChangeApplicationOutcome.Failed) break;
            }
            var failed = stored.Value.Operations.Any(value => value.Outcome == ResourceChangeApplicationOutcome.Failed);
            var hasApplied = stored.Value.Operations.Any(value => value.Outcome is ResourceChangeApplicationOutcome.Applied or ResourceChangeApplicationOutcome.AlreadyApplied);
            var status = failed ? hasApplied ? ResourceChangeSetApplicationStatus.PartiallyApplied : ResourceChangeSetApplicationStatus.Failed : ResourceChangeSetApplicationStatus.Applied;
            stored = await repository.SaveApplicationAsync(stored.Value with
            {
                Status = status,
                UpdatedAt = timeProvider.GetUtcNow(),
                CompletedAt = timeProvider.GetUtcNow(),
                LeaseUntil = timeProvider.GetUtcNow(),
                AttemptHistory = stored.Value.AttemptHistory?.Select(value => value.Number == stored.Value.Attempts
                    ? value with { CompletedAt = timeProvider.GetUtcNow(), Status = status } : value).ToArray()
            }, stored.ETag, cancellationToken);
            var setStatus = status switch
            {
                ResourceChangeSetApplicationStatus.Applied => ResourceChangeSetStatus.Applied,
                ResourceChangeSetApplicationStatus.PartiallyApplied => ResourceChangeSetStatus.PartiallyApplied,
                _ => ResourceChangeSetStatus.Failed
            };
            _ = await repository.UpdateAsync(applyingSet.Value with { Status = setStatus }, applyingSet.ETag, cancellationToken);
            if (status == ResourceChangeSetApplicationStatus.Applied)
                await plans.MarkAppliedAsync(scope, plan.Value.Id, actorPrincipalId, cancellationToken);
            if (logger.IsEnabled(LogLevel.Information))
                logger.LogInformation("Resource ChangeSet application {ApplicationId} finished with {Status}", stored.Value.Id, status);
            return stored;
        }
        catch (OperationCanceledException) { throw; }
    }

    private async Task<ResourceChangeApplicationOperation> ApplyOneAsync(ResourcePlanScope scope, ResourceChangeSet changeSet,
        ResourceChangeSetApplication application, ResourceChange change, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        try
        {
            if (change.Proposed.ScopeRef != ResourceScopeRef.Workspace(scope.WorkspaceId.Value))
                throw new ResourceChangeSetApplicationException("resource_change_scope_invalid", "A proposal can only modify resources in its own Workspace.");
            foreach (var dependency in change.DependsOn)
                if (!application.Operations.Any(value => value.LogicalId.Equals(dependency, StringComparison.OrdinalIgnoreCase) && value.Outcome is
                    ResourceChangeApplicationOutcome.Applied or ResourceChangeApplicationOutcome.AlreadyApplied or ResourceChangeApplicationOutcome.Skipped))
                    throw new ResourceChangeSetApplicationException("resource_change_dependency_unapplied", $"Dependency '{dependency}' has not been applied.");
            await CheckBindingsAsync(scope, changeSet, cancellationToken);
            var current = await stateReader.GetAsync(change.Proposed, cancellationToken);
            var retry = application.Attempts > 1;
            if (retry && ((change.Operation == ResourceChangeOperation.Delete && current is null) ||
                (change.Operation != ResourceChangeOperation.Delete && current?.Digest == change.ProposedDigest)))
                return new(change.Order, change.LogicalId, change.Operation, ResourceChangeApplicationOutcome.AlreadyApplied,
                    current?.Uid, current?.Revision, current?.ETag, null, null, now);
            var resume = retry && current is not null && IsUnpublishedIntermediate(change, current);
            if (!resume && (change.Operation == ResourceChangeOperation.Create && current is not null ||
                change.Operation is ResourceChangeOperation.Update or ResourceChangeOperation.Delete &&
                (current is null || change.Current is null || current.ETag != change.Current.ETag || current.Digest != change.Current.Digest)))
                throw new ResourceChangeSetApplicationException("resource_change_stale", "The target resource changed since the proposal was verified.");
            if (change.Operation == ResourceChangeOperation.NoOp)
            {
                if (current?.Digest != change.ProposedDigest)
                    throw new ResourceChangeSetApplicationException("resource_change_stale", "The unchanged resource changed since verification.");
                return new(change.Order, change.LogicalId, change.Operation, ResourceChangeApplicationOutcome.Skipped,
                    current.Uid, current.Revision, current.ETag, null, null, now);
            }
            var validator = validators.SingleOrDefault(value => value.Supports(change.Proposed.Kind))
                ?? throw new ResourceChangeSetApplicationException("resource_change_kind_unsupported", "No validator supports this resource kind.");
            var issues = await validator.ValidateAsync(change, scope, cancellationToken);
            if (issues.Any(value => value.Severity == ResourceChangeSetValidationSeverity.Error))
                throw new ResourceChangeSetApplicationException("resource_change_invalid", issues.First().Message);
            var applier = appliers.SingleOrDefault(value => value.Supports(change.Proposed.Kind))
                ?? throw new ResourceChangeSetApplicationException("resource_change_kind_unsupported", "No application handler supports this resource kind.");
            var permission = change.Operation == ResourceChangeOperation.Delete ? AuthorizationPermissions.ResourcesDelete : AuthorizationPermissions.ResourcesWrite;
            await scopes.WriteAsync(change.Proposed.Kind, change.Proposed.ScopeRef, permission,
                async token => { await applier.ApplyAsync(change, resume, token); return true; }, cancellationToken);
            var result = await stateReader.GetAsync(change.Proposed, cancellationToken);
            if (change.Operation != ResourceChangeOperation.Delete && result?.Digest != change.ProposedDigest)
                throw new ResourceChangeSetApplicationException("resource_change_result_mismatch", "The resulting resource differs from the reviewed proposal.");
            return new(change.Order, change.LogicalId, change.Operation, ResourceChangeApplicationOutcome.Applied,
                result?.Uid, result?.Revision, result?.ETag, null, null, timeProvider.GetUtcNow());
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            var code = exception is ResourceChangeSetApplicationException known ? known.Code : "resource_change_application_failed";
            return new(change.Order, change.LogicalId, change.Operation, ResourceChangeApplicationOutcome.Failed,
                null, null, null, code, exception.Message, timeProvider.GetUtcNow());
        }
    }

    private static bool IsUnpublishedIntermediate(ResourceChange change, CurrentResourceEvidence current)
    {
        if (change.Operation is not (ResourceChangeOperation.Create or ResourceChangeOperation.Update) ||
            change.Proposed.Kind is not ("Flow" or "Entry")) return false;
        var definition = JsonNode.Parse(change.Proposed.Definition.GetRawText())?.AsObject();
        if (definition is null) return false;
        definition["publish"] = false;
        if (change.Proposed.Kind == "Flow") definition["activate"] = false;
        var intermediate = change.Proposed with { Definition = JsonSerializer.SerializeToElement(definition) };
        return current.Digest == ResourcePlanMaterializationService.Digest(intermediate);
    }

    private async Task CheckBindingsAsync(ResourcePlanScope scope, ResourceChangeSet changeSet, CancellationToken cancellationToken)
    {
        foreach (var binding in changeSet.ResolvedBindings ?? [])
        {
            var kind = binding.Field switch
            {
                "modelProfile" => ModelResourceKinds.ModelProfile,
                "runtimeProfile" => RuntimeProfileResourceKinds.RuntimeProfile,
                "tool" => ToolResourceKinds.Tool,
                _ => throw new ResourceChangeSetApplicationException("resource_change_binding_invalid", "An unknown binding type was recorded.")
            };
            var current = await stateReader.ResolveBindingAsync(scope, kind, binding.Reference, cancellationToken);
            if (current is null || current.Digest != binding.Digest || current.ETag != binding.ETag)
                throw new ResourceChangeSetApplicationException("resource_change_binding_stale", "A selected profile or Tool changed since the proposal was verified.");
        }
    }
}
