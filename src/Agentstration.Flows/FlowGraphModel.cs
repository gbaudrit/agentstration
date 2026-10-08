using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Agentstration.Flows;

using Agentstration.Resources;

public sealed record FlowResourceReference(string ResourceId, string VersionStrategy = "UseDeploymentVersion", long? Version = null, ResourceNamespace? Namespace = null);
public sealed record FlowNodePosition(double X, double Y);
public sealed record FlowViewportMetadata(double X, double Y, double Zoom = 1);
public sealed record FlowDesignerMetadata
{
    public IReadOnlyDictionary<string, FlowNodePosition> NodePositions { get; init; } = new Dictionary<string, FlowNodePosition>();
    public string? PreferredLayout { get; init; } = "Horizontal";
    public FlowViewportMetadata? Viewport { get; init; }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(InputFlowStepDefinition), "input")]
[JsonDerivedType(typeof(AgentFlowStepDefinition), "agent")]
[JsonDerivedType(typeof(RouterFlowStepDefinition), "router")]
[JsonDerivedType(typeof(ConditionFlowStepDefinition), "condition")]
[JsonDerivedType(typeof(TransformFlowStepDefinition), "transform")]
[JsonDerivedType(typeof(FlowCallStepDefinition), "flow")]
[JsonDerivedType(typeof(RepeatFlowStepDefinition), "repeat")]
[JsonDerivedType(typeof(ToolFlowStepDefinition), "tool")]
[JsonDerivedType(typeof(ToolRouteFlowStepDefinition), "toolRoute")]
[JsonDerivedType(typeof(OutputFlowStepDefinition), "output")]
[JsonDerivedType(typeof(FailureFlowStepDefinition), "failure")]
public abstract record FlowStepDefinition
{
    public required string Name { get; init; }
    public string? DisplayName { get; init; }
    public string? Description { get; init; }
    public FlowStepArtifactOutputDefinition? ArtifactOutput { get; init; }
}

public sealed record FlowStepArtifactOutputDefinition
{
    public string? FileName { get; init; }
    public string MediaType { get; init; } = "application/json";
    public FlowStepArtifactContentEncoding ContentEncoding { get; init; }
    public JsonElement? ContentMapping { get; init; }
    public long? MaximumBytes { get; init; }
    public ResourceReference? StagingBinding { get; init; }
    public FlowCallReference? StorageFlow { get; init; }
    public FlowStepArtifactCleanupMode Clean { get; init; }
}

[JsonConverter(typeof(FlowStepArtifactCleanupModeJsonConverter))]
public enum FlowStepArtifactCleanupMode
{
    Auto,
    Always,
    Never
}

public sealed class FlowStepArtifactCleanupModeJsonConverter : JsonConverter<FlowStepArtifactCleanupMode>
{
    public override FlowStepArtifactCleanupMode Read(ref Utf8JsonReader reader, Type typeToConvert,
        JsonSerializerOptions options) => reader.TokenType switch
    {
        JsonTokenType.True => FlowStepArtifactCleanupMode.Always,
        JsonTokenType.False => FlowStepArtifactCleanupMode.Never,
        JsonTokenType.String when reader.GetString()?.Equals("auto", StringComparison.OrdinalIgnoreCase) == true =>
            FlowStepArtifactCleanupMode.Auto,
        JsonTokenType.String when bool.TryParse(reader.GetString(), out var clean) =>
            clean ? FlowStepArtifactCleanupMode.Always : FlowStepArtifactCleanupMode.Never,
        _ => throw new JsonException("Artifact output clean must be 'auto', true, or false.")
    };

    public override void Write(Utf8JsonWriter writer, FlowStepArtifactCleanupMode value,
        JsonSerializerOptions options)
    {
        if (value == FlowStepArtifactCleanupMode.Auto) writer.WriteStringValue("auto");
        else writer.WriteBooleanValue(value == FlowStepArtifactCleanupMode.Always);
    }
}

[JsonConverter(typeof(JsonStringEnumConverter<FlowStepArtifactContentEncoding>))]
public enum FlowStepArtifactContentEncoding
{
    [JsonStringEnumMemberName("auto")]
    Auto,
    [JsonStringEnumMemberName("json")]
    Json,
    [JsonStringEnumMemberName("utf8")]
    Utf8,
    [JsonStringEnumMemberName("base64")]
    Base64
}

public sealed record InputFlowStepDefinition : FlowStepDefinition
{
    public JsonElement? Schema { get; init; }
}

public sealed record AgentFlowStepDefinition : FlowStepDefinition
{
    public required FlowResourceReference Agent { get; init; }
    public JsonElement? InputMapping { get; init; }
    public FlowResourceReference? ModelProfileOverride { get; init; }
    public string? AdditionalInstructions { get; init; }
    public int? TimeoutSeconds { get; init; }
}

