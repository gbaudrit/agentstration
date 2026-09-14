using System.Text.Json;
using System.Text.Json.Serialization;

namespace Agentstration.ResourcePlanning.Contracts;

public static class ResourcePlanningContractVersions
{
    public const string V1 = "resource-planning.agentstration.io/v1";
    public static readonly IReadOnlySet<string> Supported = new HashSet<string>(StringComparer.Ordinal) { V1 };
}

public sealed record FunctionalResourcePlanV1
{
    public required PlanningSolutionIntent Solution { get; init; }
    public IReadOnlyList<PlanningRoleIntent> Roles { get; init; } = [];
    public IReadOnlyList<PlanningWorkflowIntent> Workflows { get; init; } = [];
    public IReadOnlyList<PlanningIntegrationIntent> Integrations { get; init; } = [];
    public IReadOnlyList<PlanningExperienceIntent> Experiences { get; init; } = [];
    public IReadOnlyList<PlanningLogicalDependency> Dependencies { get; init; } = [];
    public PlanningRuntimeConstraints Runtime { get; init; } = new();
}

public sealed record PlanningSolutionIntent(
    string Summary,
    IReadOnlyList<string> Outcomes,
    IReadOnlyList<string>? Constraints = null);

public sealed record PlanningRoleIntent(
    string LogicalId,
    string DisplayName,
    string Purpose,
    IReadOnlyList<string> Responsibilities,
    IReadOnlyList<string> Capabilities,
    PlanningModelNeeds? Model = null);

public sealed record PlanningModelNeeds(
    IReadOnlyList<string>? RequiredCapabilities = null,
    string? Quality = null,
    string? Cost = null,
    string? Latency = null);

public sealed record PlanningWorkflowIntent(
    string LogicalId,
    string DisplayName,
    string Objective,
    IReadOnlyList<string> Participants,
    PlanningCollaborationStyle Collaboration,
    IReadOnlyList<PlanningWorkflowStage>? Stages = null);

[JsonConverter(typeof(JsonStringEnumConverter<PlanningCollaborationStyle>))]
public enum PlanningCollaborationStyle
{
    Individual,
    Ordered,
    Parallel,
    Delegated,
    Collaborative,
    Adaptive
}

public sealed record PlanningWorkflowStage(
    string LogicalId,
    string Purpose,
    IReadOnlyList<string> Participants,
    IReadOnlyList<string>? DependsOn = null);

public sealed record PlanningIntegrationIntent(
    string LogicalId,
    string DisplayName,
    string Purpose,
    IReadOnlyList<string> RequiredCapabilities,
    string? DataClassification = null,
    bool RequiresHumanApproval = false);

public sealed record PlanningExperienceIntent(
    string LogicalId,
    string DisplayName,
    string Purpose,
    string Workflow,
    IReadOnlyList<string> Audiences,
    PlanningInteractionStyle Interaction = PlanningInteractionStyle.Conversation);

[JsonConverter(typeof(JsonStringEnumConverter<PlanningInteractionStyle>))]
public enum PlanningInteractionStyle
{
    Conversation,
    Form,
    Automation,
    Api
}

public sealed record PlanningLogicalDependency(
    string From,
    string To,
    string Relationship);

public sealed record PlanningRuntimeConstraints(
    bool LocalOnly = false,
    bool CloudAllowed = true,
    IReadOnlyList<string>? RequiredCapabilities = null,
    IReadOnlyList<string>? DataResidency = null);

public enum PlanningValidationSeverity { Warning, Error }

public sealed record PlanningValidationIssue(
    string Code,
    string Path,
    string Message,
    PlanningValidationSeverity Severity = PlanningValidationSeverity.Error);

public sealed record PlanningValidationResult(IReadOnlyList<PlanningValidationIssue> Issues)
{
    public bool IsValid => Issues.All(value => value.Severity != PlanningValidationSeverity.Error);
}

public static class FunctionalResourcePlanSerializer
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static ResourcePlanContent Serialize(FunctionalResourcePlanV1 plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return new(ResourcePlanningContractVersions.V1, JsonSerializer.SerializeToElement(plan, Options));
    }

    public static FunctionalResourcePlanV1 Deserialize(ResourcePlanContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (!string.Equals(content.SchemaVersion, ResourcePlanningContractVersions.V1, StringComparison.Ordinal))
            throw new NotSupportedException($"Resource Planning contract '{content.SchemaVersion}' is not supported.");
        return content.Document.Deserialize<FunctionalResourcePlanV1>(Options)
            ?? throw new JsonException("The functional Resource Plan document is empty.");
    }
}
