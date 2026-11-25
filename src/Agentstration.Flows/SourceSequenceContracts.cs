using System.Text.Json;
using Agentstration.Resources;
using Agentstration.Schemas;
using Agentstration.Schemas.Contracts;

namespace Agentstration.Flows;

public enum SequenceCompatibility { Undetermined, Verified, Incompatible }
public sealed record SequenceStepContract(int Position, SchemaBindingSnapshot? Input, SchemaBindingSnapshot? Output,
    SequenceCompatibility Compatibility, IReadOnlyList<JsonCollectionContract> Collections, string? Diagnostic = null)
{
    public bool Unavailable { get; init; }
}
public sealed record SourceSequenceAnalysis(IReadOnlyList<FlowCompositionCall> Calls, IReadOnlyList<SequenceStepContract> Steps)
{
    public SchemaBindingSnapshot? Input { get; init; }
}
public sealed record SourceSequenceRequest(ResourceScopeRef Consumer, ResourceNamespace Namespace,
    IReadOnlyList<FlowCompositionCall> Calls, SchemaBindingSnapshot? Input = null, bool AcquisitionContent = false, SchemaBindingSnapshot? ContentInput = null, SchemaBinding? InputBinding = null);
public sealed record SourceSequencePreview(string Fingerprint, IReadOnlyList<JsonElement> Values, string? ErrorCode = null,
    int? FailedPosition = null);

public interface ISourceSequenceService
{
    Task<FlowCallReference> ResolvePreviewBoundaryAsync(FlowCallReference root, string boundary, ResourceScopeRef consumer, ResourceNamespace ns, CancellationToken ct);
    Task<SourceSequenceAnalysis> AnalyzeAsync(SourceSequenceRequest request, CancellationToken ct);
    Task<SourceSequencePreview> ReplayAsync(SourceSequenceRequest request, JsonElement sample, string sampleId, CancellationToken ct);
}
