using System.Text.Json;
using Agentstration.Application.Work;
using Agentstration.Coding.Contracts;
using Agentstration.Resources;
using Agentstration.Work;

namespace Agentstration.Coding;

public interface ICodingProviderGateway
{
    Task<Result<CodingProviderResponse>> InvokeAsync(WorkspaceId workspaceId, Guid principalId,
        WorkProjectContextBinding binding, CodingProviderRequest request, CancellationToken cancellationToken);
}

public sealed class CodingRepositoryService(WorkProjectService projects, WorkTaskTreeService tasks, WorkPlanService plans,
    IWorkplaceContext context, ICodingProviderGateway providers, TimeProvider clock)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<Result<CodingRepositorySelection>> ResolveAsync(WorkspaceId workspaceId, WorkProjectId projectId,
        ResolveCodingRepositoriesRequest command, CancellationToken token)
    {
        var authorized = await AuthorizeAsync(workspaceId, projectId, new(command.TaskId), command.ExpectedTaskVersion, token);
        if (!authorized.IsSuccess) return Result<CodingRepositorySelection>.Failure(authorized.ErrorCode!);
        var selected = authorized.Value!;
        if (!ValidRepositories(command.Repositories) || selected.Task.Definition.Kind == WorkTaskKind.Atomic && command.Repositories.Count != 1)
            return Result<CodingRepositorySelection>.Failure("coding_repository_selection_invalid");
        var repositoriesResult = await ResolveBoundAsync(workspaceId, selected.Binding, command.Repositories, token);
        if (!repositoriesResult.IsSuccess) return Result<CodingRepositorySelection>.Failure(repositoriesResult.ErrorCode!);
        var repositories = repositoriesResult.Value!;
        return Result<CodingRepositorySelection>.Success(new(projectId.Value, selected.Project.Version, command.TaskId, selected.Task.Version,
            selected.Binding.Target, repositories, WorkPlanDefinition.Digest(new
            {
                ProjectId = selected.Project.Id,
                ProjectVersion = selected.Project.Version,
                TaskId = command.TaskId,
                TaskVersion = selected.Task.Version,
                selected.Binding.Target,
                Repositories = repositories
            }), clock.GetUtcNow()));
    }

    internal async Task<Result<CodingRepositoryIdentity>> ResolveAdmittedAsync(WorkspaceId workspaceId, WorkPlanId planId, Guid attemptId,
        string repository, CancellationToken token)
    {
        if (workspaceId != context.WorkspaceId || context.PrincipalId == Guid.Empty) return Result<CodingRepositoryIdentity>.Failure("coding_task_not_found");
        var plan = await plans.GetAsync(workspaceId, planId, token);
        var attempt = plan.Value?.Plan.Attempts.SingleOrDefault(value => value.Id == attemptId);
        if (!plan.IsSuccess || attempt is not { Status: WorkPlanAttemptStatus.Admitted or WorkPlanAttemptStatus.Submitted, CancellationRequested: false, ProjectContext: { } captured })
            return Result<CodingRepositoryIdentity>.Failure("coding_plan_attempt_required");
        var binding = captured.Bindings.SingleOrDefault(value => value.Binding.Purpose == WorkProjectBindingPurpose.Repository)?.Binding;
        if (binding is null) return Result<CodingRepositoryIdentity>.Failure("coding_repository_connection_required");
        var resolved = await ResolveBoundAsync(workspaceId, binding, [repository], token);
        return resolved.IsSuccess ? Result<CodingRepositoryIdentity>.Success(resolved.Value!.Single()) : Result<CodingRepositoryIdentity>.Failure(resolved.ErrorCode!);
    }

    private async Task<Result<IReadOnlyList<CodingRepositoryIdentity>>> ResolveBoundAsync(WorkspaceId workspaceId, WorkProjectContextBinding binding,
        IReadOnlyList<string> requested, CancellationToken token)
    {
        if (!ValidRepositories(requested)) return Result<IReadOnlyList<CodingRepositoryIdentity>>.Failure("coding_repository_selection_invalid");
        var key = WorkPlanDefinition.Digest(new { workspaceId, context.PrincipalId, binding.Target, Repositories = requested, InvocationId = Guid.NewGuid() });
        var input = JsonSerializer.SerializeToElement(new { repositories = requested }, Json);
        var digest = WorkPlanDefinition.Digest(input);
        var response = await providers.InvokeAsync(workspaceId, context.PrincipalId, binding,
            new(CodingProviderProtocol.ResolveRepositories, "execute", key, digest, input), token);
        if (!response.IsSuccess) return Result<IReadOnlyList<CodingRepositoryIdentity>>.Failure(response.ErrorCode!);
        var result = response.Value!;
        if (result.Status != CodingProviderStatus.Succeeded) return Result<IReadOnlyList<CodingRepositoryIdentity>>.Failure(result.ErrorCode ?? "coding_repository_unavailable");
        var repositories = result.Repositories;
        if (repositories is null || repositories.Count != requested.Count || repositories.Any(repository => !ValidIdentity(repository))
            || repositories.Select(repository => repository.Provider + ":" + repository.NativeId).Distinct(StringComparer.Ordinal).Count() != repositories.Count
            || !repositories.Select(repository => repository.Repository).Order(StringComparer.Ordinal)
                .SequenceEqual(requested.Order(StringComparer.Ordinal), StringComparer.Ordinal))
            return Result<IReadOnlyList<CodingRepositoryIdentity>>.Failure("coding_repository_response_invalid");
        return Result<IReadOnlyList<CodingRepositoryIdentity>>.Success(repositories);
    }

    public async Task<Result<CodingPullRequestResponse>> InvokePullRequestAsync(WorkspaceId workspaceId, WorkProjectId projectId,
        InvokeCodingPullRequestRequest command, CancellationToken token)
    {
        if (command.Id == Guid.Empty || command.ExpectedTaskVersion < 1 || command.ExpectedProjectVersion < 1 || command.Connection is null || command.Input is null
            || command.Operation is not (CodingProviderProtocol.CreatePullRequest or CodingProviderProtocol.ReadPullRequest)
            || !ValidRepositories([command.Input.Repository]) || command.Input.Title?.Length is > 512 || command.Input.Body?.Length is > 32_768
            || command.Input.Number?.Length is > 32 || command.Input.Head?.Length is > 256 || command.Input.Base?.Length is > 256
            || command.Input.ExpectedHeadRevision?.Length is > 128)
            return Result<CodingPullRequestResponse>.Failure("coding_pull_request_invalid");
        var authorized = await AuthorizeAsync(workspaceId, projectId, new(command.TaskId), command.Recover ? null : command.ExpectedTaskVersion, token);
        if (!authorized.IsSuccess) return Result<CodingPullRequestResponse>.Failure(authorized.ErrorCode!);
        var selected = authorized.Value!;
        if (selected.Binding.Target != command.Connection || !command.Recover && selected.Project.Version != command.ExpectedProjectVersion)
            return Result<CodingPullRequestResponse>.Failure("coding_connection_changed");
        if (selected.Task.Definition.Kind != WorkTaskKind.Atomic) return Result<CodingPullRequestResponse>.Failure("coding_atomic_task_required");
        if (command.Operation == CodingProviderProtocol.CreatePullRequest
            ? string.IsNullOrWhiteSpace(command.Input.Title) || string.IsNullOrWhiteSpace(command.Input.Head) || string.IsNullOrWhiteSpace(command.Input.Base)
                || string.IsNullOrWhiteSpace(command.Input.ExpectedHeadRevision) || command.Input.Number is not null
            : string.IsNullOrWhiteSpace(command.Input.Number)) return Result<CodingPullRequestResponse>.Failure("coding_pull_request_invalid");
        var key = WorkPlanDefinition.Digest(new { workspaceId, context.PrincipalId, projectId, command.TaskId, command.Id });
        var input = JsonSerializer.SerializeToElement(command.Input, Json);
        var digest = WorkPlanDefinition.Digest(new { command.Operation, command.TaskId, command.ExpectedTaskVersion, command.Connection, command.ExpectedProjectVersion, Input = command.Input });
        var response = await providers.InvokeAsync(workspaceId, context.PrincipalId, selected.Binding,
            new(command.Operation, command.Recover ? "recover" : "execute", key, digest, input), token);
        if (!response.IsSuccess) return Result<CodingPullRequestResponse>.Failure(response.ErrorCode!);
        var result = response.Value!;
        if (result.Status == CodingProviderStatus.Succeeded && (result.PullRequest is not { } pr || pr.Repository != command.Input.Repository
            || !ValidPullRequest(pr)
            || command.Operation == CodingProviderProtocol.CreatePullRequest && (pr.Head != command.Input.Head || pr.Base != command.Input.Base || pr.Draft != command.Input.Draft || pr.HeadRevision != command.Input.ExpectedHeadRevision)))
            return Result<CodingPullRequestResponse>.Failure("coding_pull_request_response_invalid");
        return Result<CodingPullRequestResponse>.Success(new(command.Id, projectId.Value, command.TaskId, command.ExpectedTaskVersion, selected.Binding.Target, result));
    }

    private async Task<Result<CodingInvocation>> AuthorizeAsync(WorkspaceId workspaceId, WorkProjectId projectId, WorkTaskId taskId,
        long? expectedVersion, CancellationToken token)
    {
        if (workspaceId.Value == Guid.Empty || workspaceId != context.WorkspaceId || context.PrincipalId == Guid.Empty)
            return Result<CodingInvocation>.Failure("coding_task_not_found");
        var project = await projects.GetAsync(workspaceId, projectId, token);
        if (!project.IsSuccess) return Result<CodingInvocation>.Failure(project.ErrorCode!);
        var tree = await tasks.GetAsync(workspaceId, taskId, token);
        if (!tree.IsSuccess) return Result<CodingInvocation>.Failure(tree.ErrorCode!);
        var task = tree.Value!.Tasks.Single(value => value.Definition.Id == taskId);
        if (expectedVersion is { } expected && task.Version != expected) return Result<CodingInvocation>.Failure("coding_task_changed");
        var associated = project.Value!.Definition.TaskIds.Contains(taskId);
        var visited = new HashSet<WorkTaskId> { taskId };
        for (var parent = task.Definition.ParentTaskId; !associated && parent is { } id;)
        {
            if (!visited.Add(id) || visited.Count > WorkTaskTreeDefinition.MaximumDepth + 1)
                return Result<CodingInvocation>.Failure("coding_task_hierarchy_invalid");
            associated = project.Value.Definition.TaskIds.Contains(id);
            if (associated) break;
            var ancestor = await tasks.GetAsync(workspaceId, id, token);
            if (!ancestor.IsSuccess) break;
            parent = ancestor.Value!.Tasks.Single(value => value.Definition.Id == id).Definition.ParentTaskId;
        }
        foreach (var planId in project.Value.Definition.PlanIds)
        {
            if (associated) break;
            var plan = await plans.GetAsync(workspaceId, planId, token);
            associated = plan.IsSuccess && plan.Value!.Revision.Definition.TaskIds.Contains(taskId);
        }
        if (!associated) return Result<CodingInvocation>.Failure("coding_task_project_mismatch");
        var binding = project.Value.Definition.Bindings.SingleOrDefault(value => value.Purpose == WorkProjectBindingPurpose.Repository);
        if (binding is null || binding.Target.Address.Kind != "Tool") return Result<CodingInvocation>.Failure("coding_repository_connection_required");
        return Result<CodingInvocation>.Success(new(project.Value, task, binding));
    }

    private static bool ValidRepositories(IReadOnlyList<string>? repositories) => repositories is { Count: >= 1 and <= 16 }
        && repositories.All(value => !string.IsNullOrWhiteSpace(value) && value.Length <= 512 && !value.Any(char.IsControl))
        && repositories.Distinct(StringComparer.Ordinal).Count() == repositories.Count;
    private static bool ValidIdentity(CodingRepositoryIdentity? value) => value is not null && value.Provider is { Length: > 0 and <= 64 }
        && value.NativeId is { Length: > 0 and <= 256 } && value.Repository is { Length: > 0 and <= 512 }
        && value.DefaultBranch is { Length: > 0 and <= 256 } && value.Revision is { Length: > 0 and <= 128 }
        && SafeUrl(value.CloneUrl) && SafeUrl(value.WebUrl) && value.Capabilities is { Count: <= 32 }
        && value.Capabilities.All(capability => capability is { Length: > 0 and <= 128 });
    private static bool ValidPullRequest(CodingPullRequestSnapshot value) => value.Provider is { Length: > 0 and <= 64 }
        && value.Repository is { Length: > 0 and <= 512 } && value.NativeId is { Length: > 0 and <= 256 } && value.Number is { Length: > 0 and <= 32 }
        && SafeUrl(value.Url) && value.State is { Length: > 0 and <= 64 } && value.Head is { Length: > 0 and <= 256 }
        && value.HeadRevision is { Length: > 0 and <= 128 } && value.Base is { Length: > 0 and <= 256 }
        && value.BaseRevision is { Length: > 0 and <= 128 } && value.Revision is { Length: > 0 and <= 128 };
    private static bool SafeUrl(Uri? value) => value is { IsAbsoluteUri: true } && value.Scheme is "https" or "http" && string.IsNullOrEmpty(value.UserInfo);
    private sealed record CodingInvocation(WorkProjectSnapshot Project, WorkTaskTreeNode Task, WorkProjectContextBinding Binding);
}
