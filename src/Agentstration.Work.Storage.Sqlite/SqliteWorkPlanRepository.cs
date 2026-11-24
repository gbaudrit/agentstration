using System.Text.Json;
using Agentstration.Resources;
using Agentstration.Work;
using Agentstration.Work.Storage.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Agentstration.Work.Storage.Sqlite;

public sealed class SqliteWorkPlanRepository(IDbContextFactory<WorkDbContext> contextFactory) : IWorkPlanRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await context.Database.EnsureCreatedAsync(cancellationToken);
        await context.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS WorkPlans (
                WorkspaceId TEXT NOT NULL, Id TEXT NOT NULL, OwnerPrincipalId TEXT NOT NULL,
                Version INTEGER NOT NULL, Payload TEXT NOT NULL, PRIMARY KEY (WorkspaceId, Id));
            CREATE INDEX IF NOT EXISTS IX_WorkPlans_WorkspaceId_OwnerPrincipalId ON WorkPlans (WorkspaceId, OwnerPrincipalId);
            CREATE TABLE IF NOT EXISTS WorkPlanRevisions (
                WorkspaceId TEXT NOT NULL, PlanId TEXT NOT NULL, OwnerPrincipalId TEXT NOT NULL, Number INTEGER NOT NULL, Payload TEXT NOT NULL,
                PRIMARY KEY (WorkspaceId, PlanId, Number), FOREIGN KEY (WorkspaceId, PlanId) REFERENCES WorkPlans (WorkspaceId, Id) ON DELETE RESTRICT);
            CREATE TABLE IF NOT EXISTS WorkPlanCommands (
                WorkspaceId TEXT NOT NULL, PlanId TEXT NOT NULL, OwnerPrincipalId TEXT NOT NULL, CommandId TEXT NOT NULL, Payload TEXT NOT NULL,
                PRIMARY KEY (WorkspaceId, PlanId, CommandId), FOREIGN KEY (WorkspaceId, PlanId) REFERENCES WorkPlans (WorkspaceId, Id) ON DELETE RESTRICT);
            """, cancellationToken);
    }

    public async Task<WorkPlanSnapshot?> GetAsync(WorkspaceId workspaceId, Guid ownerPrincipalId, WorkPlanId id, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var workspace = workspaceId.ToString(); var owner = ownerPrincipalId.ToString("D"); var key = id.ToString();
        var row = await context.WorkPlans.AsNoTracking().SingleOrDefaultAsync(
            value => value.WorkspaceId == workspace && value.OwnerPrincipalId == owner && value.Id == key, cancellationToken);
        return row is null ? null : Deserialize<WorkPlanSnapshot>(row.Payload);
    }

    public async Task<WorkPlanRevision?> GetRevisionAsync(WorkspaceId workspaceId, Guid ownerPrincipalId, WorkPlanId id, long revision,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var workspace = workspaceId.ToString(); var owner = ownerPrincipalId.ToString("D"); var key = id.ToString();
        var row = await context.WorkPlanRevisions.AsNoTracking().SingleOrDefaultAsync(
            value => value.WorkspaceId == workspace && value.OwnerPrincipalId == owner && value.PlanId == key && value.Number == revision, cancellationToken);
        return row is null ? null : Deserialize<WorkPlanRevision>(row.Payload);
    }

    public async Task<WorkPlanCommandReceipt?> GetReceiptAsync(WorkspaceId workspaceId, Guid ownerPrincipalId, WorkPlanId id, Guid commandId,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var workspace = workspaceId.ToString(); var owner = ownerPrincipalId.ToString("D"); var key = id.ToString(); var command = commandId.ToString("D");
        var row = await context.WorkPlanCommands.AsNoTracking().SingleOrDefaultAsync(value =>
            value.WorkspaceId == workspace && value.OwnerPrincipalId == owner && value.PlanId == key && value.CommandId == command, cancellationToken);
        return row is null ? null : Deserialize<WorkPlanCommandReceipt>(row.Payload);
    }

    public async Task<IReadOnlyList<WorkPlanSnapshot>> ListExecutionsAsync(int skip, int take, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var payloads = await context.WorkPlans.AsNoTracking().OrderBy(row => row.WorkspaceId).ThenBy(row => row.Id)
            .Skip(Math.Max(0, skip)).Take(Math.Clamp(take, 1, 100)).Select(row => row.Payload).ToArrayAsync(cancellationToken);
        return payloads.Select(Deserialize<WorkPlanSnapshot>).ToArray();
    }

    public async Task<WorkPlanSaveStatus> TrySaveAsync(WorkPlanChange change, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (change.Plan.Version != change.ExpectedVersion + 1 || change.Receipt.PlanVersion != change.Plan.Version
            || change.Receipt.CommandId == Guid.Empty || change.Plan.OwnerPrincipalId == Guid.Empty
            || change.Plan.WorkspaceId.Value == Guid.Empty || change.Plan.Id.Value == Guid.Empty
            || change.Receipt.ObservedTasks.Count is < 1 or > WorkPlanDefinition.MaximumTasks * 2
            || change.Receipt.ObservedTasks.Select(task => task.TaskId).Distinct().Count() != change.Receipt.ObservedTasks.Count
            || change.NewRevision is { } added && (added.PlanId != change.Plan.Id || added.Number != change.Plan.CurrentRevision))
            throw new ArgumentException("A bounded owned WorkPlan change with increasing version is required.", nameof(change));
        await using var strategyContext = await contextFactory.CreateDbContextAsync(cancellationToken);
        var strategy = strategyContext.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
            var workspace = change.Plan.WorkspaceId.ToString();
            var owner = change.Plan.OwnerPrincipalId.ToString("D");
            var key = change.Plan.Id.ToString();
            var command = change.Receipt.CommandId.ToString("D");
            var previous = await context.WorkPlanCommands.AsNoTracking().SingleOrDefaultAsync(value =>
                value.WorkspaceId == workspace && value.OwnerPrincipalId == owner && value.PlanId == key && value.CommandId == command, cancellationToken);
            if (previous is not null)
                return Deserialize<WorkPlanCommandReceipt>(previous.Payload).IntentDigest == change.Receipt.IntentDigest
                    ? WorkPlanSaveStatus.Replayed : WorkPlanSaveStatus.Conflict;
            var row = await context.WorkPlans.SingleOrDefaultAsync(value =>
                value.WorkspaceId == workspace && value.OwnerPrincipalId == owner && value.Id == key, cancellationToken);
            if (change.ExpectedVersion == 0 ? row is not null : row is null || row.Version != change.ExpectedVersion)
                return WorkPlanSaveStatus.Conflict;
            foreach (var observed in change.Receipt.ObservedTasks.OrderBy(value => value.TaskId.Value))
            {
                var task = observed.TaskId.ToString();
                var query = context.WorkItems.Where(value => value.WorkspaceId == workspace
                    && value.OwnerPrincipalId == owner && value.Id == task && value.Version == observed.Version && value.TaskTreeId != null);
                if (change.RequireInactiveTasks)
                    query = query.Where(value => value.Status != WorkItemStatus.Queued && value.Status != WorkItemStatus.Running
                        && value.Status != WorkItemStatus.WaitingForInput && value.Status != WorkItemStatus.WaitingForApproval
                        && value.Status != WorkItemStatus.Paused);
                // Lock the observed row without changing its logical version, history or payload.
                var matched = await query.ExecuteUpdateAsync(setters => setters.SetProperty(value => value.Version, value => value.Version), cancellationToken);
                if (matched == 0)
                {
                    var current = await context.WorkItems.AsNoTracking().SingleOrDefaultAsync(value =>
                        value.WorkspaceId == workspace && value.OwnerPrincipalId == owner && value.Id == task, cancellationToken);
                    return change.RequireInactiveTasks && current is not null && IsActive(current.Status)
                        ? WorkPlanSaveStatus.TasksActive : WorkPlanSaveStatus.TaskChanged;
                }
            }
            // All Work mutations acquire task rows, then plan rows, then Project rows.
            if (change.Project is { } projectObservation)
            {
                var planMatched = await context.WorkPlans.Where(value => value.WorkspaceId == workspace && value.OwnerPrincipalId == owner
                    && value.Id == key && value.Version == change.ExpectedVersion)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(value => value.Version, value => value.Version), cancellationToken);
                if (planMatched != 1) return WorkPlanSaveStatus.Conflict;
                var projectKey = projectObservation.ProjectId.ToString();
                var projectMatched = await context.WorkProjects.Where(value => value.WorkspaceId == workspace && value.OwnerPrincipalId == owner
                    && value.Id == projectKey && value.Version == projectObservation.Version)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(value => value.Version, value => value.Version), cancellationToken);
                if (projectMatched != 1) return WorkPlanSaveStatus.ProjectChanged;
            }
            foreach (var update in change.TaskUpdates)
            {
                var taskKey = update.Value.Id.ToString();
                if (update.Value.WorkspaceId != change.Plan.WorkspaceId || update.Value.OwnerPrincipalId != change.Plan.OwnerPrincipalId
                    || update.Value.Version != update.ExpectedVersion + 1
                    || !change.Receipt.ObservedTasks.Any(observed => observed.TaskId.ToWorkItemId() == update.Value.Id && observed.Version == update.ExpectedVersion))
                    throw new ArgumentException("Plan task updates must match the observed owned task version.", nameof(change));
                var document = await context.WorkItems.SingleAsync(value => value.WorkspaceId == workspace && value.OwnerPrincipalId == owner && value.Id == taskKey, cancellationToken);
                SqliteWorkItemRepository.Apply(document, update.Value);
            }
            if (row is null)
            {
                row = new WorkPlanDocument
                {
                    WorkspaceId = workspace,
                    OwnerPrincipalId = owner,
                    Id = key,
                    Version = change.Plan.Version,
                    Payload = JsonSerializer.Serialize(change.Plan, JsonOptions)
                };
                context.WorkPlans.Add(row);
            }
            else { row.Version = change.Plan.Version; row.Payload = JsonSerializer.Serialize(change.Plan, JsonOptions); }
            if (change.NewRevision is { } revision)
                context.WorkPlanRevisions.Add(new WorkPlanRevisionDocument
                {
                    WorkspaceId = workspace,
                    OwnerPrincipalId = owner,
                    PlanId = key,
                    Number = revision.Number,
                    Payload = JsonSerializer.Serialize(revision, JsonOptions)
                });
            context.WorkPlanCommands.Add(new WorkPlanCommandDocument
            {
                WorkspaceId = workspace,
                OwnerPrincipalId = owner,
                PlanId = key,
                CommandId = command,
                Payload = JsonSerializer.Serialize(change.Receipt, JsonOptions)
            });
            try
            {
                await context.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return WorkPlanSaveStatus.Saved;
            }
            catch (DbUpdateConcurrencyException) { return WorkPlanSaveStatus.Conflict; }
            catch (DbUpdateException exception) when (exception.InnerException is Microsoft.Data.Sqlite.SqliteException
            { SqliteErrorCode: 19, SqliteExtendedErrorCode: 1555 or 2067 })
            { return WorkPlanSaveStatus.Conflict; }
        });
    }

    private static bool IsActive(WorkItemStatus status) => status is WorkItemStatus.Queued or WorkItemStatus.Running
        or WorkItemStatus.WaitingForInput or WorkItemStatus.WaitingForApproval or WorkItemStatus.Paused;

    private static T Deserialize<T>(string payload) => JsonSerializer.Deserialize<T>(payload, JsonOptions)
        ?? throw new InvalidOperationException("The persisted WorkPlan document is invalid.");
}
