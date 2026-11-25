using Agentstration.Resources;

namespace Agentstration.Work;

public enum WorkProjectBindingPurpose { Context, Sharing, Repository, TaskOperations }
public sealed record WorkProjectResourceReference(ScopedResourceAddress Address, Guid Uid, long Generation, string? Version = null);
public sealed record WorkProjectContextBinding(string Name, WorkProjectBindingPurpose Purpose, WorkProjectResourceReference Target);
public sealed record WorkProjectResolvedBinding(WorkProjectContextBinding Binding, string ETag, string DiscoverDecision, string UseDecision);
public sealed record WorkProjectBindingAvailability(WorkProjectContextBinding Binding, bool Available, string Code);
public sealed record WorkProjectContextView(WorkProjectId ProjectId, long ProjectVersion, IReadOnlyList<WorkProjectBindingAvailability> Bindings);
public sealed record WorkProjectContextSnapshot(WorkProjectId ProjectId, long ProjectVersion, string Digest,
    IReadOnlyList<WorkProjectResolvedBinding> Bindings, DateTimeOffset CapturedAt);

public static class WorkProjectContextRules
{
    public const int MaximumBindings = 32;

    public static bool Valid(IReadOnlyList<WorkProjectContextBinding>? bindings) => bindings is not null
        && bindings.Count <= MaximumBindings && bindings.Select(binding => binding?.Name).Distinct(StringComparer.Ordinal).Count() == bindings.Count
        && bindings.Count(binding => binding?.Purpose == WorkProjectBindingPurpose.Sharing) <= 1
        && bindings.Count(binding => binding?.Purpose == WorkProjectBindingPurpose.Repository) <= 1
        && bindings.All(binding => binding is not null && WorkProjectDefinition.ValidName(binding.Name) && Enum.IsDefined(binding.Purpose)
            && binding.Target is { Uid: var uid, Generation: >= 1 } target && uid != Guid.Empty
            && target.Address.ScopeRef != default && target.Address.Name is { Length: > 0 and <= 512 }
            && target.Address.Kind is "ToolProvider" or "ConnectedService" or "SourceVersion" or "Tool"
            && (target.Address.Kind != "Tool" || binding.Purpose is WorkProjectBindingPurpose.TaskOperations or WorkProjectBindingPurpose.Repository or WorkProjectBindingPurpose.Sharing)
            && target.Version?.Length is not > 128
            && (binding.Purpose != WorkProjectBindingPurpose.TaskOperations || target.Address.Kind is "ToolProvider" or "Tool")
            && (binding.Purpose != WorkProjectBindingPurpose.Sharing || target.Address.Kind is "ToolProvider" or "Tool")
            && (binding.Purpose != WorkProjectBindingPurpose.Repository || target.Address.Kind is "ToolProvider" or "ConnectedService" or "Tool"));
}
