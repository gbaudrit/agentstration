using System.Security.Cryptography;
using System.Text;
using Agentstration.Application.Work;
using Agentstration.Infrastructure.Declarative;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Tools;
using Agentstration.Work;

namespace Agentstration.Infrastructure.Bootstrap;

public sealed record DeclarativeProjectNativeService(string Name, WorkProjectResourceReference Service, WorkProjectResourceReference Tool);

public sealed record DeclarativeProjectSharingDefinition(WorkSharingProjectId ProjectId, WorkParticipantId ParticipantId,
    string Location, string ToolName, string? ToolNamespace = null, string? DisplayName = null, Guid? Id = null)
{
    public IReadOnlyList<DeclarativeProjectNativeService> NativeServices { get; init; } = [];
    public IReadOnlyList<WorkProjectConnectionRequirement> ConnectionRequirements { get; init; } = [];
    public IReadOnlyList<WorkProjectConnectionAssignment> ConnectionAssignments { get; init; } = [];
    public string? RepositoryToolName { get; init; }
    public string? RepositoryToolNamespace { get; init; }
    public string? CodePublicationToolName { get; init; }
    public string? CodePublicationToolNamespace { get; init; }
    public string? TaskOperationsToolName { get; init; }
    public string? TaskOperationsToolNamespace { get; init; }
}