public sealed record FlowRouterCandidate(string Route, FlowResourceReference Agent, string? Description = null, IReadOnlyList<string>? Examples = null);
public sealed record RouterFlowStepDefinition : FlowStepDefinition
{
    public string Strategy { get; init; } = "Rules";
    public IReadOnlyList<FlowRouterCandidate> Candidates { get; init; } = [];
    public string? SelectionInstructions { get; init; }
    public double? MinimumConfidence { get; init; }
    public FlowResourceReference? Fallback { get; init; }
}

public sealed record ConditionFlowStepDefinition : FlowStepDefinition
{
    public string Mode { get; init; } = "Simple";
    public string? Left { get; init; }
    public string Operator { get; init; } = "equals";
    public string? Right { get; init; }
    public string? Expression { get; init; }
}

public sealed record TransformFlowStepDefinition : FlowStepDefinition
{
    public string Mode { get; init; } = "Mapping";
    public JsonElement? Mapping { get; init; }
    public string? Expression { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter<FlowCallVersionStrategy>))]
public enum FlowCallVersionStrategy
{
    [JsonStringEnumMemberName("active")]
    Active,
    [JsonStringEnumMemberName("exact")]
    Exact
}

public sealed record FlowCallReference(
    string ResourceId,
    FlowCallVersionStrategy VersionStrategy = FlowCallVersionStrategy.Active,
    string? Version = null,
    ResourceNamespace? Namespace = null)
{
    public FlowId Resolve(ResourceNamespace ownerNamespace) => new(ResourceId, Namespace ?? ownerNamespace);
}

public sealed record FlowCallStepDefinition : FlowStepDefinition
{
    public required FlowCallReference Flow { get; init; }
    public JsonElement? InputMapping { get; init; }
}

public sealed record RepeatFlowStepDefinition : FlowStepDefinition
{
    public required FlowCallReference Flow { get; init; }
    public JsonElement? InputMapping { get; init; }
    public JsonElement? NextInputMapping { get; init; }
    public required string Until { get; init; }
    public int MaximumIterations { get; init; } = 100;
}

public sealed record FlowToolReference(
    string ResourceId,
    ResourceNamespace? Namespace = null)
{
    public ResourceNamespace ResolveNamespace(ResourceNamespace ownerNamespace) => Namespace ?? ownerNamespace;
}

public sealed record ToolFlowStepDefinition : FlowStepDefinition
{
    public required FlowToolReference Tool { get; init; }
    public JsonElement? ArgumentsMapping { get; init; }
}

public sealed record FlowToolSetReference(
    string ResourceId,
    string Version,
    ResourceNamespace? Namespace = null)
{
    public ResourceNamespace ResolveNamespace(ResourceNamespace ownerNamespace) => Namespace ?? ownerNamespace;
}

public sealed record ToolRouteFlowStepDefinition : FlowStepDefinition
{
    public required FlowToolSetReference ToolSet { get; init; }
    public required string Capability { get; init; }
    public string? Route { get; init; }
    public JsonElement? ArgumentsMapping { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter<FlowOutputOutcome>))]
public enum FlowOutputOutcome
{
    [JsonStringEnumMemberName("success")]
    Success,
    [JsonStringEnumMemberName("error")]
    Error
}

