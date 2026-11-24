using System.Security.Cryptography;
using System.Text;
using Agentstration.Application.Work;
using Agentstration.Infrastructure.Declarative;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Work;

namespace Agentstration.Infrastructure.Bootstrap;

public sealed record DeclarativeProjectDefinition(Guid? Id = null, string? DisplayName = null, string? Description = null,
    IReadOnlyList<Guid>? PlanIds = null, IReadOnlyList<Guid>? TaskIds = null)
{
    public IReadOnlyList<WorkProjectContextBinding>? Bindings { get; init; }
}

public sealed class ProjectBootstrapResourceHandler(WorkProjectService projects, IWorkplaceContext context) : IBootstrapResourceHandler
{
    public string Kind => "Project";
    public BootstrapProfileScope Scope => BootstrapProfileScope.Workspace;

    public async Task<BootstrapResourcePlanResult> PlanAsync(BootstrapResourceDocument resource, BootstrapResourceOperationContext operation,
        BootstrapPlanningContext planning, CancellationToken token)
    {
        var value = Parse(resource, operation);
        var workspace = WorkspaceBootstrapResource.Workspace(operation);
        var existing = await projects.FindAsync(workspace, value.Metadata.Namespace, value.Metadata.Name, token);
        if (existing.IsSuccess)
        {
            if (value.Definition.Id is { } requested && existing.Value!.Id.Value != requested) return new(BootstrapResourceDisposition.Conflict);
            planning.Register(Kind, value.Metadata.Name, WorkspaceBootstrapResource.PlanningParent(value.Metadata.Namespace), resource);
            return new(BootstrapResourceDisposition.Skip);
        }
        var valid = await projects.ValidateAsync(workspace, Definition(value), token);
        if (!valid.IsSuccess) throw Invalid(valid.ErrorCode!);
        return WorkspaceBootstrapResource.Created(planning, Kind, value.Metadata.Name, value.Metadata.Namespace, resource);
    }

    public async Task<BootstrapResourceApplyResult> ApplyAsync(BootstrapResourceDocument resource, BootstrapResourceOperationContext operation, CancellationToken token)
    {
        var value = Parse(resource, operation);
        var workspace = WorkspaceBootstrapResource.Workspace(operation);
        var existing = await projects.FindAsync(workspace, value.Metadata.Namespace, value.Metadata.Name, token);
        if (existing.IsSuccess) return value.Definition.Id is { } requestedId && existing.Value!.Id.Value != requestedId
            ? BootstrapResourceApplyResult.Conflict : BootstrapResourceApplyResult.Skipped;
        var id = value.Definition.Id is { } requested ? new WorkProjectId(requested)
            : new WorkProjectId(new Guid(SHA256.HashData(Encoding.UTF8.GetBytes($"{workspace.Value:D}\n{context.PrincipalId:D}\n{value.Metadata.Namespace.Value}\n{value.Metadata.Name}")).AsSpan(0, 16)));
        var result = await projects.CreateAsync(workspace, id, value.Metadata.Name, value.Metadata.Namespace, new(id.Value, 0, Definition(value)), token);
        if (result.IsSuccess) return BootstrapResourceApplyResult.Created;
        if (result.ErrorCode == "work_project_conflict" && (await projects.FindAsync(workspace, value.Metadata.Namespace, value.Metadata.Name, token)).IsSuccess)
            return BootstrapResourceApplyResult.Skipped;
        throw Invalid(result.ErrorCode!);
    }

    private DeclarativeResourceEnvelope<DeclarativeProjectDefinition> Parse(BootstrapResourceDocument resource, BootstrapResourceOperationContext operation)
    {
        if (operation.ProfileScope != BootstrapProfileScope.Workspace || operation.ActorPrincipalId is { } actor && actor != context.PrincipalId)
            throw Invalid("work_project_not_found");
        var value = WorkspaceBootstrapResource.Parse<DeclarativeResourceEnvelope<DeclarativeProjectDefinition>>(resource);
        if (!WorkProjectDefinition.ValidName(value.Metadata.Name) || value.Definition is null || value.Definition.Id == Guid.Empty)
            throw Invalid("work_project_definition_invalid");
        return value;
    }

    private static WorkProjectDefinition Definition(DeclarativeResourceEnvelope<DeclarativeProjectDefinition> value) => new(
        value.Definition.DisplayName ?? value.Metadata.Name, value.Definition.Description,
        value.Definition.PlanIds?.Select(id => new WorkPlanId(id)).ToArray() ?? [], value.Definition.TaskIds?.Select(id => new WorkTaskId(id)).ToArray() ?? [])
        { Bindings = value.Definition.Bindings?.ToArray() ?? [] };
    private static WorkValidationException Invalid(string code) => new(code, "The Workspace Project could not be planned or applied.");
}
