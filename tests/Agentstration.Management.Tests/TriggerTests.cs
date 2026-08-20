using System.Text.Json;
using Agentstration.Infrastructure;
using Agentstration.Infrastructure.Triggers;
using Agentstration.Management.Abstractions;
using Agentstration.Management.Core;
using Agentstration.Management.Storage.Sqlite;
using Agentstration.Resources;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Management.Tests;

[TestClass]
public sealed class TriggerTests
{
    [TestMethod]
    public void ScheduleCalculatorSupportsOnceIntervalAndCronDeterministically()
    {
        var calculator = new QuartzTriggerScheduleCalculator();
        var after = DateTimeOffset.Parse("2026-08-21T06:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

        Assert.AreEqual(after.AddHours(1), calculator.GetNextOccurrence(new() { Type = TriggerScheduleType.Once, At = after.AddHours(1) }, after));
        Assert.AreEqual(after.AddHours(6), calculator.GetNextOccurrence(new() { Type = TriggerScheduleType.Interval, StartAt = after, Every = "PT6H" }, after));
        Assert.AreEqual(DateTimeOffset.Parse("2026-08-24T08:00:00+02:00", System.Globalization.CultureInfo.InvariantCulture),
            calculator.GetNextOccurrence(new() { Type = TriggerScheduleType.Cron, Expression = "0 0 8 ? * MON-FRI", TimeZone = "Europe/Paris" }, after));
    }

    [TestMethod]
    public void ScheduleCalculatorRejectsInvalidCronAndTimeZone()
    {
        var calculator = new QuartzTriggerScheduleCalculator();
        Assert.ThrowsExactly<TriggerValidationException>(() => calculator.Validate(new() { Type = TriggerScheduleType.Cron, Expression = "not cron", TimeZone = "Europe/Paris" }));
        Assert.ThrowsExactly<TriggerValidationException>(() => calculator.Validate(new() { Type = TriggerScheduleType.Cron, Expression = "0 0 8 ? * *", TimeZone = "Not/AZone" }));
    }

    [TestMethod]
    public async Task TriggerCreateUpdateAndDisableUseEtagAndReconcileAsync()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var scope = fixture.Context.Push(fixture.Request);
        var service = fixture.Services.GetRequiredService<TriggerManagementService>();
        var created = await service.CreateAsync(Resource("morning", true), CancellationToken.None);

        Assert.AreEqual(1, created.Value.Generation);
        Assert.IsNotNull(created.Value.Definition.ExecutionScope);
        Assert.AreEqual(fixture.Request.PrincipalId, created.Value.Definition.ExecutionScope.PrincipalId);
        var updated = await service.UpdateAsync(ResourceNamespace.Default, "morning", created.Value.Definition with { Enabled = false }, created.ETag, CancellationToken.None);
        Assert.AreEqual(2, updated.Value.Generation);
        Assert.IsFalse(updated.Value.Definition.Enabled);
        Assert.AreEqual(2, fixture.Scheduler.Reconciled.Count);
        await Assert.ThrowsExactlyAsync<ControlPlaneConcurrencyException>(() => service.UpdateAsync(ResourceNamespace.Default, "morning", updated.Value.Definition, created.ETag, CancellationToken.None));
    }

    [TestMethod]
    public async Task TriggerRejectsRawCredentialsButAllowsExplicitReferencesAsync()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var scope = fixture.Context.Push(fixture.Request);
        var service = fixture.Services.GetRequiredService<TriggerManagementService>();

        var rawCredential = Resource("unsafe", false) with
        {
            Definition = Resource("unsafe", false).Definition with { Input = JsonSerializer.SerializeToElement(new { nested = new { apiKey = "raw-value" } }) }
        };
        await Assert.ThrowsExactlyAsync<TriggerValidationException>(() => service.CreateAsync(rawCredential, CancellationToken.None));

