using System.Text.Json;
using Agentstration.Resources;
using Agentstration.Work;
using Agentstration.Work.Storage.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Agentstration.Work.Storage.PostgreSql;

public sealed class PostgreSqlWorkProjectRepository(IDbContextFactory<WorkDbContext> contextFactory) : IWorkProjectRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (!await context.Database.CanConnectAsync(cancellationToken)) throw new InvalidOperationException("The PostgreSQL Work store is not accessible.");
    }

    public async Task<WorkProjectSnapshot?> GetAsync(WorkspaceId workspaceId, Guid ownerPrincipalId, WorkProjectId id, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var workspace = workspaceId.ToString(); var owner = ownerPrincipalId.ToString("D"); var key = id.ToString();
        var row = await context.WorkProjects.AsNoTracking().SingleOrDefaultAsync(value => value.WorkspaceId == workspace && value.OwnerPrincipalId == owner && value.Id == key, cancellationToken);
        return row is null ? null : Deserialize(row.Payload);
    }

    public async Task<WorkProjectSnapshot?> FindAsync(WorkspaceId workspaceId, Guid ownerPrincipalId, ResourceNamespace resourceNamespace, string name, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var workspace = workspaceId.ToString(); var owner = ownerPrincipalId.ToString("D"); var ns = resourceNamespace.Value;
        var row = await context.WorkProjects.AsNoTracking().SingleOrDefaultAsync(value => value.WorkspaceId == workspace && value.OwnerPrincipalId == owner && value.Namespace == ns && value.Name == name, cancellationToken);
        return row is null ? null : Deserialize(row.Payload);
    }

    public async Task<WorkProjectPage> ListAsync(WorkspaceId workspaceId, Guid ownerPrincipalId, int skip, int take, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var workspace = workspaceId.ToString(); var owner = ownerPrincipalId.ToString("D");
        take = Math.Clamp(take, 1, 100);
        var rows = await context.WorkProjects.AsNoTracking().Where(value => value.WorkspaceId == workspace && value.OwnerPrincipalId == owner)
            .OrderBy(value => value.Namespace).ThenBy(value => value.Name).ThenBy(value => value.Id).Skip(Math.Max(0, skip)).Take(take + 1)
            .Select(value => value.Payload).ToArrayAsync(cancellationToken);
        return new(rows.Take(take).Select(Deserialize).ToArray(), rows.Length > take);
    }

    public async Task<WorkProjectSaveStatus> TrySaveAsync(WorkProjectChange change, CancellationToken cancellationToken)
    {
        var project = change.Project;
        if (project.WorkspaceId.Value == Guid.Empty || project.OwnerPrincipalId == Guid.Empty || project.Id.Value == Guid.Empty
            || project.Version != change.ExpectedVersion + 1 || change.Receipt.Version != project.Version
            || change.Receipt.CommandId == Guid.Empty || project.Commands.Count > WorkProjectDefinition.MaximumCommands
            || !project.Definition.PlanIds.OrderBy(id => id.Value).SequenceEqual(change.Plans.Select(item => item.PlanId).OrderBy(id => id.Value))
            || !project.Definition.TaskIds.OrderBy(id => id.Value).SequenceEqual(change.Tasks.Select(item => item.TaskId).OrderBy(id => id.Value)))
            throw new ArgumentException("A bounded owned Project change with guarded work references is required.", nameof(change));
        await using var strategyContext = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await strategyContext.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
            var workspace = project.WorkspaceId.ToString(); var owner = project.OwnerPrincipalId.ToString("D"); var key = project.Id.ToString();
            var row = await context.WorkProjects.SingleOrDefaultAsync(value => value.WorkspaceId == workspace && value.OwnerPrincipalId == owner && value.Id == key, cancellationToken);
            if (row is not null && Deserialize(row.Payload).Commands.SingleOrDefault(receipt => receipt.CommandId == change.Receipt.CommandId) is { } prior)
                return prior.IntentDigest == change.Receipt.IntentDigest ? WorkProjectSaveStatus.Replayed : WorkProjectSaveStatus.Conflict;
            if (change.ExpectedVersion == 0 ? row is not null : row is null || row.Version != change.ExpectedVersion)
                return WorkProjectSaveStatus.Conflict;
            if (row is not null && (row.Name != project.Name || row.Namespace != project.Namespace.Value)) return WorkProjectSaveStatus.Conflict;
            foreach (var observed in change.Tasks.OrderBy(value => value.TaskId.Value))
            {
                var id = observed.TaskId.ToString();
                var matched = await context.WorkItems.Where(value => value.WorkspaceId == workspace && value.OwnerPrincipalId == owner && value.Id == id && value.Version == observed.Version)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(value => value.Version, value => value.Version), cancellationToken);
                if (matched != 1) return WorkProjectSaveStatus.WorkChanged;
            }
            foreach (var observed in change.Plans.OrderBy(value => value.PlanId.Value))
            {
                var id = observed.PlanId.ToString();
                var matched = await context.WorkPlans.Where(value => value.WorkspaceId == workspace && value.OwnerPrincipalId == owner && value.Id == id && value.Version == observed.Version)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(value => value.Version, value => value.Version), cancellationToken);
                if (matched != 1) return WorkProjectSaveStatus.WorkChanged;
            }
            if (row is null)
            {
                row = new WorkProjectDocument { WorkspaceId = workspace, OwnerPrincipalId = owner, Id = key, Name = project.Name, Namespace = project.Namespace.Value,
                    Version = project.Version, Payload = JsonSerializer.Serialize(project, JsonOptions) };
                context.WorkProjects.Add(row);
            }
            else { row.Version = project.Version; row.Payload = JsonSerializer.Serialize(project, JsonOptions); }
            try
            {
                await context.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return WorkProjectSaveStatus.Saved;
            }
            catch (DbUpdateConcurrencyException) { return WorkProjectSaveStatus.Conflict; }
            catch (DbUpdateException exception) when (exception.InnerException is Npgsql.PostgresException { SqlState: "23505" }) { return WorkProjectSaveStatus.Conflict; }
        });
    }

    private static WorkProjectSnapshot Deserialize(string payload) => JsonSerializer.Deserialize<WorkProjectSnapshot>(payload, JsonOptions)
        ?? throw new InvalidOperationException("The persisted Project is invalid.");
}
