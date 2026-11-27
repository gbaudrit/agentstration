using Agentstration.Resources;
using Agentstration.Work;
using Agentstration.Work.Storage.Abstractions;

namespace Agentstration.Application.Work;

public interface IWorkProjectBindingResolver
{
    Task<Result<WorkProjectResolvedBinding>> ResolveAsync(WorkspaceId workspaceId, Guid principalId,
        WorkProjectContextBinding binding, CancellationToken cancellationToken);
    Task<Result<WorkProjectConnectionEvidence>> InspectConnectionAsync(WorkspaceId workspaceId, Guid principalId,
        WorkProjectConnectionSelection selection, CancellationToken cancellationToken)
        => Task.FromResult(Result<WorkProjectConnectionEvidence>.Failure("work_project_connection_unsupported"));
    Task<Result<WorkProjectResolvedBinding>> ResolveConnectionAsync(WorkspaceId workspaceId, Guid principalId,
        WorkProjectConnectionSelection selection, CancellationToken cancellationToken)
        => Task.FromResult(Result<WorkProjectResolvedBinding>.Failure("work_project_connection_unsupported"));
}

public sealed class WorkProjectContextService(IWorkProjectRepository projects, IWorkProjectBindingResolver resolver,
    IWorkplaceContext context, TimeProvider clock)
{
    public async Task<Result<IReadOnlyList<WorkProjectResolvedBinding>>> ValidateAsync(WorkspaceId workspaceId,
        WorkProjectDefinition definition, CancellationToken token)
        => await ResolveSelectedAsync(workspaceId, definition, definition.ConnectionAssignments?.Select(value => value.Name).ToArray() ?? [], token);

    private async Task<Result<IReadOnlyList<WorkProjectResolvedBinding>>> ResolveSelectedAsync(WorkspaceId workspaceId,
        WorkProjectDefinition definition, IReadOnlyList<string> selections, CancellationToken token)
    {
        if (!Owns(workspaceId)) return Result<IReadOnlyList<WorkProjectResolvedBinding>>.Failure("work_project_not_found");
        if (!WorkProjectContextRules.Valid(definition.Bindings) || !WorkProjectConnectionRules.Valid(definition))
            return Result<IReadOnlyList<WorkProjectResolvedBinding>>.Failure("work_project_bindings_invalid");
        var resolved = new List<WorkProjectResolvedBinding>();
        foreach (var binding in definition.Bindings)
        {
            var result = await resolver.ResolveAsync(workspaceId, context.PrincipalId, binding, token);
            if (!result.IsSuccess) return Result<IReadOnlyList<WorkProjectResolvedBinding>>.Failure(result.ErrorCode!);
            if (result.Value!.Binding != binding || result.Value.ETag.Length > 256
                || result.Value.DiscoverDecision.Length > 128 || result.Value.UseDecision.Length > 128
                || !WorkProjectContextRules.ValidEvidence(binding, result.Value.Contract))
                return Result<IReadOnlyList<WorkProjectResolvedBinding>>.Failure("work_project_binding_resolution_invalid");
            resolved.Add(result.Value);
        }
        if (selections.Count > 32 || selections.Distinct(StringComparer.Ordinal).Count() != selections.Count
            || selections.Any(value => !WorkProjectDefinition.ValidName(value)))
            return Result<IReadOnlyList<WorkProjectResolvedBinding>>.Failure("work_project_connection_selection_invalid");
        foreach (var name in selections.Order(StringComparer.Ordinal))
        {
            var requirement = definition.ConnectionRequirements.SingleOrDefault(value => value.Name == name);
            var assignment = definition.ConnectionAssignments.SingleOrDefault(value => value.Name == name);
            if (requirement is null) return Result<IReadOnlyList<WorkProjectResolvedBinding>>.Failure("work_project_connection_unknown");
            if (assignment is null) return Result<IReadOnlyList<WorkProjectResolvedBinding>>.Failure("work_project_connection_unconfigured");
            var selected = await resolver.ResolveConnectionAsync(workspaceId, context.PrincipalId, new(requirement, assignment), token);
            if (!selected.IsSuccess) return Result<IReadOnlyList<WorkProjectResolvedBinding>>.Failure(selected.ErrorCode!);
            if (selected.Value!.Binding.Connection is null || WorkPlanDefinition.Digest(selected.Value.Binding.Connection) != WorkPlanDefinition.Digest(new WorkProjectConnectionSelection(requirement, assignment))
                || !WorkProjectConnectionRules.Evidence(selected.Value) || !WorkProjectContextRules.ValidEvidence(selected.Value!.Binding, selected.Value.Contract))
                return Result<IReadOnlyList<WorkProjectResolvedBinding>>.Failure("work_project_binding_resolution_invalid");
            resolved.Add(selected.Value);
        }
        return Result<IReadOnlyList<WorkProjectResolvedBinding>>.Success(resolved);
    }

    public async Task<Result<WorkProjectContextView>> GetAvailabilityAsync(WorkspaceId workspaceId, WorkProjectId projectId, CancellationToken token)
    {
        var project = Owns(workspaceId) ? await projects.GetAsync(workspaceId, context.PrincipalId, projectId, token) : null;
        if (project is null) return Result<WorkProjectContextView>.Failure("work_project_not_found");
        var availability = new List<WorkProjectBindingAvailability>();
        foreach (var binding in project.Definition.Bindings)
        {
            var result = await resolver.ResolveAsync(workspaceId, context.PrincipalId, binding, token);
            availability.Add(new(binding, result.IsSuccess, result.IsSuccess ? "resolved" : result.ErrorCode!) { Contract = result.IsSuccess ? result.Value!.Contract : null });
        }
        var connections = new List<WorkProjectConnectionAvailability>();
        foreach (var need in project.Definition.ConnectionRequirements)
        {
            var assignment = project.Definition.ConnectionAssignments.SingleOrDefault(value => value.Name == need.Name);
            if (assignment is null) { connections.Add(new(need, null, false, "work_project_connection_unconfigured")); continue; }
            var result = await resolver.ResolveConnectionAsync(workspaceId, context.PrincipalId, new(need, assignment), token);
            connections.Add(new(need, assignment, result.IsSuccess, result.IsSuccess ? "resolved" : result.ErrorCode!,
                result.Value?.Contract, result.Value?.Service));
        }
        return Result<WorkProjectContextView>.Success(new(projectId, project.Version, availability) { Connections = connections });
    }

    public async Task<Result<WorkProjectContextSnapshot>> CaptureAsync(WorkspaceId workspaceId, WorkProjectId projectId,
        WorkPlanId planId, CancellationToken token, IReadOnlyList<string>? connections = null)
    {
        var project = Owns(workspaceId) ? await projects.GetAsync(workspaceId, context.PrincipalId, projectId, token) : null;
        if (project is null) return Result<WorkProjectContextSnapshot>.Failure("work_project_not_found");
        if (!project.Definition.PlanIds.Contains(planId)) return Result<WorkProjectContextSnapshot>.Failure("work_project_plan_not_associated");
        var resolved = await ResolveSelectedAsync(workspaceId, project.Definition, connections ?? [], token);
        if (!resolved.IsSuccess) return Result<WorkProjectContextSnapshot>.Failure(resolved.ErrorCode!);
        var digest = project.Definition.Sharing is null
            ? WorkPlanDefinition.Digest(new { project.Id, project.Version, Bindings = resolved.Value! })
            : WorkPlanDefinition.Digest(new { project.Id, project.Version, Bindings = resolved.Value!, project.Definition.Sharing });
        return Result<WorkProjectContextSnapshot>.Success(new(projectId, project.Version, digest, resolved.Value!, clock.GetUtcNow())
        { Sharing = project.Definition.Sharing });
    }

    public async Task<Result<WorkProjectResolvedBinding>> RevalidateAsync(WorkspaceId workspaceId,
        WorkProjectResolvedBinding captured, CancellationToken token)
    {
        if (!Owns(workspaceId)) return Result<WorkProjectResolvedBinding>.Failure("work_project_not_found");
        var current = await resolver.ResolveAsync(workspaceId, context.PrincipalId, captured.Binding, token);
        if (!current.IsSuccess) return current;
        if (WorkPlanDefinition.Digest(current.Value) != WorkPlanDefinition.Digest(captured))
            return Result<WorkProjectResolvedBinding>.Failure("work_project_contract_changed");
        return current;
    }

    private bool Owns(WorkspaceId workspaceId) => workspaceId.Value != Guid.Empty && workspaceId == context.WorkspaceId && context.PrincipalId != Guid.Empty;
}