        var reference = Resource("safe-reference", false) with
        {
            Definition = Resource("safe-reference", false).Definition with { Input = JsonSerializer.SerializeToElement(new { credentialRef = "provider-owned-reference" }) }
        };
        var created = await service.CreateAsync(reference, CancellationToken.None);
        Assert.AreEqual("provider-owned-reference", created.Value.Definition.Input.GetProperty("credentialRef").GetString());
    }

    [TestMethod]
    public async Task OccurrenceStoreIsIdempotentWorkspaceScopedAndSurvivesRestartAsync()
    {
        var database = Path.Combine(Path.GetTempPath(), $"agentstration-trigger-{Guid.NewGuid():N}.db");
        try
        {
            var workspace = Guid.NewGuid();
            var trigger = Guid.NewGuid();
            var occurrence = Occurrence(workspace, trigger);
            await using (var first = await OccurrenceFixture.CreateAsync(database))
            {
                Assert.IsTrue(await first.Store.TryCreateAsync(occurrence, CancellationToken.None));
                Assert.IsFalse(await first.Store.TryCreateAsync(occurrence, CancellationToken.None));
                await first.Store.CompleteAsync(workspace, occurrence.Id, TriggerOccurrenceOutcome.Submitted, occurrence.ScheduledAt, occurrence.Id.ToString(), null, null, CancellationToken.None);
            }
            await using (var restarted = await OccurrenceFixture.CreateAsync(database))
            {
                var restored = await restarted.Store.ListAsync(workspace, trigger, 10, CancellationToken.None);
                Assert.HasCount(1, restored);
                Assert.AreEqual(TriggerOccurrenceOutcome.Submitted, restored[0].Outcome);
                Assert.IsEmpty(await restarted.Store.ListAsync(Guid.NewGuid(), trigger, 10, CancellationToken.None));
            }
        }
        finally { SqliteConnection.ClearAllPools(); File.Delete(database); }
    }

    [TestMethod]
    public void ScheduledOccurrenceIdentityIsStablePerTriggerAndInstant()
    {
        var trigger = Resource("stable", true) with { Uid = Guid.NewGuid(), WorkspaceId = Guid.NewGuid() };
        var at = DateTimeOffset.Parse("2026-08-21T08:00:00+02:00", System.Globalization.CultureInfo.InvariantCulture);
        Assert.AreEqual(TriggerFiringService.DeterministicOccurrenceId(trigger, at), TriggerFiringService.DeterministicOccurrenceId(trigger, at.ToUniversalTime()));
        Assert.AreNotEqual(TriggerFiringService.DeterministicOccurrenceId(trigger, at), TriggerFiringService.DeterministicOccurrenceId(trigger, at.AddHours(1)));
    }

    [TestMethod]
    public async Task ScheduledFiringSubmitsAtMostOneWorkForTheOccurrenceAsync()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var scope = fixture.Context.Push(fixture.Request);
        await fixture.Services.GetRequiredService<TriggerManagementService>().CreateAsync(Resource("deduplicated", true), CancellationToken.None);
        var firing = fixture.Services.GetRequiredService<TriggerFiringService>();
        var scheduledAt = DateTimeOffset.Parse("2026-08-21T06:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

        var first = await firing.FireScheduledAsync(ResourceNamespace.Default, "deduplicated", scheduledAt, CancellationToken.None);
        var duplicate = await firing.FireScheduledAsync(ResourceNamespace.Default, "deduplicated", scheduledAt, CancellationToken.None);

        Assert.AreEqual(first.Id, duplicate.Id);
        Assert.AreEqual(TriggerOccurrenceOutcome.Submitted, duplicate.Outcome);
        Assert.AreEqual(1, fixture.Work.Submissions);
    }

    [TestMethod]
    public async Task DisabledTriggerNeverRunsAndAuthorizationFailureIsRecordedBeforeWorkAsync()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var scope = fixture.Context.Push(fixture.Request);
        var management = fixture.Services.GetRequiredService<TriggerManagementService>();
        var disabled = await management.CreateAsync(Resource("disabled", false), CancellationToken.None);
        var firing = fixture.Services.GetRequiredService<TriggerFiringService>();
        await Assert.ThrowsExactlyAsync<TriggerExecutionException>(() => firing.RunNowAsync(ResourceNamespace.Default, disabled.Value.Name, CancellationToken.None));

        await management.CreateAsync(Resource("revoked", true), CancellationToken.None);
        fixture.Authorizer.Deny = true;
        var failed = await firing.FireScheduledAsync(ResourceNamespace.Default, "revoked", DateTimeOffset.UtcNow, CancellationToken.None);
        Assert.AreEqual(TriggerOccurrenceOutcome.Failed, failed.Outcome);
        Assert.AreEqual("trigger_authorization_denied", failed.ErrorCode);
        Assert.AreEqual(0, fixture.Work.Submissions);
    }

    private static TriggerResource Resource(string name, bool enabled) => new()
    {
        ApiVersion = ManagementApiVersions.CoreV1,
        Kind = ResourceKinds.Trigger,
        Metadata = new() { Name = name },
        Definition = new()
        {
            DisplayName = name,
            Enabled = enabled,
            Source = new() { Schedule = new() { Type = TriggerScheduleType.Once, At = DateTimeOffset.UtcNow.AddHours(1) } },
            Target = new() { Flow = new() { Name = "test-flow" } }
        }
    };

    private static TriggerOccurrence Occurrence(Guid workspace, Guid trigger) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        WorkspaceId = workspace,
        TriggerUid = trigger,
        TriggerName = "test",
        TriggerNamespace = ResourceNamespace.Default,
        TriggerGeneration = 1,
        Kind = TriggerOccurrenceKind.Scheduled,
        ScheduledAt = DateTimeOffset.UtcNow
    };

    private sealed class Fixture : IAsyncDisposable
    {
        public required string Database { get; init; }
        public required ServiceProvider Services { get; init; }
        public required CurrentRequestContext Context { get; init; }
        public required RequestContext Request { get; init; }
        public required RecordingScheduler Scheduler { get; init; }
        public required RecordingWorkSubmitter Work { get; init; }
        public required RecordingAuthorizer Authorizer { get; init; }

        public static async Task<Fixture> CreateAsync()
        {
            var database = Path.Combine(Path.GetTempPath(), $"agentstration-trigger-management-{Guid.NewGuid():N}.db");
            var context = new CurrentRequestContext();
            var scheduler = new RecordingScheduler();
            var work = new RecordingWorkSubmitter();
            var authorizer = new RecordingAuthorizer();
            var services = new ServiceCollection().AddSingleton(TimeProvider.System).AddSingleton(context).AddSingleton<ICurrentRequestContext>(context)
                .AddSqliteControlPlane($"Data Source={database}")
                .AddSingleton<ITriggerScheduleCalculator, QuartzTriggerScheduleCalculator>()
                .AddSingleton<ITriggerTargetValidator, AcceptingTargetValidator>()
                .AddSingleton<ITriggerSchedulerProjection>(scheduler)
                .AddSingleton<ITriggerOccurrenceStore, RecordingOccurrenceStore>()
                .AddSingleton<ITriggerWorkSubmitter>(work)
                .AddSingleton<ITriggerExecutionAuthorizer>(authorizer)
                .AddSingleton<TriggerManagementService>()
                .AddSingleton<TriggerFiringService>()
                .BuildServiceProvider();
            await services.GetRequiredService<IControlPlaneStore>().InitializeAsync(CancellationToken.None);
            return new() { Database = database, Services = services, Context = context, Request = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()), Scheduler = scheduler, Work = work, Authorizer = authorizer };
        }

        public async ValueTask DisposeAsync()
        {
            await Services.DisposeAsync();
            SqliteConnection.ClearAllPools();
            File.Delete(Database);
        }
    }

    private sealed class OccurrenceFixture : IAsyncDisposable
    {
        public required ServiceProvider Services { get; init; }
        public ITriggerOccurrenceStore Store => Services.GetRequiredService<ITriggerOccurrenceStore>();
        public static async Task<OccurrenceFixture> CreateAsync(string database)
        {
            var services = new ServiceCollection().AddSingleton(TimeProvider.System).AddSingleton<ICurrentRequestContext, SystemOperationRequestContext>()
                .AddSqliteControlPlane($"Data Source={database}").BuildServiceProvider();
            await services.GetRequiredService<IControlPlaneStore>().InitializeAsync(CancellationToken.None);
            return new() { Services = services };
        }
        public async ValueTask DisposeAsync() => await Services.DisposeAsync();
    }

    private sealed class AcceptingTargetValidator : ITriggerTargetValidator
    {
        public Task ValidateAsync(ResourceNamespace ownerNamespace, TriggerTarget target, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class RecordingScheduler : ITriggerSchedulerProjection
    {
        public List<TriggerResource> Reconciled { get; } = [];
        public Task ReconcileAsync(TriggerResource trigger, CancellationToken cancellationToken) { Reconciled.Add(trigger); return Task.CompletedTask; }
        public Task RemoveAsync(Guid workspaceId, Guid triggerUid, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class RecordingOccurrenceStore : ITriggerOccurrenceStore
    {
        private readonly Dictionary<Guid, TriggerOccurrence> values = [];
        public Task<bool> TryCreateAsync(TriggerOccurrence occurrence, CancellationToken cancellationToken) { if (values.ContainsKey(occurrence.Id)) return Task.FromResult(false); values.Add(occurrence.Id, occurrence); return Task.FromResult(true); }
        public Task CompleteAsync(Guid workspaceId, Guid occurrenceId, TriggerOccurrenceOutcome outcome, DateTimeOffset firedAt, string? workItemId, string? errorCode, string? errorMessage, CancellationToken cancellationToken) { values[occurrenceId] = values[occurrenceId] with { Outcome = outcome, FiredAt = firedAt, WorkItemId = workItemId, ErrorCode = errorCode, ErrorMessage = errorMessage }; return Task.CompletedTask; }
        public Task<IReadOnlyList<TriggerOccurrence>> ListAsync(Guid workspaceId, Guid triggerUid, int take, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<TriggerOccurrence>>(values.Values.Where(value => value.WorkspaceId == workspaceId && value.TriggerUid == triggerUid).Take(take).ToArray());
    }

    private sealed class RecordingWorkSubmitter : ITriggerWorkSubmitter
    {
        public int Submissions { get; private set; }
        public Task<bool> HasActiveWorkAsync(Guid workspaceId, Guid triggerUid, CancellationToken cancellationToken) => Task.FromResult(false);
        public Task<TriggerSubmission?> GetExistingAsync(Guid workspaceId, Guid occurrenceId, CancellationToken cancellationToken) => Task.FromResult<TriggerSubmission?>(null);
        public Task<TriggerSubmission> SubmitAsync(TriggerResource trigger, TriggerOccurrence occurrence, CancellationToken cancellationToken) { Submissions++; return Task.FromResult(new TriggerSubmission(occurrence.Id.ToString())); }
    }

    private sealed class RecordingAuthorizer : ITriggerExecutionAuthorizer
    {
        public bool Deny { get; set; }
        public Task<IAsyncDisposable> AuthorizeAsync(TriggerExecutionScope executionScope, CancellationToken cancellationToken) => Deny
            ? throw new TriggerExecutionException("trigger_authorization_denied", "Permission was revoked.")
            : Task.FromResult<IAsyncDisposable>(new EmptyAsyncDisposable());
        private sealed class EmptyAsyncDisposable : IAsyncDisposable { public ValueTask DisposeAsync() => ValueTask.CompletedTask; }
    }
}
