using Agentstration.Resources;
using Agentstration.SystemGraph.Contracts;

namespace Agentstration.Reconciliation.Contracts;

public sealed record ReconciliationPreviewRequest(string From, string To, IReadOnlyList<string> Consumers,
    ResourceNamespace? Namespace = null, IReadOnlyDictionary<string, string>? TargetVersions = null);
public sealed record ReconciliationPreviewStep(string Consumer, SystemGraphIdentity Identity, string Action,
    string Status, string Reason, string? ProposedVersion, IReadOnlyList<SystemGraphRelation> BeforeBindings,
    IReadOnlyList<SystemGraphRelation> AfterBindings)
{
    public SystemGraphNode? AuthoringSource { get; init; }
}
public sealed record ReconciliationPreview(string Digest, DateTimeOffset ObservedAt, string ChangeAssessment,
    IReadOnlyList<SystemGraphNode> ReadSet, IReadOnlyList<SystemGraphRelation> Relations,
    IReadOnlyList<ReconciliationPreviewStep> Steps, IReadOnlyList<string> Blockers, bool CanExecute,
    bool GloballyComplete);
public sealed record ReconciliationRevalidationRequest(ReconciliationPreviewRequest Request, string ExpectedDigest);
public interface IReconciliationPreviewService
{
    Task<ReconciliationPreview> PreviewAsync(ReconciliationPreviewRequest request, CancellationToken ct);
    Task<ReconciliationPreview> RevalidateAsync(ReconciliationRevalidationRequest request, CancellationToken ct);
}
