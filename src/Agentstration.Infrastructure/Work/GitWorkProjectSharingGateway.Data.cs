using System.Text;
using Agentstration.Resources;
using Agentstration.Work;

namespace Agentstration.Infrastructure.Work;

public sealed partial class GitWorkProjectSharingGateway
{
    public async Task<Result<WorkDataPackage>> ReadDataAsync(WorkspaceId workspaceId, Guid principalId, WorkProjectContextBinding binding,
        WorkProjectSharingConnection connection, string? revision, CancellationToken token)
    {
        if (revision is not null && !Revision(revision)) return Failure("sharing_revision_invalid");
        var digest = WorkPlanDefinition.Digest(new { connection.ProjectId, connection.Location, revision, Data = true });
        var key = WorkPlanDefinition.Digest(new { Read = digest });
        var resolved = await InvokeAsync(workspaceId, principalId, binding, connection, "resolve", "execute", key, digest, new { }, token);
        if (!resolved.IsSuccess) return Failure(resolved.ErrorCode!);
        if (resolved.Value!.Status != "Succeeded" || resolved.Value.Revision is null) return Failure("work_data_source_unavailable");
        revision ??= resolved.Value.Revision;
        var files = new Dictionary<string, WorkDataFile>(StringComparer.Ordinal);
        long totalBytes = 0;
        try
        {
            var error = await ReadFilesAsync([WorkDataFormat.CatalogPath]);
            if (error is not null) return Failure(error);
            var catalog = documents.Read<WorkDataManifest<WorkDataCatalogDefinition>>(Encoding.UTF8.GetBytes(files[WorkDataFormat.CatalogPath].Content), WorkDataFormat.MaximumFileBytes);
            if (!catalog.IsSuccess || catalog.Value!.Definition?.Plans is not { Count: > 0 and <= WorkDataFormat.MaximumPlans } paths
                || paths.Any(path => !WorkDataFormat.PlanPath(path))) return Failure("work_data_catalog_invalid");
            error = await ReadFilesAsync(paths);
            if (error is not null) return Failure(error);
            var treePaths = new HashSet<string>(StringComparer.Ordinal);
            foreach (var path in paths)
            {
                var plan = documents.Read<WorkDataManifest<WorkDataPlanDefinition>>(Encoding.UTF8.GetBytes(files[path].Content), WorkDataFormat.MaximumFileBytes);
                if (!plan.IsSuccess || plan.Value!.Definition?.TaskTrees is not { Count: > 0 and <= WorkDataFormat.MaximumPlans } references
                    || references.Any(value => !WorkDataFormat.ValidPath(value))) return Failure("work_data_plan_invalid");
                treePaths.UnionWith(references);
            }
            error = await ReadFilesAsync(treePaths);
            return error is null ? Result<WorkDataPackage>.Success(new(revision, files.Values.OrderBy(file => file.Path, StringComparer.Ordinal).ToArray())) : Failure(error);
        }
        catch (Exception exception) when (exception is FormatException or DecoderFallbackException)
        { return Failure("work_document_invalid"); }

        async Task<string?> ReadFilesAsync(IEnumerable<string> selected)
        {
            var requested = selected.Distinct(StringComparer.Ordinal).Where(path => !files.ContainsKey(path)).ToArray();
            if (requested.Any(path => !WorkDataFormat.ValidPath(path)) || files.Count + requested.Length > WorkDataFormat.MaximumFiles)
                return "work_data_paths_invalid";
            foreach (var batch in requested.Chunk(8))
            {
                var nativePaths = batch.Select(path => connection.Location + "/" + path).ToArray();
                var read = await InvokeAsync(workspaceId, principalId, binding, connection, "read", "execute", key, digest, new { revision, paths = nativePaths }, token);
                if (!read.IsSuccess) return read.ErrorCode;
                if (read.Value!.Status != "Succeeded" || read.Value.Revision != revision || read.Value.Files?.Count != batch.Length
                    || read.Value.Files.Select(file => file.Path).Distinct(StringComparer.Ordinal).Count() != batch.Length)
                    return "work_data_source_unavailable";
                foreach (var path in batch)
                {
                    var file = read.Value.Files.SingleOrDefault(value => value.Path == connection.Location + "/" + path);
                    if (file is null || file.Executable || !Revision(file.Revision)) return "work_data_source_invalid";
                    var bytes = Convert.FromBase64String(file.ContentBase64);
                    totalBytes += bytes.Length;
                    if (bytes.Length > WorkDataFormat.MaximumFileBytes || totalBytes > WorkDataFormat.MaximumPackageBytes) return "work_document_size_limit";
                    files.Add(path, new(path, new UTF8Encoding(false, true).GetString(bytes)));
                }
            }
            return null;
        }
        static Result<WorkDataPackage> Failure(string code) => Result<WorkDataPackage>.Failure(code);
    }
}
