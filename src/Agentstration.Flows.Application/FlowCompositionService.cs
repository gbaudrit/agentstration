using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentstration.Flows.Storage.Abstractions;
using Agentstration.Resources;

namespace Agentstration.Flows.Application;

public sealed record CompiledFlowComposition(FlowVersion Definition, FlowCompositionReceipt? Receipt);

/// <summary>Expands declared points into ordinary, exact-version governed child calls at admission.</summary>
public sealed class FlowCompositionService(FlowService flows)
{
    public const int MaximumCallsPerPoint = 32;
    public const int MaximumCalls = 128;
    public async Task<CompiledFlowComposition> ResolveAsync(FlowVersion template,
        IReadOnlyDictionary<string, IReadOnlyList<FlowCompositionCall>> bindings, ResourceScopeRef consumer, CancellationToken ct)
    {
        var templateHash = template.DefinitionHash ?? (template.Graph is { } templateGraph ? FlowDefinitionHash.Compute(templateGraph) : FlowDefinitionHash.Compute(template.Definition));
        template = await flows.CompileFunctionsAsync(template, consumer, ct);
        var points = template.Graph?.Steps.OfType<FlowCompositionPointStepDefinition>().ToArray() ?? [];
        if (points.Length == 0)
        {
            if (bindings.Count != 0) throw Error("flow_composition_point_unknown");
            return new(template, (template.DefinitionHash ?? templateHash) == templateHash ? null : new(templateHash, template.DefinitionHash!, new Dictionary<string, IReadOnlyList<FlowCompositionCall>>()));
        }
        if (points.Select(p => p.Point).Distinct(StringComparer.Ordinal).Count() != points.Length
            || bindings.Keys.Any(name => !points.Any(point => point.Point == name))) throw Error("flow_composition_point_unknown");
        var selected = new Dictionary<string, IReadOnlyList<FlowCompositionCall>>(StringComparer.Ordinal);
        var replacements = new Dictionary<string, IReadOnlyList<FlowStepDefinition>>(StringComparer.Ordinal);
        var addedTransitions = new List<FlowTransitionDefinition>();
        var total = 0;
        foreach (var point in points)
        {
            var configured = bindings.GetValueOrDefault(point.Point) ?? [];
            if (configured.Count > MaximumCallsPerPoint || (total += configured.Count) > MaximumCalls) throw Error("flow_composition_limit");
            if (configured.Count == 0 && (!point.AllowEmpty || !FlowCompositionSchema.IsAssignable(point.InputSchema, point.OutputSchema)))
                throw Error("flow_composition_binding_required");
            var calls = new List<FlowCompositionCall>();
            var nodes = new List<FlowStepDefinition>();
            var previousSchema = point.InputSchema;
            var mapping = point.InputMapping ?? JsonSerializer.SerializeToElement("${transition.output}");
            foreach (var call in configured)
            {
                if (call.Parameters.ValueKind != JsonValueKind.Object || call.Parameters.GetRawText().Length > FlowCompositionSchema.MaximumParametersLength) throw Error("flow_composition_parameters_invalid");
                if (call.Function is { } functionReference)
                {
                    if (call.Flow is not null || call.PassInputDirectly || call.Parameters.TryGetProperty("value", out _)) throw Error("flow_composition_function_ambiguous");
                    var function = await flows.ResolveFunctionAsync(functionReference with { Namespace = functionReference.Namespace ?? template.FlowId.Namespace }, consumer, ct);
                    JsonElement arguments;
                    JsonElement? functionParameters = null;
                    if (function.InputSchema.TryGetProperty("properties", out var functionProperties) && functionProperties.TryGetProperty("value", out var functionValue))
                    {
                        if (!FunctionValueCompatible(previousSchema, functionValue)) throw Error("flow_composition_contract_incompatible");
                        FlowCompositionSchema.ValidateValue(Agentstration.Functions.Contracts.FunctionInvocationSchemas.Parameters(function.InputSchema), call.Parameters);
                        arguments = JsonSerializer.SerializeToElement(new { value = mapping.Clone() });
                        functionParameters = call.Parameters.Clone();
                    }
                    else
                    {
                        if (call.Parameters.EnumerateObject().Any() || !FunctionValueCompatible(previousSchema, function.InputSchema)) throw Error("flow_composition_contract_incompatible");
                        arguments = mapping.Clone();
                    }
                    var functionName = $"cp-{Hash(point.Name)[..12]}-{calls.Count + 1:D2}";
                    if (template.Graph!.Steps.Any(step => step.Name == functionName)) throw Error("flow_composition_step_conflict");
                    calls.Add(call with { Function = function.Reference, Parameters = call.Parameters.Clone() });
                    nodes.Add(FlowService.FunctionStep(functionName, function, arguments, point.DisplayName, parameters: functionParameters));
                    addedTransitions.Add(new($"{functionName}-next", functionName, "success", point.Name));
                    if (nodes.Count > 1)
                    {
                        var priorName = nodes[^2].Name;
                        var priorIndex = addedTransitions.FindIndex(t => t.FromStep == priorName);
                        addedTransitions[priorIndex] = addedTransitions[priorIndex] with { ToStep = functionName };
                    }
                    mapping = JsonSerializer.SerializeToElement("${steps." + functionName + ".output}");
                    previousSchema = function.OutputSchema;
                    continue;
                }
                var reference = call.Flow ?? throw Error("flow_composition_target_required");
                var child = await ResolveCallAsync(reference, consumer, template.FlowId.Namespace, ct);
                await RejectCyclesAsync(child, consumer, [Key(template)], 0, ct);
                if (child.Graph?.Steps.OfType<FlowCompositionPointStepDefinition>().Any(p => !p.AllowEmpty) == true)
                    throw Error("flow_composition_child_binding_required");
                var inputSchema = child.Graph?.ResolveInputSchema() ?? throw Error("flow_composition_contract_required");
                JsonElement input;
                if (call.PassInputDirectly)
                {
                    if (call.Parameters.EnumerateObject().Any()) throw Error("flow_composition_parameters_invalid");
                    if (!previousSchema.TryGetProperty("type", out var sourceType) || !inputSchema.TryGetProperty("type", out var targetType)
                        || !JsonElement.DeepEquals(sourceType, targetType)) throw Error("flow_composition_contract_incompatible");
                    input = mapping.Clone();
                }
                else
                {
                    if (!inputSchema.TryGetProperty("properties", out var properties) || !properties.TryGetProperty("value", out var value)
                        || !properties.TryGetProperty("parameters", out var parameters)
                        || !FlowCompositionSchema.IsAssignable(previousSchema, value)) throw Error("flow_composition_contract_incompatible");
                    FlowCompositionSchema.ValidateValue(parameters, call.Parameters);
                    input = mapping.Clone();
                }
                var outputs = child.Graph!.GetOutputs().Where(output => output.Outcome == FlowOutputOutcome.Success).ToArray();
                if (outputs.Length != 1 || outputs[0].Schema is not { } outputSchema) throw Error("flow_composition_output_ambiguous");
                var resource = await flows.GetExactAsync(child.ScopeRef, child.FlowId, ct) ?? throw Error("flow_composition_child_unavailable");
                var exact = call with
                {
                    Flow = reference with
                    {
                        VersionStrategy = FlowCallVersionStrategy.Exact,
                        Version = child.Version,
                        Namespace = child.FlowId.Namespace,
                        ScopeRef = child.ScopeRef,
                        ResourceUid = resource.Value.Uid,
                        DefinitionHash = child.DefinitionHash ?? FlowDefinitionHash.Compute(child.Graph)
                    },
                    Parameters = call.Parameters.Clone()
                };
                calls.Add(exact);
                var name = $"cp-{Hash(point.Name)[..12]}-{calls.Count:D2}";
                if (template.Graph!.Steps.Any(step => step.Name == name)) throw Error("flow_composition_step_conflict");
                nodes.Add(new FlowCallStepDefinition
                {
                    Name = name,
                    DisplayName = $"{point.DisplayName ?? point.Point} {calls.Count}",
                    Flow = exact.Flow,
                    InputMapping = input,
                    CompositionPoint = point.Point,
                    CompositionParameters = call.PassInputDirectly ? null : call.Parameters.Clone()
                });
                var eventName = child.Graph.Steps.OfType<OutputFlowStepDefinition>().Single(output => output.Name == outputs[0].Name).Outcome.HasValue
                    ? outputs[0].Name : "completed";
                addedTransitions.Add(new($"{name}-next", name, eventName, point.Name));
                if (nodes.Count > 1)
                {
                    var prior = nodes[^2].Name;
                    var index = addedTransitions.FindIndex(transition => transition.FromStep == prior);
                    addedTransitions[index] = addedTransitions[index] with { ToStep = name };
                }
                mapping = JsonSerializer.SerializeToElement("${steps." + name + ".output}");
                previousSchema = outputSchema;
            }
            if (!FlowCompositionSchema.IsAssignable(previousSchema, point.OutputSchema)) throw Error("flow_composition_contract_incompatible");
            nodes.Add(new TransformFlowStepDefinition
            {
                Name = point.Name,
                DisplayName = point.DisplayName,
                Description = point.Description,
                ArtifactOutput = point.ArtifactOutput,
                Mapping = mapping.Clone(),
                Schema = point.OutputSchema.Clone()
            });
            replacements.Add(point.Name, nodes);
            selected.Add(point.Point, calls);
        }
        var graph = template.Graph! with
        {
            Steps = template.Graph!.Steps.SelectMany(step => replacements.TryGetValue(step.Name, out var nodes) ? nodes : [step]).ToArray(),
            EntryStep = replacements.TryGetValue(template.Graph.EntryStep, out var entry) ? entry[0].Name : template.Graph.EntryStep,
            Transitions = template.Graph.Transitions.Select(transition => replacements.TryGetValue(transition.ToStep, out var nodes)
                ? transition with { ToStep = nodes[0].Name } : transition).Concat(addedTransitions).ToArray()
        };
        var hash = FlowDefinitionHash.Compute(graph);
        return new(template with { Graph = graph, DefinitionHash = hash },
            new(templateHash, hash, selected));
    }
    // Function value constraints are checked on the actual value by the authority. Check known structure here.
    private static bool FunctionValueCompatible(JsonElement output, JsonElement input)
    {
        if (!input.EnumerateObject().Any()) return true;
        if (input.TryGetProperty("anyOf", out var alternatives)) return alternatives.EnumerateArray().Any(candidate => FunctionValueCompatible(output, candidate));
        if (!output.TryGetProperty("type", out var actual) || !input.TryGetProperty("type", out var expected)) return false;
        if (!JsonElement.DeepEquals(actual, expected)) return actual.GetString() == "integer" && expected.GetString() == "number";
        if (expected.GetString() == "array" && input.TryGetProperty("items", out var items))
            return output.TryGetProperty("items", out var produced) && FunctionValueCompatible(produced, items);
        if (expected.GetString() != "object") return true;
        var guaranteed = output.TryGetProperty("required", out var requiredOutput) ? requiredOutput.EnumerateArray().Select(value => value.GetString()).ToHashSet(StringComparer.Ordinal) : [];
        if (input.TryGetProperty("required", out var requiredInput) && requiredInput.EnumerateArray().Any(value => !guaranteed.Contains(value.GetString()))) return false;
        if (input.TryGetProperty("properties", out var properties) && output.TryGetProperty("properties", out var available))
            foreach (var property in properties.EnumerateObject())
                if (available.TryGetProperty(property.Name, out var source) && !FunctionValueCompatible(source, property.Value)) return false;
        return true;
    }

