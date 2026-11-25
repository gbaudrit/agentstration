using System.Text.Json;
using Agentstration.ConnectedServices.Contracts;
using Agentstration.Flows;
using Agentstration.Resources;
using Agentstration.Schemas.Contracts;

namespace Agentstration.DataSources.Contracts;

public enum SourceSampleState { Pending, Running, Ready, Invalid, Failed, Cancelled }
public sealed record SourceSample(string Id, Guid PrincipalId, Guid TenantId, Guid WorkspaceId, ResourceScopeRef Consumer,
    DataSourceOperationAcquisitionBinding Acquisition, SourceSampleState State, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt,
    JsonElement? Value = null, SchemaBindingSnapshot? Contract = null, string? ErrorCode = null)
{
    public Agentstration.Identity.Contracts.AuthorizationRestriction? Restriction { get; init; }
    public IReadOnlyDictionary<string, string>? InputSamples { get; init; }
    public JsonElement? ContextEvidence { get; init; }
    public PreparedDataSourceSampleReference? PreparedInput { get; init; }
    public ConsumedSampleReference? ConsumedInput { get; init; }
    public SchemaBindingSnapshot? ContentContract { get; init; }
    public bool IsRawOperationResponse { get; init; } = true;
    public string? Provenance { get; init; }
}
public sealed record AcquireSourceSampleRequest(DataSourceOperationAcquisitionBinding Acquisition, ResourceNamespace? Namespace = null);
public sealed record ReplaySourceSampleRequest(ResourceNamespace Namespace, IReadOnlyList<FlowCompositionCall> Calls);

public sealed record ConsumedSampleReference(ResourceReference Source, Guid SourceUid, long SourceGeneration,
    string AcquisitionId, Guid AcquisitionUid, ResourceNamespace AcquisitionNamespace, string Sha256, string? AcquisitionTargetName = null);
public sealed record PrepareConsumedSampleRequest(ResourceReference Source, string AcquisitionId, ResourceNamespace AcquisitionNamespace);
public sealed record ConsumedSampleCandidate(string Id, Guid Uid, ResourceNamespace Namespace, DateTimeOffset? CompletedAt);

public interface ISourceAuthoringSamples
{
    Task<SourceSample> GetAsync(string id, CancellationToken ct);
    Task<SourceSample> CapturePreparedAsync(IReadOnlyDictionary<string, string> inputSamples, JsonElement value,
        string draftFingerprint, JsonElement evidence, CancellationToken ct);
}

public sealed class SourceSampleUnavailableException(string code) : Exception("The exact authorized authoring sample is unavailable.")
{
    public string Code { get; } = code;
}

public sealed record PreparedDataSourceSampleReference(ResourceReference Source, Guid SourceUid, long SourceGeneration,
    string RawSampleId, string SequenceFingerprint);
public sealed record PrepareDataSourceSampleRequest(ResourceReference Source, string RawSampleId);
