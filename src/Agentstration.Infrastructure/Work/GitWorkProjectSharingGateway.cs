using System.Text.Json;
using Agentstration.Application.Work;
using Agentstration.Resources;
using Agentstration.Work;

namespace Agentstration.Infrastructure.Work;

public sealed partial class GitWorkProjectSharingGateway
{
    public async Task<Result<WorkSharingProviderCapabilities>> DiscoverAsync(WorkspaceId workspaceId, Guid principalId,
        WorkProjectContextBinding binding, WorkProjectSharingConnection connection, CancellationToken token)
    {
        var resolved = await ResolveAsync(workspaceId, principalId, binding, connection, token);
        return resolved.IsSuccess ? Result<WorkSharingProviderCapabilities>.Success(new(WorkSharingFormat.Version, true, true, true, true, true, true, true))
            : Result<WorkSharingProviderCapabilities>.Failure(resolved.ErrorCode!);
    }
    public async Task<Result<WorkSharingSnapshot>> ReadAsync(WorkspaceId workspaceId, Guid principalId, WorkProjectContextBinding binding,
        WorkProjectSharingConnection connection, string? revision, CancellationToken token)
    {
        Result<WorkSharingSnapshot> Failure(string code) => Result<WorkSharingSnapshot>.Failure(code);
        if (revision is not null && !Revision(revision)) return Failure("sharing_revision_invalid");
        var digest = WorkPlanDefinition.Digest(new { connection.ProjectId, connection.Location, revision });
        var key = WorkPlanDefinition.Digest(new { Read = digest });
        var resolved = await InvokeAsync(workspaceId, principalId, binding, connection, "resolve", "execute", key, digest, new { }, token);
        if (!resolved.IsSuccess) return Failure(resolved.ErrorCode!);
        if (resolved.Value!.Status == "Missing") return revision is null ? Empty(null) : Failure("sharing_revision_not_found");
        if (resolved.Value.Status != "Succeeded" || resolved.Value.Revision is null) return Failure(resolved.Value.ErrorCode ?? "sharing_read_unavailable");
        revision ??= resolved.Value.Revision;
        var indexPath = connection.Location + "/index.yaml";
        var index = await InvokeAsync(workspaceId, principalId, binding, connection, "read", "execute", key, digest, new { revision, paths = new[] { indexPath } }, token);
        if (!index.IsSuccess) return Failure(index.ErrorCode!);
        if (index.Value!.Status == "Rejected" && index.Value.ErrorCode == "git_file_not_found")
        {
            var legacy = await InvokeAsync(workspaceId, principalId, binding, connection, "read", "execute", key, digest,
                new { revision, paths = new[] { connection.Location + "/index.json" } }, token);
            if (!legacy.IsSuccess) return Failure(legacy.ErrorCode!);
            return legacy.Value!.Status == "Rejected" && legacy.Value.ErrorCode == "git_file_not_found"
                ? Empty(revision) : Failure("sharing_format_upgrade_required");
        }
        if (index.Value.Status != "Succeeded" || index.Value.Revision != revision || index.Value.Files is not { Count: 1 }
            || index.Value.Files[0].Path != indexPath || index.Value.Files[0].Executable) return Failure(index.Value.ErrorCode ?? "sharing_index_invalid");
        try
        {
            var indexBytes = Convert.FromBase64String(index.Value.Files[0].ContentBase64);
            if (indexBytes.Length > 32 * 1024) return Failure("sharing_index_invalid");
            var parsedSpace = documents.Read<WorkSharingSpace>(indexBytes, 32 * 1024);
            var space = parsedSpace.IsSuccess ? parsedSpace.Value : null;
            if (space is null || !WorkSharingFormat.Validate(space).IsSuccess || space.ProjectId != connection.ProjectId
                || space.Records.Sum(entry => (long)entry.SizeBytes) > WorkSharingFormat.MaximumSnapshotBytes - 64 * 1024) return Failure("sharing_index_invalid");
            var records = new List<WorkSharingRecord>();
            // Fixed bounded batches respect both the native provider's byte and path limits.
            foreach (var batch in space.Records.Chunk(16))
            {
                var paths = batch.Select(entry => Path(connection, entry.Key, entry.Version, entry.Digest)).ToArray();
                var read = await InvokeAsync(workspaceId, principalId, binding, connection, "read", "execute", key, digest, new { revision, paths }, token);
                if (!read.IsSuccess) return Failure(read.ErrorCode!);
                if (read.Value!.Status != "Succeeded" || read.Value.Revision != revision || read.Value.Files?.Count != paths.Length
                    || read.Value.Files.Select(file => file.Path).Distinct().Count() != paths.Length) return Failure("sharing_records_invalid");
                foreach (var entry in batch)
                {
                    var file = read.Value.Files.SingleOrDefault(file => file.Path == Path(connection, entry.Key, entry.Version, entry.Digest));
                    if (file is null || file.Executable || !Revision(file.Revision)) return Failure("sharing_records_invalid");
                    var bytes = Convert.FromBase64String(file.ContentBase64);
                    if (bytes.Length != entry.SizeBytes) return Failure("sharing_records_invalid");
                    var parsedRecord = documents.Read<WorkSharingRecord>(bytes, WorkSharingFormat.MaximumRecordBytes + 1024);
                    var record = parsedRecord.IsSuccess ? parsedRecord.Value : null;
                    if (record is not null)
                    {
                        var normalized = WorkSharingContent.Normalize(new(record.Key, record.Version - 1, record.Content));
                        if (!normalized.IsSuccess) return Failure("sharing_records_invalid");
                        record = record with { Content = normalized.Value!.Content };
                    }
                    if (record is null || record.Key != entry.Key || record.Version != entry.Version || record.Digest != entry.Digest
                        || !WorkSharingFormat.Validate(record).IsSuccess) return Failure("sharing_records_invalid");
                    records.Add(record);
                }
            }
            return Result<WorkSharingSnapshot>.Success(new(revision, space, records));
        }
        catch (JsonException) { return Failure("sharing_format_invalid"); }
        catch (FormatException) { return Failure("sharing_format_invalid"); }
        Result<WorkSharingSnapshot> Empty(string? observed) => Result<WorkSharingSnapshot>.Success(new(observed, new(connection.ProjectId, []), []));
    }
    public async Task<WorkSharingProviderResult> PublishAsync(WorkspaceId workspaceId, Guid principalId, WorkProjectContextBinding binding,
        WorkProjectSharingConnection connection, WorkSharingTransportAttempt attempt, bool recover, CancellationToken token)
    {
        var changes = attempt.Records.Select(record => new
        {
            path = Path(connection, record.Key, record.Version, record.Digest),
            contentBase64 = Convert.ToBase64String(documents.Write(record))
        }).Append(new { path = connection.Location + "/index.yaml", contentBase64 = Convert.ToBase64String(documents.Write(attempt.Space)) }).ToArray();
        var input = new { baseRevision = attempt.ExpectedRevision, expectedRefRevision = attempt.ExpectedRevision, changes, message = "Synchronize Project sharing records" };
        var result = await InvokeAsync(workspaceId, principalId, binding, connection, "publish", recover ? "recover" : "execute",
            attempt.OperationKey, attempt.IntentDigest, input, token);
        if (!result.IsSuccess) return new(WorkSharingProviderStatus.Uncertain, attempt.OperationKey, attempt.IntentDigest, ErrorCode: result.ErrorCode);
        return new(Enum.Parse<WorkSharingProviderStatus>(result.Value!.Status), attempt.OperationKey, attempt.IntentDigest, result.Value.Revision, result.Value.ErrorCode);
    }
    private static string Path(WorkProjectSharingConnection connection, WorkSharingRecordKey key, long version, string digest) =>
        $"{connection.Location}/records/{key.Domain}/{key.Name}/{version:D10}-{digest}.yaml";
}