    public async Task<FlowVersion> ResolveCallAsync(FlowCallReference reference, ResourceScopeRef consumer,
        ResourceNamespace ownerNamespace, CancellationToken ct)
    {
        if (reference.VersionStrategy == FlowCallVersionStrategy.Active && !string.IsNullOrWhiteSpace(reference.Version)
            || reference.VersionStrategy == FlowCallVersionStrategy.Exact && string.IsNullOrWhiteSpace(reference.Version))
            throw Error("flow_composition_reference_invalid");
        var id = reference.Resolve(ownerNamespace);
        FlowVersion resolved;
        try
        {
            resolved = await flows.ResolveAsync(consumer, new(id, reference.VersionStrategy == FlowCallVersionStrategy.Exact ? reference.Version : null,
                reference.VersionStrategy == FlowCallVersionStrategy.Active, id.Namespace, reference.ScopeRef), ownerNamespace, ct);
        }
        catch (Exception exception) when (exception is FlowReferenceOutsideScopeException or FlowReferenceAmbiguousException or FlowNotFoundException)
        { throw Error("flow_composition_child_unavailable"); }
        var resource = await flows.GetExactAsync(resolved.ScopeRef, id, ct);
        if (resource?.Value.Enabled != true) throw Error("flow_composition_child_unavailable");
        await flows.ValidateUseAsync(resolved.ScopeRef, id, resolved.Version, consumer, reference.ResourceUid, ct);
        if (reference.DefinitionHash is { } expected && expected != resolved.DefinitionHash) throw Error("flow_composition_child_changed");
        return resolved;
    }
    private async Task RejectCyclesAsync(FlowVersion version, ResourceScopeRef consumer, HashSet<string> path, int depth, CancellationToken ct)
    {
        if (depth > 8 || !path.Add(Key(version))) throw Error("flow_composition_cycle");
        try
        {
            foreach (var reference in version.Graph?.Steps.Select(step => step switch
            { FlowCallStepDefinition call => call.Flow, RepeatFlowStepDefinition repeat => repeat.Flow, _ => null }).Where(value => value is not null) ?? [])
                await RejectCyclesAsync(await ResolveCallAsync(reference!, consumer, version.FlowId.Namespace, ct), consumer, path, depth + 1, ct);
        }
        finally { path.Remove(Key(version)); }
    }
    private static string Key(FlowVersion flow) => $"{flow.ScopeRef.Value}/{flow.FlowId}/{flow.Version}";
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static FlowValidationException Error(string code) => new(code, "The configured Flow composition is unavailable, incompatible or exceeds its limits.");
}
