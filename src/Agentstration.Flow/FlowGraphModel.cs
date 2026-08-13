using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Agentstration.Flow;

public sealed record FlowResourceReference(string ResourceId, string VersionStrategy = "UseDeploymentVersion", long? Version = null);
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
[JsonDerivedType(typeof(OutputFlowStepDefinition), "output")]
[JsonDerivedType(typeof(FailureFlowStepDefinition), "failure")]
public abstract record FlowStepDefinition
{
    public required string Name { get; init; }
    public string? DisplayName { get; init; }
    public string? Description { get; init; }
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

public sealed record OutputFlowStepDefinition : FlowStepDefinition
{
    public JsonElement? OutputMapping { get; init; }
}

public sealed record FailureFlowStepDefinition : FlowStepDefinition
{
    public string Code { get; init; } = "FLOW_FAILED";
    public string Message { get; init; } = "Flow execution failed.";
    public string? DetailsExpression { get; init; }
}

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
        OutputFlowStepDefinition => "output",
        FailureFlowStepDefinition => "failure",
        _ => throw new ArgumentOutOfRangeException(nameof(step))
    };
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
