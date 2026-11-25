using Agentstration.Resources;
using Agentstration.Work;
using Microsoft.EntityFrameworkCore;

namespace Agentstration.Work.Storage.Sqlite;

public sealed partial class SqliteWorkplaceRepository
{
    public async Task<WorkTaskResult?> GetResultAsync(WorkspaceId workspaceId, WorkTaskId taskId, WorkTaskResultId resultId, CancellationToken token)
    {
        await using var context = await contextFactory.CreateDbContextAsync(token);
        var workspace = workspaceId.ToString(); var task = taskId.ToString(); var id = resultId.ToString();
        var payload = await context.WorkTaskResults.AsNoTracking().Where(row => row.WorkspaceId == workspace && row.WorkTaskId == task && row.Id == id)
            .Select(row => row.Payload).SingleOrDefaultAsync(token);
        return payload is null ? null : Deserialize<WorkTaskResult>(payload);
    }

    public async Task<WorkTaskHistoryPage<WorkTaskResult>> ListResultPageAsync(WorkspaceId workspaceId, WorkTaskId taskId, int skip, int take, CancellationToken token)
    {
        if (skip < 0 || take is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(take));
        await using var context = await contextFactory.CreateDbContextAsync(token);
        var workspace = workspaceId.ToString(); var task = taskId.ToString();
        var query = context.WorkTaskResults.AsNoTracking().Where(row => row.WorkspaceId == workspace && row.WorkTaskId == task);
        var count = await query.CountAsync(token);
        var payloads = await query.OrderBy(row => row.Id).Skip(skip).Take(take).Select(row => row.Payload).ToArrayAsync(token);
        return new(payloads.Select(Deserialize<WorkTaskResult>).ToArray(), skip, take, count);
    }

    public async Task<WorkTaskHistoryPage<WorkTaskArtifact>> ListArtifactPageAsync(WorkspaceId workspaceId, WorkTaskId taskId, int skip, int take, CancellationToken token)
    {
        if (skip < 0 || take is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(take));
        await using var context = await contextFactory.CreateDbContextAsync(token);
        var workspace = workspaceId.ToString(); var task = taskId.ToString();
        var query = context.WorkTaskArtifacts.AsNoTracking().Where(row => row.WorkspaceId == workspace && row.WorkTaskId == task);
        var count = await query.CountAsync(token);
        var payloads = await query.OrderBy(row => row.Id).Skip(skip).Take(take).Select(row => row.Payload).ToArrayAsync(token);
        return new(payloads.Select(Deserialize<WorkTaskArtifact>).ToArray(), skip, take, count);
    }
}
