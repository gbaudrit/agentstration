using Agentstration.Resources;

namespace Agentstration.Work;

public enum WorkProjectBindingPurpose { Context, Sharing, Repository, TaskOperations, CodePublication, Tracking, Forge }
public sealed record WorkProjectResourceReference(ScopedResourceAddress Address, Guid Uid, long Generation, string? Version = null);
public sealed record WorkProjectContractSelection(WorkProjectResourceReference Provider, WorkProjectResourceReference Extension,
    string Version, string ProviderId, string TargetId, string BindingId, string Revision, IReadOnlyList<string> RequiredOperations);
public sealed record WorkProjectContributionPin(WorkProjectResourceReference Resource, string RouteDigest);
public sealed record WorkProjectContractEvidence(string Version, string ImplementationId, string ImplementationVersion,
    WorkProjectResourceReference Provider, WorkProjectResourceReference Extension, string MappingDigest, string CapabilitiesDigest,
    IReadOnlyList<string> Operations, IReadOnlyList<WorkProjectContributionPin> Contributions);
public sealed record WorkProjectContextBinding(string Name, WorkProjectBindingPurpose Purpose, WorkProjectResourceReference Target)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public WorkProjectContractSelection? Contract { get; init; }
    public WorkProjectConnectionSelection? Connection { get; init; }
    public WorkProjectResourceReference? NativeService { get; init; }
}
public sealed record WorkProjectResolvedBinding(WorkProjectContextBinding Binding, string ETag, string DiscoverDecision, string UseDecision)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public WorkProjectContractEvidence? Contract { get; init; }
    public WorkProjectConnectionEvidence? Service { get; init; }
    public WorkProjectNativeRoute? NativeRoute { get; init; }
}
public sealed record WorkProjectNativeRoute(WorkProjectResourceReference Provider, WorkProjectResourceReference Extension, string Digest);
public sealed record WorkProjectBindingAvailability(WorkProjectContextBinding Binding, bool Available, string Code)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public WorkProjectContractEvidence? Contract { get; init; }
}
public sealed record WorkProjectContextView(WorkProjectId ProjectId, long ProjectVersion, IReadOnlyList<WorkProjectBindingAvailability> Bindings)
{
    public IReadOnlyList<WorkProjectConnectionAvailability> Connections { get; init; } = [];
}
public sealed record WorkProjectContextSnapshot(WorkProjectId ProjectId, long ProjectVersion, string Digest,
    IReadOnlyList<WorkProjectResolvedBinding> Bindings, DateTimeOffset CapturedAt)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public WorkProjectSharingConnection? Sharing { get; init; }
}

public static class WorkProjectContextRules
{
    public const int MaximumBindings = 32;

    public static bool Valid(IReadOnlyList<WorkProjectContextBinding>? bindings) => bindings is not null
        && bindings.Count <= MaximumBindings && bindings.Select(binding => binding?.Name).Distinct(StringComparer.Ordinal).Count() == bindings.Count
        && bindings.Count(binding => binding?.Purpose == WorkProjectBindingPurpose.Sharing) <= 1
        && bindings.Count(binding => binding?.Purpose == WorkProjectBindingPurpose.Repository) <= 1
        && bindings.Count(binding => binding?.Purpose == WorkProjectBindingPurpose.CodePublication) <= 1
        && bindings.All(binding => binding is not null && WorkProjectDefinition.ValidName(binding.Name) && Enum.IsDefined(binding.Purpose)
            && binding.Target is { Uid: var uid, Generation: >= 1 } target && uid != Guid.Empty
            && target.Address.ScopeRef != default && target.Address.Name is { Length: > 0 and <= 512 }
            && target.Address.Kind is "ToolProvider" or "ConnectedService" or "SourceVersion" or "Tool"
            && (target.Address.Kind != "Tool" || binding.Purpose is WorkProjectBindingPurpose.TaskOperations or WorkProjectBindingPurpose.Repository or WorkProjectBindingPurpose.Sharing or WorkProjectBindingPurpose.CodePublication or WorkProjectBindingPurpose.Tracking or WorkProjectBindingPurpose.Forge)
            && (binding.NativeService is null || binding.Connection is null && binding.Contract is null
                && binding.Purpose is WorkProjectBindingPurpose.Sharing or WorkProjectBindingPurpose.CodePublication
                && target.Address.Kind == "Tool" && Reference(binding.NativeService, "ConnectedService"))
            && ValidContract(binding)
            && target.Version?.Length is not > 128
            && (binding.Purpose != WorkProjectBindingPurpose.CodePublication || target.Address.Kind == "Tool")
            && (binding.Purpose != WorkProjectBindingPurpose.TaskOperations || target.Address.Kind is "ToolProvider" or "Tool")
            && (binding.Purpose != WorkProjectBindingPurpose.Sharing || target.Address.Kind is "ToolProvider" or "Tool")
            && (binding.Purpose != WorkProjectBindingPurpose.Repository || target.Address.Kind is "ToolProvider" or "ConnectedService" or "Tool"));

