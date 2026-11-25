using System.Security.Cryptography;
using System.Text;
using Agentstration.Application.Work;
using Agentstration.Infrastructure.Declarative;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Tools;
using Agentstration.Work;

namespace Agentstration.Infrastructure.Bootstrap;

public sealed record DeclarativeProjectSharingDefinition(WorkSharingProjectId ProjectId, WorkParticipantId ParticipantId,
    string Location, string ToolName, string? ToolNamespace = null, string? DisplayName = null, Guid? Id = null);

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
        if (existing.IsSuccess) return new(Matches(existing.Value!, value.Definition) ? BootstrapResourceDisposition.Skip : BootstrapResourceDisposition.Conflict);
        var definition = await DefinitionAsync(workspace, value.Definition, token);
        var valid = await projects.ValidateAsync(workspace, definition, token);
        if (!valid.IsSuccess) throw Invalid(valid.ErrorCode!);
        return WorkspaceBootstrapResource.Created(planning, Kind, value.Metadata.Name, value.Metadata.Namespace, resource);
    }
    public async Task<BootstrapResourceApplyResult> ApplyAsync(BootstrapResourceDocument resource, BootstrapResourceOperationContext operation, CancellationToken token)
    {
        var value = Parse(resource, operation);
        var workspace = WorkspaceBootstrapResource.Workspace(operation);
        var existing = await projects.FindAsync(workspace, value.Metadata.Namespace, value.Metadata.Name, token);
        if (existing.IsSuccess) return Matches(existing.Value!, value.Definition) ? BootstrapResourceApplyResult.Skipped : BootstrapResourceApplyResult.Conflict;
        var definition = await DefinitionAsync(workspace, value.Definition, token);
        var id = new WorkProjectId(value.Definition.Id ?? new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{workspace}:{context.PrincipalId:D}:{value.Metadata.Namespace}:{value.Metadata.Name}")).AsSpan(0, 16)));
        var result = await projects.CreateAsync(workspace, id, value.Metadata.Name, value.Metadata.Namespace, new(id.Value, 0, definition), token);
        if (!result.IsSuccess) throw Invalid(result.ErrorCode!);
        return BootstrapResourceApplyResult.Created;
    }
    private DeclarativeResourceEnvelope<DeclarativeProjectSharingDefinition> Parse(BootstrapResourceDocument resource, BootstrapResourceOperationContext operation)
    {
        if (operation.ProfileScope != BootstrapProfileScope.Workspace || operation.ActorPrincipalId != context.PrincipalId
            || WorkspaceBootstrapResource.Workspace(operation) != context.WorkspaceId) throw Invalid("work_project_not_found");
        var value = WorkspaceBootstrapResource.Parse<DeclarativeResourceEnvelope<DeclarativeProjectSharingDefinition>>(resource);
        if (!WorkProjectDefinition.ValidName(value.Metadata.Name) || value.Definition is null || value.Definition.Id == Guid.Empty)
            throw Invalid("sharing_connection_invalid");
        return value;
    }
    private async Task<WorkProjectDefinition> DefinitionAsync(WorkspaceId workspace, DeclarativeProjectSharingDefinition value, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(value.ToolName)) throw Invalid("sharing_connection_invalid");
        var address = ScopedResourceAddress.Create(ResourceScopeRef.Workspace(workspace.Value),
            ResourceNamespace.Parse(value.ToolNamespace ?? "default"), ToolResourceKinds.Tool, value.ToolName);
        var tool = (await resources.GetExactAsync<ToolResource>(address, token))?.Value ?? throw Invalid("sharing_tool_not_found");
        if (!tool.Definition.Metadata.ContainsKey("agentstration.toolPublicationUid")) throw Invalid("sharing_protected_relay_required");
        return new(value.DisplayName ?? value.ProjectId.ToString(), null, [], [])
        {
            Bindings = [new("sharing", WorkProjectBindingPurpose.Sharing, new(address, tool.Uid, tool.Generation))],
            Sharing = new(value.ProjectId, value.ParticipantId, "sharing", value.Location)
        };
    }
    private static bool Matches(WorkProjectSnapshot project, DeclarativeProjectSharingDefinition value) => (value.Id is null || project.Id.Value == value.Id)
        && project.Definition.Sharing is { } connection && connection.ProjectId == value.ProjectId && connection.ParticipantId == value.ParticipantId && connection.Location == value.Location
        && project.Definition.Bindings.Any(binding => binding.Name == connection.BindingName && binding.Target.Address.Name == value.ToolName
            && binding.Target.Address.Namespace.Value == (value.ToolNamespace ?? "default"));
    private static WorkValidationException Invalid(string code) => new(code, "The Project sharing connection could not be planned or applied.");
}
