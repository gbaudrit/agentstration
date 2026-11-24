using Agentstration.Resources;
using Agentstration.Work;
using Agentstration.Work.Storage.Abstractions;

namespace Agentstration.Application.Work;

public interface IWorkProjectBindingResolver
{
    Task<Result<WorkProjectResolvedBinding>> ResolveAsync(WorkspaceId workspaceId, Guid principalId,
        WorkProjectContextBinding binding, CancellationToken cancellationToken);
}

public sealed class WorkProjectContextService(IWorkProjectRepository projects, IWorkProjectBindingResolver resolver,
    IWorkplaceContext context, TimeProvider clock)
{
    public async Task<Result<IReadOnlyList<WorkProjectResolvedBinding>>> ValidateAsync(WorkspaceId workspaceId,
        WorkProjectDefinition definition, CancellationToken token)
    {
        if (!Owns(workspaceId)) return Result<IReadOnlyList<WorkProjectResolvedBinding>>.Failure("work_project_not_found");
        if (!WorkProjectContextRules.Valid(definition.Bindings))
            return Result<IReadOnlyList<WorkProjectResolvedBinding>>.Failure("work_project_bindings_invalid");
        var resolved = new List<WorkProjectResolvedBinding>();
        foreach (var binding in definition.Bindings)
        {
            var result = await resolver.ResolveAsync(workspaceId, context.PrincipalId, binding, token);
            if (!result.IsSuccess) return Result<IReadOnlyList<WorkProjectResolvedBinding>>.Failure(result.ErrorCode!);
            if (result.Value!.Binding != binding || result.Value.ETag.Length > 256
                || result.Value.DiscoverDecision.Length > 128 || result.Value.UseDecision.Length > 128)
                return Result<IReadOnlyList<WorkProjectResolvedBinding>>.Failure("work_project_binding_resolution_invalid");
            resolved.Add(result.Value);
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
            availability.Add(new(binding, result.IsSuccess, result.IsSuccess ? "resolved" : result.ErrorCode!));
        }
        return Result<WorkProjectContextView>.Success(new(projectId, project.Version, availability));
    }

    public async Task<Result<WorkProjectContextSnapshot>> CaptureAsync(WorkspaceId workspaceId, WorkProjectId projectId,
        WorkPlanId planId, CancellationToken token)
    {
        var project = Owns(workspaceId) ? await projects.GetAsync(workspaceId, context.PrincipalId, projectId, token) : null;
        if (project is null) return Result<WorkProjectContextSnapshot>.Failure("work_project_not_found");
        if (!project.Definition.PlanIds.Contains(planId)) return Result<WorkProjectContextSnapshot>.Failure("work_project_plan_not_associated");
        var resolved = await ValidateAsync(workspaceId, project.Definition, token);
        return !resolved.IsSuccess ? Result<WorkProjectContextSnapshot>.Failure(resolved.ErrorCode!)
            : Result<WorkProjectContextSnapshot>.Success(new(projectId, project.Version,
                WorkPlanDefinition.Digest(new { project.Id, project.Version, Bindings = resolved.Value! }), resolved.Value!, clock.GetUtcNow()));
    }

    private bool Owns(WorkspaceId workspaceId) => workspaceId.Value != Guid.Empty && workspaceId == context.WorkspaceId && context.PrincipalId != Guid.Empty;
}
