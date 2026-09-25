using System.Text.Json;
using System.Text.Json.Serialization;
using Agentstration.Resources;

namespace Agentstration.ResourceManagement.Contracts;

public sealed record BootstrapResourceDocument
{
    public string ApiVersion { get; init; } = string.Empty;
    public string Kind { get; init; } = string.Empty;
    public ResourceMetadata Metadata { get; init; } = new();
    public JsonElement Definition { get; init; }
}

public enum BootstrapResourceApplyResult { Created, Skipped, Conflict }

[JsonConverter(typeof(JsonStringEnumConverter<BootstrapProfileScope>))]
public enum BootstrapProfileScope
{
    [JsonStringEnumMemberName("instance")] Instance,
    [JsonStringEnumMemberName("tenant")] Tenant,
    [JsonStringEnumMemberName("workspace")] Workspace
}

[JsonConverter(typeof(JsonStringEnumConverter<BootstrapResourceDisposition>))]
public enum BootstrapResourceDisposition
{
    [JsonStringEnumMemberName("create")] Create,
    [JsonStringEnumMemberName("skip")] Skip,
    [JsonStringEnumMemberName("conflict")] Conflict,
    [JsonStringEnumMemberName("invalid")] Invalid,
    [JsonStringEnumMemberName("failed")] Failed
}

public sealed record BootstrapApplicationTarget(Guid? TenantId = null, Guid? WorkspaceId = null);
public sealed record BootstrapBindingSelection(string Profile, string Name, ResourceReference Target);

[JsonConverter(typeof(JsonStringEnumConverter<BootstrapBindingTargetKind>))]
public enum BootstrapBindingTargetKind
{
    [JsonStringEnumMemberName("modelProfile")] ModelProfile,
    [JsonStringEnumMemberName("modelProvider")] ModelProvider,
    [JsonStringEnumMemberName("runtimeProfile")] RuntimeProfile,
    [JsonStringEnumMemberName("extensionRegistration")] ExtensionRegistration,
    [JsonStringEnumMemberName("parameter")] Parameter,
    [JsonStringEnumMemberName("secret")] Secret
}

public sealed record BootstrapResourceOperationContext(string ProfileName, string ProfilePath, BootstrapProfileScope ProfileScope, BootstrapApplicationTarget? Target = null);
public sealed record BootstrapResourcePlanDetail(string Kind, string Name, BootstrapResourceDisposition Disposition, string? Description = null);
public sealed record BootstrapResourcePlanResult(BootstrapResourceDisposition Disposition, IReadOnlyList<BootstrapResourcePlanDetail>? Details = null);

public sealed class BootstrapPlanningContext
{
    private readonly HashSet<(string Kind, string Name, string? Parent)> planned = [];
    private readonly Dictionary<(string Kind, string Name, string? Parent), BootstrapResourceDocument> documents = [];

    public void Register(string kind, string name, string? parent = null, BootstrapResourceDocument? document = null)
    {
        var key = (kind, name, parent);
        planned.Add(key);
        if (document is not null) documents[key] = document;
    }

    public bool Contains(string kind, string name, string? parent = null) => planned.Contains((kind, name, parent));

    public bool TryGetDocument(string kind, string name, string? parent, out BootstrapResourceDocument document) =>
        documents.TryGetValue((kind, name, parent), out document!);
}

public interface IBootstrapResourceHandler
{
    string Kind { get; }
    BootstrapProfileScope Scope { get; }
    bool SupportsProfileScope(BootstrapProfileScope profileScope) =>
        Scope == profileScope
        || (profileScope == BootstrapProfileScope.Workspace && Scope == BootstrapProfileScope.Tenant);
    Task<BootstrapResourcePlanResult> PlanAsync(BootstrapResourceDocument resource, BootstrapResourceOperationContext operation, BootstrapPlanningContext planning, CancellationToken cancellationToken);
    Task<BootstrapResourceApplyResult> ApplyAsync(BootstrapResourceDocument resource, BootstrapResourceOperationContext operation, CancellationToken cancellationToken);
}
