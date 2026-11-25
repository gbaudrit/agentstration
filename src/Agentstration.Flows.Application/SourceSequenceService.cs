using System.Text.Json;
using Agentstration.Functions;
using Agentstration.Functions.Contracts;
using Agentstration.Schemas;
using Agentstration.Schemas.Contracts;

namespace Agentstration.Flows.Application;

/// <summary>Authoring uses the same exact function resolver, schema snapshots and engines as admission.</summary>
public sealed class SourceSequenceService(FlowService flows, TransformationFunctionService functions, ISchemaBindingResolver schemaBindings) : ISourceSequenceService
{
    public const int MaximumPreviewBytes = 8 * 1024 * 1024;
    private readonly SemaphoreSlim previewSlots = new(2, 2);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public async Task<FlowCallReference> ResolvePreviewBoundaryAsync(FlowCallReference root, string boundary,
        Agentstration.Resources.ResourceScopeRef consumer, Agentstration.Resources.ResourceNamespace ns, CancellationToken ct)
    {
        var resolved = await new FlowCompositionService(flows).ResolveCallAsync(root, consumer, ns, ct);
        var points = resolved.Graph?.Steps.OfType<FlowCompositionPointStepDefinition>().ToArray() ?? [];
        if (points.SingleOrDefault(p => p.Point == boundary) is not { AllowEmpty: false, InputMapping: { ValueKind: JsonValueKind.String } input } boundaryPoint
            || input.GetString() != "${input}" || !points.Any(p => p.Point != boundary && p.InputMapping is { ValueKind: JsonValueKind.String } mapping
                && mapping.GetString() == "${steps." + boundaryPoint.Name + ".output}"))
            throw new FlowValidationException("preview_composition_unsupported", "This composition point has no directly inspectable governed input boundary.");
        return root with
        {
            VersionStrategy = FlowCallVersionStrategy.Exact,
            Version = resolved.Version,
            ScopeRef = resolved.ScopeRef,
            Namespace = resolved.FlowId.Namespace,
            DefinitionHash = resolved.DefinitionHash
        };
    }
    public async Task<SourceSequenceAnalysis> AnalyzeAsync(SourceSequenceRequest request, CancellationToken ct)
    {
        if (request.Calls.Count > FlowCompositionService.MaximumCallsPerPoint) throw new FlowValidationException("flow_composition_limit", "The sequence exceeds its bounded limit.");
        if (request.Calls.Any(call => call.Parameters.ValueKind != JsonValueKind.Object || call.Parameters.GetRawText().Length > FlowCompositionSchema.MaximumParametersLength)
            || request.Input?.Content.GetRawText().Length > DataContracts.MaximumSchemaCharacters
            || request.ContentInput?.Content.GetRawText().Length > DataContracts.MaximumSchemaCharacters)
            throw new FlowValidationException("sequence_authoring_limit", "The sequence contracts or parameters exceed the bounded authoring profile.");
        var calls = new List<FlowCompositionCall>();
        var steps = new List<SequenceStepContract>();
        var previous = request.InputBinding is { } binding ? await schemaBindings.ResolveAsync("$.sequence.input", binding,
            new(request.Consumer, request.Namespace, SchemaFormatIds.JsonSchema), ct) : request.Input;
        var initialContract = previous;
        foreach (var authored in request.Calls)
        {
            ct.ThrowIfCancellationRequested();
            var call = authored;
            var input = previous;
            SchemaBindingSnapshot? output = null;
            var compatibility = SequenceCompatibility.Undetermined;
            var collections = previous is null ? new JsonCollectionCandidates([]) : JsonCollectionContracts.Discover(previous);
            string? diagnostic = collections.Diagnostic;
            var unavailable = false;
            try
            {
                if (call.Function is { } reference)
                {
                    var function = await flows.ResolveFunctionAsync(reference, request.Consumer, ct);
                    var expected = function.InputSchema.TryGetProperty("properties", out var functionProperties) && functionProperties.TryGetProperty("value", out var valueContract) ? valueContract : function.InputSchema;
                    if (calls.Count == 0 && request.AcquisitionContent && FlowCompositionInputs.ExpectsText(function.InputSchema)
                        && previous is { Format: SchemaFormatIds.JsonSchema } && FlowCompositionInputs.IsTextResponse(previous.Content))
                    {
                        call = call with { FunctionInputSource = FlowCompositionFunctionInputSource.AcquisitionContent };
                        input = request.ContentInput ?? Inline(previous.Content.GetProperty("properties").GetProperty("content"), "operation.content");
                    }
                    compatibility = function.Transformation is { } mapping && input is { Format: BuiltinXmlSchemaFormatHandler.Format }
                        ? input.DefinitionHash == mapping.Input.DefinitionHash ? SequenceCompatibility.Verified : SequenceCompatibility.Undetermined
                        : Compatibility(input?.Content, expected);
                    if (function.ImplementationId == "json.explode" && call.Parameters.TryGetProperty("path", out var selected)
                        && selected.ValueKind == JsonValueKind.String && collections.Collections.SingleOrDefault(item => item.DataPath == selected.GetString()) is { } collection)
                    {
                        var parameters = call.Parameters.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
                        // An authored manual binding remains deliberate. Only fill an absent item binding.
                        if (!parameters.ContainsKey("itemSchema")) parameters["itemSchema"] = JsonSerializer.SerializeToElement(collection.Binding, Json);
                        call = call with
                        {
                            Parameters = JsonSerializer.SerializeToElement(parameters),
                            DerivedItemSchemaParentHash = authored.DerivedItemSchemaParentHash ?? (!authored.Parameters.TryGetProperty("itemSchema", out _) ? previous?.DefinitionHash : null)
                        };
                    }
                    if (call.DerivedItemSchemaParentHash is { } derived && previous?.DefinitionHash != derived)
                    {
                        compatibility = SequenceCompatibility.Undetermined;
                        diagnostic = "sequence_derived_contract_stale";
                    }
                    var snapshots = await flows.ResolveFunctionSchemasAsync(function.InputSchema, call.Parameters, request.Consumer,
                        request.Namespace, $"$.sequence[{calls.Count}]", ct);
                    call = call with { Function = function.Reference, FunctionSchemaSnapshots = snapshots };
                    output = function.Transformation?.Output ?? Inline(function.OutputSchema, $"function:{function.Reference.Name}@{function.Reference.Version}");
                    if (function.ImplementationId == "json.explode" && snapshots?.TryGetValue("itemSchema", out var item) == true)
                        output = Inline(JsonSerializer.SerializeToElement(new
                        {
                            type = "object",
                            properties = new
                            {
                                value = new { type = "array", items = item.Content },
                                recordContract = new { type = "object" },
                                selectionPath = new { type = "string" }
                            }
                        }), "json.explode/record-projection");
                }
                else if (call.Flow is { } flow)
                {
                    var resolved = await new FlowCompositionService(flows).ResolveCallAsync(flow, request.Consumer, request.Namespace, ct);
                    var schema = resolved.Graph?.ResolveInputSchema();
                    JsonElement? expected = schema is { } value && value.TryGetProperty("properties", out var props) && props.TryGetProperty("value", out var child) ? child : null;
                    compatibility = Compatibility(input?.Content, expected);
                    var produced = resolved.Graph?.ResolveSingleSuccessOutputSchema(out _);
                    output = produced is { } declared ? OutputContract(resolved, declared) : null;
                    diagnostic = "preview_flow_unsupported";
                }
                else diagnostic = "flow_composition_target_required";
            }
            catch (FlowValidationException error) { diagnostic = error.Code; output = null; unavailable = true; }
            catch (Agentstration.Schemas.Contracts.SchemaBindingResolutionException error) { diagnostic = error.Code; output = null; unavailable = true; }
            catch (DataContractException error) { diagnostic = error.Code; output = null; unavailable = true; }
            steps.Add(new(calls.Count + 1, input, output, compatibility, collections.Collections, diagnostic) { Unavailable = unavailable });
            calls.Add(call);
            previous = output;
        }
        return new(calls, steps) { Input = initialContract };
    }

