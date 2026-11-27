namespace Agentstration.Work;

// Requirements are portable. Assignments and resolved evidence belong to one installation.
public sealed record WorkProjectConnectionRequirement(string Name, string Contract, string Version,
    IReadOnlyList<string> RequiredOperations, WorkTrackingTarget? Target = null);
public sealed record WorkProjectConnectionExport(string Field, string ExportId);
public sealed record WorkProjectConnectionAssignment(string Name, WorkProjectResourceReference Service,
    WorkProjectResourceReference DescribeTool, IReadOnlyList<WorkProjectConnectionExport> Exports);
public sealed record WorkProjectConnectionSelection(WorkProjectConnectionRequirement Requirement, WorkProjectConnectionAssignment Assignment);
public sealed record WorkProjectConnectionEvidence(WorkProjectResourceReference Service, WorkProjectResourceReference Type,
    string TypeVersion, string TypeSchemaDigest, string ConfigurationDigest);
public sealed record WorkProjectConnectionAvailability(WorkProjectConnectionRequirement Requirement,
    WorkProjectConnectionAssignment? Assignment, bool Available, string Code,
    WorkProjectContractEvidence? Contract = null, WorkProjectConnectionEvidence? Service = null);

public static class WorkProjectConnectionRules
{
    public const string Tracking = "agentstration.tracking.contracts.mcp.v1";
    public const string Forge = "agentstration.forge.contracts.mcp.v1";
    public static IReadOnlyList<string> TargetFields { get; } = Array.AsReadOnly(new[] { "providerId", "targetId", "bindingId", "revision" });

    public static bool Valid(WorkProjectDefinition definition) => definition.ConnectionRequirements is { Count: <= 32 } requirements
        && definition.ConnectionAssignments is { Count: <= 32 } assignments
        && requirements.All(Valid) && requirements.Select(value => value.Name).Distinct(StringComparer.Ordinal).Count() == requirements.Count
        && assignments.All(Valid) && assignments.Select(value => value.Name).Distinct(StringComparer.Ordinal).Count() == assignments.Count
        && assignments.All(value => requirements.Any(need => need.Name == value.Name))
        && definition.Bindings is not null && definition.Bindings.All(value => value is not null && value.Connection is null && value.Purpose is not (WorkProjectBindingPurpose.Tracking or WorkProjectBindingPurpose.Forge)) && !definition.Bindings.Any(value => value is not null && requirements.Any(need => need.Name == value.Name));

    public static WorkProjectConnectionRequirement Normalize(WorkProjectConnectionRequirement value) =>
        value with { RequiredOperations = value.RequiredOperations.Order(StringComparer.Ordinal).ToArray() };

    public static bool Valid(WorkProjectConnectionRequirement value) => value is not null && WorkProjectDefinition.ValidName(value.Name)
        && value.Contract is Tracking or Forge && value.Version == "1.0"
        && value.RequiredOperations is { Count: > 0 and <= 8 } && value.RequiredOperations.All(operation => WorkProjectContextRules.Text(operation, 32))
        && value.RequiredOperations.Distinct(StringComparer.Ordinal).Count() == value.RequiredOperations.Count
        && (value.Target is null || WorkProjectContextRules.Text(value.Target.ProviderId) && WorkProjectContextRules.Text(value.Target.TargetId));

    public static bool Valid(WorkProjectConnectionAssignment value) => value is not null && WorkProjectDefinition.ValidName(value.Name)
        && WorkProjectContextRules.Reference(value.Service, "ConnectedService") && WorkProjectContextRules.Reference(value.DescribeTool, "Tool")
        && value.Exports is { Count: 4 } && value.Exports.All(item => item is not null && TargetFields.Contains(item.Field)
            && WorkProjectContextRules.Text(item.ExportId)) && value.Exports.Select(item => item.Field).Distinct(StringComparer.Ordinal).Count() == 4;

    public static bool Valid(WorkProjectConnectionSelection value) => value is not null && Valid(value.Requirement) && Valid(value.Assignment)
        && value.Requirement.Name == value.Assignment.Name;

    public static WorkProjectContextBinding Binding(WorkProjectConnectionSelection selection) => new(selection.Requirement.Name,
        selection.Requirement.Contract == Tracking ? WorkProjectBindingPurpose.Tracking : WorkProjectBindingPurpose.Forge,
        selection.Assignment.DescribeTool)
    { Connection = selection };

    public static bool Contains(WorkProjectDefinition definition, WorkProjectConnectionSelection selection) =>
        definition.ConnectionRequirements.Any(value => WorkPlanDefinition.Digest(value) == WorkPlanDefinition.Digest(selection.Requirement))
        && definition.ConnectionAssignments.Any(value => WorkPlanDefinition.Digest(value) == WorkPlanDefinition.Digest(selection.Assignment));

    public static bool Evidence(WorkProjectResolvedBinding value) => value.Binding.Connection is not { } selection ? value.Service is null
        : value.Service is { } service && Valid(selection) && service.Service == selection.Assignment.Service
            && WorkProjectContextRules.Reference(service.Type, "ConnectedServiceType") && WorkProjectContextRules.Text(service.TypeVersion)
            && service.TypeSchemaDigest.StartsWith("sha256:", StringComparison.Ordinal) && Hash(service.TypeSchemaDigest[7..]) && Hash(service.ConfigurationDigest);

    private static bool Hash(string? value) => value is { Length: 64 } && value.All(character => char.IsAsciiHexDigitLower(character));
}
