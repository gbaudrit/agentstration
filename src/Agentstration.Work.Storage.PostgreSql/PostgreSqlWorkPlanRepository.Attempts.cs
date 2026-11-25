using System.Text.Json;
using Agentstration.Resources;
using Agentstration.Work;
using Microsoft.EntityFrameworkCore;

namespace Agentstration.Work.Storage.PostgreSql;

public sealed partial class PostgreSqlWorkPlanRepository
{
    public async Task<WorkTaskExecutionPage> ListTaskAttemptsAsync(WorkspaceId workspaceId, Guid ownerPrincipalId, WorkTaskId taskId,
        int skip, int take, CancellationToken token)
    {
        if (skip < 0 || take is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(take));
        await using var context = await contextFactory.CreateDbContextAsync(token);
        var workspace = workspaceId.ToString(); var owner = ownerPrincipalId.ToString("D"); var task = taskId.ToString();
        var query = context.WorkTaskAttempts.AsNoTracking().Where(row => row.WorkspaceId == workspace && row.OwnerPrincipalId == owner && row.TaskId == task);
        var count = await query.CountAsync(token);
        var payloads = await query.OrderBy(row => row.AdmittedAtTicks).ThenBy(row => row.Id).Skip(skip).Take(take).Select(row => row.Payload).ToArrayAsync(token);
        return new(payloads.Select(Deserialize<WorkTaskExecutionReceipt>).ToArray(), skip, take, count);
    }

    private static async Task ProjectAttemptsAsync(WorkDbContext context, WorkPlanSnapshot plan, CancellationToken token)
    {
        var workspace = plan.WorkspaceId.ToString(); var id = plan.Id.ToString();
        var existing = await context.WorkTaskAttempts.Where(row => row.WorkspaceId == workspace && row.PlanId == id).ToDictionaryAsync(row => row.Id, token);
        foreach (var attempt in plan.Attempts)
        {
            var payload = JsonSerializer.Serialize(new WorkTaskExecutionReceipt(plan.WorkspaceId, plan.OwnerPrincipalId, plan.Id, attempt), JsonOptions);
            if (existing.TryGetValue(attempt.Id.ToString("D"), out var row)) { if (row.Payload != payload) row.Payload = payload; }
            else context.WorkTaskAttempts.Add(new()
            {
                WorkspaceId = workspace, Id = attempt.Id.ToString("D"), OwnerPrincipalId = plan.OwnerPrincipalId.ToString("D"),
                TaskId = attempt.TaskId.ToString(), PlanId = id, AdmittedAtTicks = attempt.AdmittedAt.UtcTicks, Payload = payload
            });
        }
    }
}