    public async Task<SourceSequencePreview> ReplayAsync(SourceSequenceRequest request, JsonElement sample, string sampleId, CancellationToken ct)
    {
        if (!await previewSlots.WaitAsync(0, ct)) return new(Fingerprint(request, sampleId), [sample.Clone()], "preview_capacity");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        try { return await ReplayCoreAsync(request, sample, sampleId, deadline.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new(Fingerprint(request, sampleId), [sample.Clone()], "preview_timeout"); }
        finally { previewSlots.Release(); }
    }

    private async Task<SourceSequencePreview> ReplayCoreAsync(SourceSequenceRequest request, JsonElement sample, string sampleId, CancellationToken ct)
    {
        var fingerprint = Fingerprint(request, sampleId);
        var values = new List<JsonElement> { sample.Clone() };
        var totalBytes = JsonSerializer.SerializeToUtf8Bytes(sample).Length;
        SourceSequenceAnalysis analysis;
        try { analysis = await AnalyzeAsync(request, ct); }
        catch (FlowValidationException error) { return new(fingerprint, values, error.Code, 1); }
        for (var index = 0; index < analysis.Calls.Count; index++)
        {
            var call = analysis.Calls[index];
            if (analysis.Steps[index].Unavailable) return new(fingerprint, values, analysis.Steps[index].Diagnostic, index + 1);
            if (analysis.Steps[index].Diagnostic == "sequence_derived_contract_stale") return new(fingerprint, values, "sequence_derived_contract_stale", index + 1);
            if (call.Function is null) return new(fingerprint, values, "preview_flow_unsupported", index + 1);
            try
            {
                var function = await functions.ResolveAsync(call.Function, request.Consumer, ct);
                // Only compiled pure JSON/XML engines. Artifact functions have separate publication/governance effects.
                if (function.ImplementationVersion != "1.0.0" || function.ImplementationId is not ("json.select" or "json.project" or "collection.filter" or "collection.distinct" or "json.explode" or "xml.to_json"))
                    return new(fingerprint, values, "preview_function_unsupported", index + 1);
                var value = values[^1];
                if (call.FunctionInputSource == FlowCompositionFunctionInputSource.AcquisitionContent) value = value.GetProperty("content");
                var input = call.Parameters.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
                input.Add("value", value);
                var output = await functions.InvokeAsync(function, request.Consumer, JsonSerializer.SerializeToElement(input), ct,
                    "preview-" + sampleId + "/" + index, call.FunctionSchemaSnapshots);
                totalBytes += JsonSerializer.SerializeToUtf8Bytes(output).Length;
                if (totalBytes > MaximumPreviewBytes) return new(fingerprint, values, "preview_payload_limit", index + 1);
                values.Add(output);
            }
            catch (Agentstration.Functions.Abstractions.TransformationFunctionException error) { return new(fingerprint, values, error.Code, index + 1); }
            catch (DataContractException) { return new(fingerprint, values, "preview_contract_invalid", index + 1); }
        }
        return new(fingerprint, values);
    }

    public static SchemaBindingSnapshot OutputContract(FlowVersion version, JsonElement schema)
    {
        var output = version.Graph?.GetOutputs().SingleOrDefault(o => o.Outcome == FlowOutputOutcome.Success);
        var snapshot = output is null ? null : version.SchemaBindings?.GetValueOrDefault("$.steps[" + output.Name + "].schema");
        snapshot ??= version.SchemaBindings?.GetValueOrDefault("$.outputSchema");
        return snapshot is not null && JsonElement.DeepEquals(JsonSchemaSelection.Select(snapshot.Content), schema) ? snapshot
            : Inline(schema, $"flow:{version.FlowId}@{version.Version}/{version.DefinitionHash}");
    }
    public static string Fingerprint(SourceSequenceRequest request, string sampleId) => FunctionDigests.Compute(new { sampleId, request });
    public static SchemaBindingSnapshot Inline(JsonElement schema, string origin) => new(origin, SchemaFormatIds.JsonSchema,
        BuiltinJsonSchemaFormatHandler.Draft202012, schema.Clone(), JsonSchemaSelection.DefinitionHash(SchemaFormatIds.JsonSchema, BuiltinJsonSchemaFormatHandler.Draft202012, schema));
    public static SequenceCompatibility Compatibility(JsonElement? producer, JsonElement? consumer)
    {
        if (producer is not { ValueKind: JsonValueKind.Object } actual || consumer is not { ValueKind: JsonValueKind.Object } expected
            || !actual.TryGetProperty("type", out var sourceType) || !expected.TryGetProperty("type", out var targetType)) return SequenceCompatibility.Undetermined;
        if (FlowCompositionSchema.IsSupported(actual) && FlowCompositionSchema.IsSupported(expected)
            && FlowCompositionSchema.IsAssignable(actual, expected)) return SequenceCompatibility.Verified;
        if (sourceType.ValueKind == JsonValueKind.String && targetType.ValueKind == JsonValueKind.String
            && sourceType.GetString() != targetType.GetString() && !(sourceType.GetString() == "integer" && targetType.GetString() == "number")) return SequenceCompatibility.Incompatible;
        return SequenceCompatibility.Undetermined;
    }
}
