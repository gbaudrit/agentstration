using System.Security.Cryptography;
using System.Text;
using Agentstration.ResourcePlanning.Contracts;
using Agentstration.ResourcePlanning.Storage.Abstractions;

namespace Agentstration.ResourcePlanning;

public sealed class ResourceChangeSetService(
    ResourcePlanMaterializationService materializer,
    IResourceChangeSetRepository repository,
    TimeProvider timeProvider)
{
    public async Task<ResourceChangeSetSnapshot> CreateAsync(ResourcePlanScope scope, ResourcePlanId planId, Guid actorPrincipalId, CancellationToken cancellationToken)
    {
        if (actorPrincipalId == Guid.Empty) throw new ArgumentException("An actor Principal is required.", nameof(actorPrincipalId));
        var materialization = await materializer.MaterializeAsync(scope, planId, cancellationToken);
        if (!materialization.CanCreateChangeSet)
            throw new ResourcePlanMaterializationException(materialization.Diagnostics);
        var existing = await repository.FindAsync(scope, planId, materialization.PlanRevision, materialization.Digest, cancellationToken);
        if (existing is not null) return existing;
        var changes = materialization.Proposals.Select((proposal, index) => new ResourceChange(
            index,
            proposal.LogicalId,
            proposal.Operation switch
            {
                ResourcePlanProposedOperation.Create => ResourceChangeOperation.Create,
                ResourcePlanProposedOperation.Update => ResourceChangeOperation.Update,
                ResourcePlanProposedOperation.NoOp => ResourceChangeOperation.NoOp,
                _ => throw new ArgumentOutOfRangeException(nameof(proposal))
            },
            proposal.Resource,
            proposal.Current,
            proposal.DependsOn,
            proposal.ProposedDigest)).ToArray();
        var digest = ResourcePlanMaterializationService.Digest(new
        {
            materialization.PlanId,
            materialization.PlanRevision,
            materialization.MaterializerVersion,
            materialization.Digest,
            changes
        });
        var changeSet = new ResourceChangeSet(
            DeterministicId(digest),
            planId,
            materialization.PlanRevision,
            scope,
            materialization.ContractVersion,
            materialization.MaterializerVersion,
            materialization.Digest,
            digest,
            ResourceChangeSetStatus.Proposed,
            changes,
            materialization.Diagnostics,
            actorPrincipalId,
            timeProvider.GetUtcNow());
        try { return await repository.CreateAsync(changeSet, cancellationToken); }
        catch (ResourcePlanConcurrencyException)
        {
            var replay = await repository.FindAsync(scope, planId, materialization.PlanRevision, materialization.Digest, cancellationToken);
            if (replay is null) throw;
            return replay;
        }
    }

    public async Task<ResourceChangeSetSnapshot> GetAsync(ResourcePlanScope scope, ResourceChangeSetId id, CancellationToken cancellationToken) =>
        await repository.GetAsync(scope, id, cancellationToken) ?? throw new ResourceChangeSetNotFoundException(id);

    public Task<ResourceChangeSetPage> ListAsync(ResourcePlanScope scope, ResourcePlanId? planId, int skip, int take, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(skip);
        ArgumentOutOfRangeException.ThrowIfLessThan(take, 1);
        return repository.ListAsync(scope, planId, skip, Math.Min(take, 200), cancellationToken);
    }

    private static ResourceChangeSetId DeterministicId(string digest)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(digest));
        return new(new Guid(hash.AsSpan(0, 16)));
    }
}

public sealed class ResourcePlanMaterializationException(IReadOnlyList<ResourcePlanMaterializationDiagnostic> diagnostics)
    : Exception("The Resource Plan could not be materialized into a reviewable change set.")
{
    public IReadOnlyList<ResourcePlanMaterializationDiagnostic> Diagnostics { get; } = diagnostics;
}
