using System.Text.Json;
using Agentstration.DataSources.Contracts;
using Agentstration.Flows;
using Agentstration.Functions.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.Knowledge.Contracts;
using Agentstration.Resources;

namespace Agentstration.Knowledge;

/// <summary>Prepares the existing standard projection boundary; never starts production work or a Snapshot.</summary>
public sealed class KnowledgePreviewService(ISourceAuthoringSamples samples, ISourceSequenceService sequences,
    IKnowledgeFlowResolver flows, ICurrentRequestContext current, IAuthorizationService authorization)
{
    public async Task<PreparedKnowledgePreview> PrepareAsync(PrepareKnowledgePreviewRequest request, CancellationToken ct)
    {
        var context = current.Current;
        await authorization.EnsurePermissionAsync(context, AuthorizationPermissions.RunsExecute, ct);
        var consumer = current.AccessMode switch
        {
            ControlPlaneAccessMode.Instance => ResourceScopeRef.Instance,
            ControlPlaneAccessMode.TenantAdministration => ResourceScopeRef.Tenant(context.TenantId),
            ControlPlaneAccessMode.Workspace => ResourceScopeRef.Workspace(context.WorkspaceId),
            _ => throw Error("knowledge_preview_context_required")
        };
        var definition = request.Definition;
        if (definition.DataSources.Count is < 1 or > 32 || definition.DataSources.Select(b => b.Name).Distinct(StringComparer.Ordinal).Count() != definition.DataSources.Count
            || request.BindingSamples.Keys.Any(name => !definition.DataSources.Any(b => b.Name == name))) throw Error("knowledge_preview_bindings_invalid");
        var target = definition.ProjectionFlow;
        if (target is null || target.Name != "knowledge-projection-builtin"
            || target.ScopeRef != ResourceScopeRef.Instance || (target.Namespace ?? ResourceNamespace.Default) != ResourceNamespace.Default)
            throw Error("knowledge_preview_composition_unsupported");
        var root = definition.Orchestration is { } custom ? await sequences.ResolvePreviewBoundaryAsync(custom, "projection", consumer, request.Namespace, ct) : null;
        var projection = await flows.ResolveAsync(consumer, request.Namespace, target, ct);
        if (projection.Contract != KnowledgeFlowContracts.Projection) throw Error("knowledge_preview_composition_unsupported");
        var issues = new List<KnowledgePreviewInputIssue>();
        var prepared = new List<JsonElement>();
        var evidence = new List<object>();
        var used = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var binding in definition.DataSources)
        {
            if (!request.BindingSamples.TryGetValue(binding.Name, out var id))
            {
                issues.Add(new(binding.Name, "knowledge_preview_input_missing", binding.Required));
                continue;
            }
            SourceSample sample;
            try { sample = await samples.GetAsync(id, ct); }
            catch (SourceSampleUnavailableException error) { issues.Add(new(binding.Name, error.Code, binding.Required)); continue; }
            var source = sample.ConsumedInput?.Source ?? sample.PreparedInput?.Source;
            if (sample.IsRawOperationResponse || source is null || sample.Value is not { } value || sample.State != SourceSampleState.Ready
                || source.Name != binding.DataSource.Name || source.ScopeRef != binding.DataSource.ScopeRef
                || (source.Namespace ?? ResourceNamespace.Default) != (binding.DataSource.Namespace ?? request.Namespace)
                || sample.ConsumedInput?.AcquisitionTargetName != binding.AcquisitionTarget?.Name) throw Error("knowledge_preview_input_mismatch");
            if (binding.TransformationFlow is not null) throw Error("knowledge_preview_composition_unsupported");
            var replay = await sequences.ReplayAsync(new(consumer, request.Namespace, binding.Transformations, sample.Contract), value, sample.Id, ct);
            if (replay.ErrorCode is { } failure)
            {
                issues.Add(new(binding.Name, failure, binding.Required));
                continue;
            }
            var result = replay.Values[^1];
            if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("artifacts", out var artifacts) || artifacts.ValueKind != JsonValueKind.Array)
                throw Error("knowledge_preview_manifest_invalid");
            prepared.AddRange(artifacts.EnumerateArray().Select(a => a.Clone()));
            if (prepared.Count > 100) throw Error("knowledge_preview_manifest_limit");
            used.Add(binding.Name, id);
            evidence.Add(new { binding.Name, binding.Required, SampleId = id, Input = sample.ConsumedInput, PreparedInput = sample.PreparedInput, sample.Contract, replay.Fingerprint });
        }
        if (issues.Any(i => i.Required) || prepared.Count == 0) throw Error("knowledge_preview_required_input_unavailable");
        var fingerprint = FunctionDigests.Compute(new { request, projection, root });
        var composite = await samples.CapturePreparedAsync(used, JsonSerializer.SerializeToElement(new { artifacts = prepared }),
            fingerprint, JsonSerializer.SerializeToElement(new { inputs = evidence, issues, projection }), ct);
        return new(composite, issues);
    }
    private static KnowledgeSourceValidationException Error(string code) => new(code, "The selected composition or its exact prepared inputs cannot be previewed.");
}
