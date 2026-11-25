using System.Text;
using Agentstration.Resources;
using Agentstration.Work;

namespace Agentstration.Application.Work;

public sealed record WorkDataReadResult(WorkDataSelection? Selection, IReadOnlyList<WorkDataDiagnostic> Diagnostics);

/// <summary>Resolves only explicit Work documents. It never applies configuration or contacts a provider.</summary>
public sealed class WorkDataReader(IWorkDocumentCodec documents)
{
    public WorkDataReadResult Read(WorkDataPackage package)
    {
        var errors = new List<WorkDataDiagnostic>();
        if (package.Files is not { Count: > 0 and <= WorkDataFormat.MaximumFiles }
            || package.Revision?.Length > 256 || package.Revision?.Any(char.IsControl) == true)
            return new(null, [new(WorkDataFormat.CatalogPath, "work_data_package_invalid")]);
        long bytes = 0;
        var files = new Dictionary<string, WorkDataFile>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in package.Files)
        {
            if (file is null || !WorkDataFormat.ValidPath(file.Path) || file.Content is null || !names.Add(file.Path))
                return new(null, [new(WorkDataFormat.CatalogPath, "work_data_paths_invalid")]);
            var size = Encoding.UTF8.GetByteCount(file.Content);
            bytes += size;
            if (size > WorkDataFormat.MaximumFileBytes || bytes > WorkDataFormat.MaximumPackageBytes)
                return new(null, [new(file.Path, "work_document_size_limit")]);
            files.Add(file.Path, file);
        }
        var catalog = ReadDocument<WorkDataCatalogDefinition>(WorkDataFormat.CatalogPath, "WorkCatalog");
        if (catalog is null) return new(null, errors);
        if (catalog.FormatVersion != WorkDataFormat.Version || catalog.ProjectId.Value == Guid.Empty
            || catalog.Plans is not { Count: <= WorkDataFormat.MaximumPlans }
            || catalog.Plans.Distinct(StringComparer.Ordinal).Count() != catalog.Plans.Count)
            return new(null, [new(WorkDataFormat.CatalogPath, "work_data_catalog_invalid")]);
        var plans = new List<WorkDataPlanSelection>();
        var planIds = new HashSet<WorkPlanId>();
        var taskIds = new HashSet<WorkTaskId>();
        foreach (var path in catalog.Plans)
        {
            if (!WorkDataFormat.PlanPath(path)) { errors.Add(new(WorkDataFormat.CatalogPath, "work_data_plan_path_invalid")); continue; }
            var plan = ReadDocument<WorkDataPlanDefinition>(path, "WorkPlan");
            if (plan is null) continue;
            if (!planIds.Add(plan.Id) || plan.TaskTrees is not { Count: > 0 and <= WorkDataFormat.MaximumPlans }
                || plan.TaskTrees.Distinct(StringComparer.Ordinal).Count() != plan.TaskTrees.Count)
            { errors.Add(new(path, "work_data_plan_invalid")); continue; }
            var trees = new List<WorkTaskTreeDefinition>();
            foreach (var treePath in plan.TaskTrees)
            {
                if (!WorkDataFormat.ValidPath(treePath) || !(treePath.StartsWith("plans/", StringComparison.Ordinal)
                    || treePath.StartsWith("epics/", StringComparison.Ordinal)))
                { errors.Add(new(path, "work_data_tree_path_invalid")); continue; }
                var tree = ReadDocument<WorkTaskTreeDefinition>(treePath, "WorkTaskTree");
                if (tree is not null) trees.Add(tree);
            }
            if (trees.Count != plan.TaskTrees.Count) continue;
            var normalized = WorkSharingContent.NormalizeDraft(new(plan.Id, plan.Plan, trees, []) { Issue = plan.Issue });
            if (!normalized.IsSuccess) { errors.Add(new(path, normalized.ErrorCode!)); continue; }
            foreach (var task in normalized.Value!.Trees.SelectMany(tree => tree.Tasks))
                if (!taskIds.Add(task.Id)) errors.Add(new(path, "work_data_task_identity_duplicate"));
            plans.Add(new(path, normalized.Value, 0));
        }
        if (taskIds.Count > 100) errors.Add(new(WorkDataFormat.CatalogPath, "work_data_tasks_limit"));
        WorkOrganizationDefinition? organization = null;
        if (catalog.Epics is { } epicPaths)
        {
            if (epicPaths.Count > 64 || epicPaths.Distinct(StringComparer.Ordinal).Count() != epicPaths.Count)
                errors.Add(new(WorkDataFormat.CatalogPath, "work_organization_invalid"));
            else
            {
                var epics = new List<WorkEpicDefinition>();
                foreach (var path in epicPaths)
                {
                    if (!WorkDataFormat.ValidPath(path) || !path.StartsWith("epics/", StringComparison.Ordinal) || !path.EndsWith("/epic.yaml", StringComparison.Ordinal))
                    { errors.Add(new(WorkDataFormat.CatalogPath, "work_data_epic_path_invalid")); continue; }
                    var epic = ReadDocument<WorkEpicDefinition>(path, "WorkEpic");
                    if (epic is not null) epics.Add(epic);
                }
                var normalized = new WorkOrganizationDefinition(epics).Validate();
                if (!normalized.IsSuccess) errors.Add(new(WorkDataFormat.CatalogPath, normalized.ErrorCode!));
                else organization = normalized.Value;
            }
        }
        return new(errors.Count == 0 ? new WorkDataSelection(catalog.ProjectId, plans) { Organization = organization } : null, errors);

        T? ReadDocument<T>(string path, string kind) where T : class
        {
            if (!files.TryGetValue(path, out var file)) { errors.Add(new(path, "work_data_file_missing")); return null; }
            var parsed = documents.Read<WorkDataManifest<T>>(Encoding.UTF8.GetBytes(file.Content), WorkDataFormat.MaximumFileBytes);
            if (!parsed.IsSuccess) { errors.Add(new(path, parsed.ErrorCode!)); return null; }
            var manifest = parsed.Value!;
            if (manifest.ApiVersion != ResourceApiVersions.CoreV1 || manifest.Kind != kind || manifest.Metadata is null
                || !WorkProjectDefinition.ValidName(manifest.Metadata.Name) || manifest.Definition is null)
            { errors.Add(new(path, "work_data_manifest_invalid")); return null; }
            return manifest.Definition;
        }
    }
}