public sealed record OutputFlowStepDefinition : FlowStepDefinition
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public FlowOutputOutcome? Outcome { get; init; }
    public JsonElement? OutputMapping { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Schema { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Code { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Message { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DetailsExpression { get; init; }
}

/// <summary>Compatibility shape for persisted Flow definitions authored before named outputs.</summary>
public sealed record FailureFlowStepDefinition : FlowStepDefinition
{
    public string Code { get; init; } = "FLOW_FAILED";
    public string Message { get; init; } = "Flow execution failed.";
    public string? DetailsExpression { get; init; }
}

public sealed record FlowOutputDefinition(
    string Name,
    string? DisplayName,
    FlowOutputOutcome Outcome,
    JsonElement? Schema);

public sealed record FlowTransitionDefinition(
    string Id,
    string FromStep,
    string Event,
    string ToStep,
    string? Condition = null,
    int? Priority = null);

public sealed record FlowGraphDefinition
{
    public required string EntryStep { get; init; }
    public JsonElement? InputSchema { get; init; }
    public IReadOnlyList<FlowStepDefinition> Steps { get; init; } = [];
    public IReadOnlyList<FlowTransitionDefinition> Transitions { get; init; } = [];
    public JsonElement? OutputSchema { get; init; }
    public FlowDesignerMetadata Designer { get; init; } = new();
}

public sealed record FlowDraft
{
    public required Agentstration.Resources.WorkspaceId WorkspaceId { get; init; }
    public required string Id { get; init; }
    public required FlowId FlowId { get; init; }
    public required string DisplayName { get; init; }
    public string? Description { get; init; }
    public IReadOnlyDictionary<string, string> Tags { get; init; } = new Dictionary<string, string>();
    public required FlowGraphDefinition Definition { get; init; }
    public long Revision { get; init; } = 1;
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
    public string UpdatedBy { get; init; } = "local-user";
    public string DefinitionHash => FlowDefinitionHash.Compute(Definition);
}

public enum FlowValidationSeverity { Error, Warning, Information }
public sealed record FlowValidationIssue(string Code, FlowValidationSeverity Severity, string Message, string? StepId = null, string? TransitionId = null, string? PropertyPath = null);
public sealed record FlowValidationResult(IReadOnlyList<FlowValidationIssue> Issues)
{
    public bool IsValid => Issues.All(issue => issue.Severity != FlowValidationSeverity.Error);
}

public enum FlowDefinitionState { Draft, Published }

public static class FlowStepDefinitionExtensions
{
    public static string Type(this FlowStepDefinition step) => step switch
    {
        InputFlowStepDefinition => "input",
        AgentFlowStepDefinition => "agent",
        RouterFlowStepDefinition => "router",
        ConditionFlowStepDefinition => "condition",
        TransformFlowStepDefinition => "transform",
        FlowCallStepDefinition => "flow",
        RepeatFlowStepDefinition => "repeat",
        ToolFlowStepDefinition => "tool",
        ToolRouteFlowStepDefinition => "toolRoute",
        OutputFlowStepDefinition => "output",
        FailureFlowStepDefinition => "failure",
        _ => throw new ArgumentOutOfRangeException(nameof(step))
    };

    public static IReadOnlyList<string> OutputEvents(this FlowStepDefinition step) => step switch
    {
        InputFlowStepDefinition => ["completed"],
        AgentFlowStepDefinition => ["success", "error"],
        RouterFlowStepDefinition => ["selected", "failed"],
        ConditionFlowStepDefinition => ["true", "false"],
        TransformFlowStepDefinition => ["completed"],
        ToolFlowStepDefinition => ["success", "error"],
        ToolRouteFlowStepDefinition => ["success", "error"],
        FlowCallStepDefinition => [],
        OutputFlowStepDefinition or FailureFlowStepDefinition => [],
        _ => throw new ArgumentOutOfRangeException(nameof(step))
    };
}

public static class FlowGraphDefinitionExtensions
{
    public static IReadOnlyList<FlowOutputDefinition> GetOutputs(this FlowGraphDefinition definition) =>
        definition.Steps
            .Select(step => step switch
            {
                OutputFlowStepDefinition output => new FlowOutputDefinition(
                    output.Name,
                    output.DisplayName,
                    output.Outcome ?? FlowOutputOutcome.Success,
                    definition.ResolveOutputSchema(output, out _)?.Clone()),
                FailureFlowStepDefinition failure => new FlowOutputDefinition(
                    failure.Name,
                    failure.DisplayName,
                    FlowOutputOutcome.Error,
                    null),
                _ => null
            })
            .OfType<FlowOutputDefinition>()
            .ToArray();

    public static JsonElement? ResolveOutputSchema(
        this FlowGraphDefinition definition,
        OutputFlowStepDefinition output,
        out bool ambiguous)
    {
        ambiguous = false;
        if (output.Schema is { } declared) return declared;
        if (output.Outcome is not FlowOutputOutcome.Error && definition.OutputSchema is { } legacy) return legacy;
        if (output.OutputMapping is { } mapping
            && (mapping.ValueKind != JsonValueKind.String
                || !string.Equals(mapping.GetString(), "${transition.output}", StringComparison.Ordinal)))
            return null;

        var candidates = definition.Transitions
            .Where(transition => transition.ToStep == output.Name)
            .Select(transition => definition.Steps.FirstOrDefault(step => step.Name == transition.FromStep))
            .Select(step => step is InputFlowStepDefinition input ? input.Schema ?? definition.InputSchema : null)
            .Where(schema => schema is not null)
            .Select(schema => schema!.Value)
            .ToArray();
        var distinct = new List<JsonElement>();
        foreach (var candidate in candidates)
            if (!distinct.Any(existing => JsonElement.DeepEquals(existing, candidate))) distinct.Add(candidate);
        ambiguous = distinct.Count > 1;
        return distinct.Count == 1 ? distinct[0] : null;
    }
}

public static class FlowDefinitionHash
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    public static string Compute(FlowGraphDefinition definition)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(definition, JsonOptions));
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }
}