/// <summary>Connects a private Project to a pre-provisioned local protected Tool. It never synchronizes or applies remote configuration.</summary>
public sealed class ProjectSharingBootstrapResourceHandler(WorkProjectService projects, IWorkplaceContext context, IResourceStore resources) : IBootstrapResourceHandler
{
    public string Kind => "ProjectSharing";
    public BootstrapProfileScope Scope => BootstrapProfileScope.Workspace;
    public async Task<BootstrapResourcePlanResult> PlanAsync(BootstrapResourceDocument resource, BootstrapResourceOperationContext operation,
        BootstrapPlanningContext planning, CancellationToken token)
    {
        var value = Parse(resource, operation);
        var workspace = WorkspaceBootstrapResource.Workspace(operation);
        var existing = await projects.FindAsync(workspace, value.Metadata.Namespace, value.Metadata.Name, token);
        if (existing.IsSuccess) return new(await ReusableAsync(workspace, existing.Value!, value.Definition, token)
            ? BootstrapResourceDisposition.Skip : BootstrapResourceDisposition.Conflict, ConnectionDetails(value.Definition));
        var definition = await DefinitionAsync(workspace, value.Definition, token, planning);
        if (definition is not null)
        {
            var valid = await projects.ValidateAsync(workspace, definition, token);
            if (!valid.IsSuccess) throw Invalid(valid.ErrorCode!);
        }
        _ = WorkspaceBootstrapResource.Created(planning, Kind, value.Metadata.Name, value.Metadata.Namespace, resource);
        return new(BootstrapResourceDisposition.Create, ConnectionDetails(value.Definition));
    }
    public async Task<BootstrapResourceApplyResult> ApplyAsync(BootstrapResourceDocument resource, BootstrapResourceOperationContext operation, CancellationToken token)
    {
        var value = Parse(resource, operation);
        var workspace = WorkspaceBootstrapResource.Workspace(operation);
        var existing = await projects.FindAsync(workspace, value.Metadata.Namespace, value.Metadata.Name, token);
        if (existing.IsSuccess) return await ReusableAsync(workspace, existing.Value!, value.Definition, token)
            ? BootstrapResourceApplyResult.Skipped : BootstrapResourceApplyResult.Conflict;
        var definition = await DefinitionAsync(workspace, value.Definition, token);
        var id = new WorkProjectId(value.Definition.Id ?? new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{workspace}:{context.PrincipalId:D}:{value.Metadata.Namespace}:{value.Metadata.Name}")).AsSpan(0, 16)));
        var result = await projects.CreateAsync(workspace, id, value.Metadata.Name, value.Metadata.Namespace, new(id.Value, 0, definition!), token);
        if (!result.IsSuccess) throw Invalid(result.ErrorCode!);
        return BootstrapResourceApplyResult.Created;
    }
    private DeclarativeResourceEnvelope<DeclarativeProjectSharingDefinition> Parse(BootstrapResourceDocument resource, BootstrapResourceOperationContext operation)
    {
        if (operation.ProfileScope != BootstrapProfileScope.Workspace || operation.ActorPrincipalId is { } actor && actor != context.PrincipalId
            || WorkspaceBootstrapResource.Workspace(operation) != context.WorkspaceId) throw Invalid("work_project_not_found");
        var value = WorkspaceBootstrapResource.Parse<DeclarativeResourceEnvelope<DeclarativeProjectSharingDefinition>>(resource);
        if (value.Definition?.NativeServices is not { Count: <= 2 } nativeServices || nativeServices.Any(item => item.Name is not ("sharing" or "code-publication"))
            || nativeServices.Select(item => item.Name).Distinct(StringComparer.Ordinal).Count() != nativeServices.Count) throw Invalid("work_project_connections_invalid");
        if (!WorkProjectDefinition.ValidName(value.Metadata.Name) || value.Definition is null || value.Definition.Id == Guid.Empty)
            throw Invalid("sharing_connection_invalid");
        return value;
    }
    private async Task<bool> ReusableAsync(WorkspaceId workspace, WorkProjectSnapshot project, DeclarativeProjectSharingDefinition value, CancellationToken token)
    {
        if (!Matches(project, value)) return false;
        var expected = (await DefinitionAsync(workspace, value, token))!;
        if (expected.Bindings.Any(binding => !project.Definition.Bindings.Contains(binding))
            || expected.ConnectionRequirements.Any(need => !project.Definition.ConnectionRequirements.Any(own => WorkPlanDefinition.Digest(own) == WorkPlanDefinition.Digest(WorkProjectConnectionRules.Normalize(need))))
            || expected.ConnectionAssignments.Any(assignment => !project.Definition.ConnectionAssignments.Any(own => WorkPlanDefinition.Digest(own) == WorkPlanDefinition.Digest(assignment)))) return false;
        var valid = await projects.ValidateAsync(workspace, project.Definition, token);
        if (!valid.IsSuccess) throw Invalid(valid.ErrorCode!);
        return true;
    }
    private async Task<WorkProjectDefinition?> DefinitionAsync(WorkspaceId workspace, DeclarativeProjectSharingDefinition value,
        CancellationToken token, BootstrapPlanningContext? planning = null)
    {
        var bindings = new List<WorkProjectContextBinding>();
        var deferred = false;
        foreach (var (name, purpose, toolName, toolNamespace) in Targets(value))
        {
            if (string.IsNullOrWhiteSpace(toolName)) throw Invalid("sharing_connection_invalid");
            var native = value.NativeServices.SingleOrDefault(item => item.Name == name);
            var address = native?.Tool.Address ?? ScopedResourceAddress.Create(ResourceScopeRef.Workspace(workspace.Value),
                ResourceNamespace.Parse(toolNamespace ?? "default"), ToolResourceKinds.Tool, toolName);
            var tool = (await resources.GetExactAsync<ToolResource>(address, token))?.Value;
            if (tool is null && planning?.Contains(ToolResourceKinds.Tool, toolName, address.Namespace.Value) == true)
            {
                deferred = true;
                continue;
            }
            if (tool is null) throw Invalid("sharing_tool_not_found");
            if (native is not null && (tool.Uid != native.Tool.Uid || tool.Generation != native.Tool.Generation)) throw Invalid("work_project_connection_stale");
            if (native is null && !tool.Definition.Metadata.ContainsKey("agentstration.toolPublicationUid")) throw Invalid("sharing_protected_relay_required");
            bindings.Add(new(name, purpose, new(address, tool.Uid, tool.Generation)) { NativeService = native?.Service });
        }
        if (deferred) return null; // Resolve and authorize the real publication identities at application time.
        return new(value.DisplayName ?? value.ProjectId.ToString(), null, [], [])
        {
            Bindings = bindings,
            ConnectionRequirements = value.ConnectionRequirements,
            ConnectionAssignments = value.ConnectionAssignments,
            Sharing = new(value.ProjectId, value.ParticipantId, "sharing", value.Location)
        };
    }
    private static IReadOnlyList<BootstrapResourcePlanDetail> ConnectionDetails(DeclarativeProjectSharingDefinition value) =>
        value.ConnectionRequirements.Select(need =>
        {
            var service = value.ConnectionAssignments.SingleOrDefault(assignment => assignment.Name == need.Name)?.Service;
            return new BootstrapResourcePlanDetail("ConnectedService", need.Name, BootstrapResourceDisposition.Skip)
            {
                Contract = need.Contract,
                Target = need.Target is { } target ? target.ProviderId + "/" + target.TargetId : null,
                Reference = service is null ? null : new(service.Address.Name, service.Address.ScopeRef, service.Address.Namespace)
            };
        }).Concat(value.NativeServices.Select(item => new BootstrapResourcePlanDetail("ConnectedService", item.Name, BootstrapResourceDisposition.Skip)
        { Contract = "agentstration.git.repository", Reference = new(item.Service.Address.Name, item.Service.Address.ScopeRef, item.Service.Address.Namespace) })).ToArray();
    private static IEnumerable<(string Name, WorkProjectBindingPurpose Purpose, string Tool, string? Namespace)> Targets(DeclarativeProjectSharingDefinition value)
    {
        yield return ("sharing", WorkProjectBindingPurpose.Sharing, value.ToolName, value.ToolNamespace);
        if (value.RepositoryToolName is not null) yield return ("repository", WorkProjectBindingPurpose.Repository, value.RepositoryToolName, value.RepositoryToolNamespace);
        if (value.CodePublicationToolName is not null) yield return ("code-publication", WorkProjectBindingPurpose.CodePublication, value.CodePublicationToolName, value.CodePublicationToolNamespace);
        if (value.TaskOperationsToolName is not null) yield return ("task-operations", WorkProjectBindingPurpose.TaskOperations, value.TaskOperationsToolName, value.TaskOperationsToolNamespace);
    }
    private static bool Matches(WorkProjectSnapshot project, DeclarativeProjectSharingDefinition value) => (value.Id is null || project.Id.Value == value.Id)
        && project.Definition.Sharing is { } connection && connection.ProjectId == value.ProjectId && connection.ParticipantId == value.ParticipantId && connection.Location == value.Location
        && Targets(value).All(target => project.Definition.Bindings.Any(binding => binding.Name == target.Name && binding.Purpose == target.Purpose
            && binding.Target.Address.Name == target.Tool && binding.Target.Address.Namespace.Value == (target.Namespace ?? "default")));
    private static WorkValidationException Invalid(string code) => new(code, "The Project sharing connection could not be planned or applied.");
}