    public static bool ValidEvidence(WorkProjectContextBinding binding, WorkProjectContractEvidence? evidence)
    {
        if (binding.Contract is not { } selection) return evidence is null;
        return evidence is not null && evidence.Provider == selection.Provider && evidence.Extension == selection.Extension
            && evidence.Version == selection.Version && Text(evidence.ImplementationId) && Text(evidence.ImplementationVersion)
            && Hash(evidence.MappingDigest) && Hash(evidence.CapabilitiesDigest)
            && evidence.Operations is { Count: > 0 and <= 8 } && evidence.Operations.All(operation => Text(operation, 32))
            && evidence.Operations.Distinct(StringComparer.Ordinal).Count() == evidence.Operations.Count
            && selection.RequiredOperations.All(evidence.Operations.Contains)
            && evidence.Contributions is { Count: > 0 and <= 8 }
            && evidence.Contributions.All(pin => pin is not null && Reference(pin.Resource, "Tool") && Hash(pin.RouteDigest))
            && evidence.Contributions.Select(pin => pin.Resource.Uid).Distinct().Count() == evidence.Contributions.Count
            && evidence.Contributions.Any(pin => pin.Resource == binding.Target)
            && WorkPlanDefinition.Digest(evidence.Contributions) == evidence.MappingDigest;
    }

    private static bool Hash(string? value) => value is { Length: 64 } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool ValidContract(WorkProjectContextBinding binding)
    {
        if (binding.Connection is { } connection) return WorkProjectConnectionRules.Valid(connection)
            && binding.Name == connection.Requirement.Name && binding.Target == connection.Assignment.DescribeTool
            && binding.Purpose == (connection.Requirement.Contract == WorkProjectConnectionRules.Tracking ? WorkProjectBindingPurpose.Tracking : WorkProjectBindingPurpose.Forge);
        if (binding.Purpose is not (WorkProjectBindingPurpose.Tracking or WorkProjectBindingPurpose.Forge)) return binding.Contract is null;
        return binding.Target.Address.Kind == "Tool" && binding.Contract is { } selection
            && Reference(selection.Provider, "ToolProvider") && Reference(selection.Extension, "ExtensionRegistration")
            && Text(selection.Version, 16) && Text(selection.ProviderId) && Text(selection.TargetId)
            && Text(selection.BindingId) && Text(selection.Revision)
            && selection.RequiredOperations is { Count: > 0 and <= 8 }
            && selection.RequiredOperations.All(operation => Text(operation, 32))
            && selection.RequiredOperations.Distinct(StringComparer.Ordinal).Count() == selection.RequiredOperations.Count;
    }

    public static bool Reference(WorkProjectResourceReference? reference, string kind) => reference is { Uid: var uid, Generation: >= 1 }
        && uid != Guid.Empty && reference.Address.ScopeRef != default && reference.Address.Kind == kind
        && Text(reference.Address.Name, 512) && reference.Version?.Length is not > 128;
    public static bool Text(string? text, int maximum = 128) => !string.IsNullOrWhiteSpace(text)
        && text.Length <= maximum && !text.Any(char.IsControl);

}
