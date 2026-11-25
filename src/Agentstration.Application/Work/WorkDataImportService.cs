using System.Diagnostics;
using Agentstration.Resources;
using Agentstration.Work;
using Agentstration.Work.Storage.Abstractions;

namespace Agentstration.Application.Work;

public sealed record WorkDataSource(WorkDataPackage? Package = null, string? Revision = null);
public sealed record ImportWorkDataCommand(WorkDataImportId Id, WorkDataSource Source, string ReviewedDigest);

/// <summary>Reviewed, durable preparation of draft Work. Execution and configuration remain separate.</summary>
public sealed class WorkDataImportService(IWorkDataRepository repository, WorkProjectService projects,
    WorkPlanService plans, WorkTaskTreeService trees, IWorkProjectSharingRepository mappings,
    IWorkProjectDataGateway source, WorkDataReader reader, WorkProjectOrganizationService organization, IWorkplaceContext context, TimeProvider clock)
{
    private static readonly ActivitySource Activity = new("Agentstration.Work.Data");

    public Task InitializeAsync(CancellationToken token) => repository.InitializeAsync(token);

    public async Task<Result<WorkDataPreview>> PreviewAsync(WorkspaceId workspace, WorkProjectId projectId, WorkDataSource input, CancellationToken token)
    {
        var project = await projects.GetAsync(workspace, projectId, token);
        if (!project.IsSuccess) return Result<WorkDataPreview>.Failure(project.ErrorCode!);
        var package = await LoadAsync(project.Value!, input, token);
        if (!package.IsSuccess) return Result<WorkDataPreview>.Failure(package.ErrorCode!);
        return Result<WorkDataPreview>.Success((await PrepareAsync(project.Value!, package.Value!, token)).Preview);
    }

    public async Task<Result<WorkDataImport>> ImportAsync(WorkspaceId workspace, WorkProjectId projectId, ImportWorkDataCommand command, CancellationToken token)
    {
        using var activity = Activity.StartActivity("work.data.import");
        activity?.SetTag("workspace.id", workspace.ToString()); activity?.SetTag("project.id", projectId.ToString());
        activity?.SetTag("work.data.import.id", command.Id.ToString());
        var project = await projects.GetAsync(workspace, projectId, token);
        if (!project.IsSuccess) return Failure(project.ErrorCode!);
        if (command.Id.Value == Guid.Empty || command.Source is null || !WorkSharingFormat.Digest(command.ReviewedDigest))
            return Failure("work_data_command_invalid");
        if (command.Source.Package is { } supplied && reader.Read(supplied).Selection is null)
            return Failure("work_data_package_invalid");
        var normalizedSource = command.Source.Package is { } incoming
            ? command.Source with { Package = incoming with { Files = incoming.Files.OrderBy(file => file.Path, StringComparer.Ordinal).ToArray() } } : command.Source;
        var requestDigest = WorkPlanDefinition.Digest(new { Source = normalizedSource, command.ReviewedDigest });
        var prior = await repository.GetImportAsync(workspace, context.PrincipalId, projectId, command.Id, token);
        if (prior is not null) return prior.RequestDigest == requestDigest ? await RunAsync(prior, token) : Failure("work_data_command_conflict");
        var package = await LoadAsync(project.Value!, command.Source, token);
        if (!package.IsSuccess) return Failure(package.ErrorCode!);
        var prepared = await PrepareAsync(project.Value!, package.Value!, token);
        if (!prepared.Preview.CanImport || prepared.Preview.Digest != command.ReviewedDigest)
            return Failure("work_data_review_stale");
        var now = clock.GetUtcNow();
        var operation = new WorkDataImport(command.Id, workspace, context.PrincipalId, projectId, project.Value!.Version,
            ContextDigest(project.Value), requestDigest, command.ReviewedDigest, prepared.Preview.SourceDigest!, package.Value!.Revision,
            prepared.Selection!, prepared.Tasks, [], WorkDataImportStatus.Pending, 1, now, now);
        if (!await repository.SaveImportAsync(operation, 0, token))
        {
            prior = await repository.GetImportAsync(workspace, context.PrincipalId, projectId, command.Id, token);
            return prior?.RequestDigest == requestDigest ? await RunAsync(prior, token) : Failure("work_data_review_stale");
        }
        return await RunAsync(operation, token);
    }

    public async Task<Result<WorkDataImport>> GetAsync(WorkspaceId workspace, WorkProjectId projectId, WorkDataImportId id, CancellationToken token)
    {
        var project = await projects.GetAsync(workspace, projectId, token);
        if (!project.IsSuccess) return Failure(project.ErrorCode!);
        var operation = await repository.GetImportAsync(workspace, context.PrincipalId, projectId, id, token);
        return operation is null ? Failure("work_data_import_not_found") : Result<WorkDataImport>.Success(operation);
    }

    public async Task<Result<WorkDataImport>> ResumeAsync(WorkspaceId workspace, WorkProjectId projectId, WorkDataImportId id, CancellationToken token)
    {
        var loaded = await GetAsync(workspace, projectId, id, token);
        return loaded.IsSuccess ? await RunAsync(loaded.Value!, token) : loaded;
    }

    private async Task<Result<WorkDataPackage>> LoadAsync(WorkProjectSnapshot project, WorkDataSource input, CancellationToken token)
    {
        if (input is null || input.Package is not null && input.Revision is not null)
            return Result<WorkDataPackage>.Failure("work_data_source_invalid");
        if (input.Package is { } package) return Result<WorkDataPackage>.Success(package);
        var connection = project.Definition.Sharing;
        var binding = project.Definition.Bindings.SingleOrDefault(value => value.Name == connection?.BindingName && value.Purpose == WorkProjectBindingPurpose.Sharing);
        return connection is null || binding is null ? Result<WorkDataPackage>.Failure("sharing_connection_required")
            : await source.ReadDataAsync(project.WorkspaceId, context.PrincipalId, binding, connection, input.Revision, token);
    }

    private sealed record Prepared(WorkDataPreview Preview, WorkDataSelection? Selection, IReadOnlyList<WorkDataTaskObservation> Tasks);

    private async Task<Prepared> PrepareAsync(WorkProjectSnapshot project, WorkDataPackage package, CancellationToken token)
    {
        var parsed = reader.Read(package);
        var errors = parsed.Diagnostics.ToList();
        var selected = new List<WorkDataPlanSelection>();
        var observed = new List<WorkDataTaskObservation>();
        if (parsed.Selection is { } selection)
        {
            if (project.Definition.Sharing is { } connection && selection.ProjectId != connection.ProjectId)
                errors.Add(new(WorkDataFormat.CatalogPath, "work_data_project_mismatch"));
            foreach (var proposed in selection.Plans)
            {
                var id = proposed.Plan.PlanId;
                var mapped = await mappings.FindPlanProjectAsync(project.WorkspaceId, context.PrincipalId, id, token);
                if (mapped is not null && mapped != project.Id) errors.Add(new(proposed.Path, "sharing_plan_project_conflict"));
                var local = await plans.GetAsync(project.WorkspaceId, id, token);
                if (local.IsSuccess && WorkPlanDefinition.Digest(local.Value!.Revision.Definition) != WorkPlanDefinition.Digest(proposed.Plan.Definition))
                    errors.Add(new(proposed.Path, "sharing_local_plan_conflict"));
                else if (!local.IsSuccess && local.ErrorCode != "work_plan_not_found") errors.Add(new(proposed.Path, local.ErrorCode!));
                selected.Add(proposed with { LocalVersion = local.IsSuccess ? local.Value!.Plan.Version : 0 });
                foreach (var tree in proposed.Plan.Trees)
                {
                    var existing = await trees.GetAsync(project.WorkspaceId, tree.RootTaskId, token);
                    if (existing.IsSuccess && WorkPlanDefinition.Digest(existing.Value!.Tasks.Select(value => value.Definition).OrderBy(value => value.Id.Value).ToArray())
                        != WorkPlanDefinition.Digest(tree.Tasks)) errors.Add(new(proposed.Path, "work_data_local_task_conflict"));
                    foreach (var task in tree.Tasks)
                    {
                        var node = await trees.GetAsync(project.WorkspaceId, task.Id, token);
                        var actual = node.IsSuccess ? node.Value!.Tasks.Single(value => value.Definition.Id == task.Id) : null;
                        if (actual is not null && (!existing.IsSuccess || actual.Definition != task))
                            errors.Add(new(proposed.Path, "work_data_local_task_conflict"));
                        else if (!node.IsSuccess && node.ErrorCode != "work_task_not_found") errors.Add(new(proposed.Path, node.ErrorCode!));
                        observed.Add(new(task.Id, actual?.Version ?? 0));
                    }
                }
            }
            if (project.Definition.PlanIds.Union(selected.Select(value => value.Plan.PlanId)).Count() > WorkProjectDefinition.MaximumMembers)
                errors.Add(new(WorkDataFormat.CatalogPath, "work_project_definition_invalid"));
        }
        var sourceDigest = parsed.Selection is null ? null : WorkPlanDefinition.Digest(package.Files.OrderBy(value => value.Path, StringComparer.Ordinal).ToArray());
        var resolved = parsed.Selection is null ? null : parsed.Selection with { Plans = selected };
        if (resolved?.Organization is { } proposedOrganization)
        {
            var currentOrganization = await organization.GetAsync(project.WorkspaceId, project.Id, token);
            if (!currentOrganization.IsSuccess) errors.Add(new(WorkDataFormat.CatalogPath, currentOrganization.ErrorCode!));
            else resolved = resolved with { OrganizationVersion = currentOrganization.Value!.Version };
            var members = project.Definition.PlanIds.Union(selected.Select(value => value.Plan.PlanId)).ToHashSet();
            if (proposedOrganization.Epics.SelectMany(epic => epic.PlanIds).Any(id => !members.Contains(id)))
                errors.Add(new(WorkDataFormat.CatalogPath, "work_organization_plan_not_associated"));
        }
        var digest = errors.Count != 0 ? null : WorkPlanDefinition.Digest(new
        {
            project.WorkspaceId, project.OwnerPrincipalId, Project = project.Id, project.Version,
            Context = ContextDigest(project), package.Revision, Source = sourceDigest, Selection = resolved, Tasks = observed
        });
        return new(new WorkDataPreview(errors.Count == 0, project.Version, package.Revision, sourceDigest, digest, selected, errors)
        { Organization = resolved?.Organization, OrganizationVersion = resolved?.OrganizationVersion ?? 0 }, resolved, observed);
    }

    private async Task<Result<WorkDataImport>> RunAsync(WorkDataImport operation, CancellationToken token)
    {
        if (operation.Status == WorkDataImportStatus.Succeeded) return Result<WorkDataImport>.Success(operation);
        try
        {
            foreach (var selected in operation.Selection.Plans.Where(value => !operation.ImportedPlans.Contains(value.Plan.PlanId)))
            {
                token.ThrowIfCancellationRequested();
                var current = await projects.GetAsync(operation.WorkspaceId, operation.ProjectId, token);
                if (!current.IsSuccess || ContextDigest(current.Value!) != operation.ProjectContextDigest)
                    return await PartialAsync(operation, "work_data_project_changed", token);
                var planId = selected.Plan.PlanId;
                var local = await plans.GetAsync(operation.WorkspaceId, planId, token);
                if (selected.LocalVersion > 0 && (!local.IsSuccess || local.Value!.Plan.Version != selected.LocalVersion
                    || WorkPlanDefinition.Digest(local.Value.Revision.Definition) != WorkPlanDefinition.Digest(selected.Plan.Definition)))
                    return await PartialAsync(operation, "sharing_local_plan_conflict", token);
                var pendingTasks = selected.Plan.Trees.SelectMany(tree => tree.Tasks).Select(task => task.Id).ToHashSet();
                foreach (var expected in operation.Tasks.Where(task => task.Version > 0 && pendingTasks.Contains(task.Id)))
                {
                    var loadedTask = await trees.GetAsync(operation.WorkspaceId, expected.Id, token);
                    if (!loadedTask.IsSuccess || loadedTask.Value!.Tasks.Single(node => node.Definition.Id == expected.Id).Version != expected.Version)
                        return await PartialAsync(operation, "work_data_local_work_changed", token);
                }
                foreach (var tree in selected.Plan.Trees)
                {
                    var created = await trees.CreateAsync(operation.WorkspaceId, tree, token);
                    if (!created.IsSuccess) return await PartialAsync(operation, created.ErrorCode!, token);
                }
                var command = Command(operation.Id, planId, "create");
                if (selected.LocalVersion == 0)
                    local = await plans.CreateAsync(operation.WorkspaceId, planId, new(command, 0, selected.Plan.Definition), token);
                if (!local.IsSuccess) return await PartialAsync(operation, local.ErrorCode!, token);
                // A replay returns the current plan, which may have changed after creation but before progress was saved.
                if (WorkPlanDefinition.Digest(local.Value!.Revision.Definition) != WorkPlanDefinition.Digest(selected.Plan.Definition))
                    return await PartialAsync(operation, "sharing_local_plan_conflict", token);
                current = await projects.GetAsync(operation.WorkspaceId, operation.ProjectId, token);
                if (!current.IsSuccess || ContextDigest(current.Value!) != operation.ProjectContextDigest)
                    return await PartialAsync(operation, "work_data_project_changed", token);
                if (!current.Value!.Definition.PlanIds.Contains(planId))
                {
                    var saved = await projects.UpdateAsync(operation.WorkspaceId, operation.ProjectId,
                        new(Command(operation.Id, planId, "associate"), current.Value.Version,
                            current.Value.Definition with { PlanIds = [.. current.Value.Definition.PlanIds, planId] }), token);
                    if (!saved.IsSuccess) return await PartialAsync(operation, saved.ErrorCode!, token);
                }
                var next = operation with { ImportedPlans = [.. operation.ImportedPlans, planId], Version = operation.Version + 1,
                    Status = WorkDataImportStatus.Pending, UpdatedAt = clock.GetUtcNow(), ErrorCode = null };
                if (!await repository.SaveImportAsync(next, operation.Version, token)) return Failure("work_data_operation_conflict");
                operation = next;
            }
            if (operation.Selection.Organization is { } proposedOrganization)
            {
                var grouping = await organization.UpdateAsync(operation.WorkspaceId, operation.ProjectId,
                    new(new Guid(Convert.FromHexString(WorkPlanDefinition.Digest(new { operation.Id, Organization = true })).AsSpan(0, 16)),
                        operation.Selection.OrganizationVersion, proposedOrganization), token);
                if (!grouping.IsSuccess) return await PartialAsync(operation, grouping.ErrorCode!, token);
            }
            var completed = operation with { Status = WorkDataImportStatus.Succeeded, Version = operation.Version + 1,
                UpdatedAt = clock.GetUtcNow(), ErrorCode = null };
            return await repository.SaveImportAsync(completed, operation.Version, token)
                ? Result<WorkDataImport>.Success(completed) : Failure("work_data_operation_conflict");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10), clock);
            return await PartialAsync(operation, "work_data_import_cancelled", deadline.Token);
        }
    }

    private async Task<Result<WorkDataImport>> PartialAsync(WorkDataImport operation, string error, CancellationToken token)
    {
        var changed = operation with { Status = WorkDataImportStatus.Partial, ErrorCode = error,
            Version = operation.Version + 1, UpdatedAt = clock.GetUtcNow() };
        return await repository.SaveImportAsync(changed, operation.Version, token)
            ? Result<WorkDataImport>.Success(changed) : Failure("work_data_operation_conflict");
    }

    private static string ContextDigest(WorkProjectSnapshot project) => WorkPlanDefinition.Digest(new { project.Definition.Sharing, project.Definition.Bindings });
    private static Guid Command(WorkDataImportId operation, WorkPlanId plan, string action) =>
        new(Convert.FromHexString(WorkPlanDefinition.Digest(new { operation, plan, action })).AsSpan(0, 16));
    private static Result<WorkDataImport> Failure(string code) => Result<WorkDataImport>.Failure(code);
}
