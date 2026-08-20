using Agentstration.Memory.Storage.Abstractions;
using Agentstration.Resources;

namespace Agentstration.Memory.Testing;

public delegate ValueTask<MemoryRecordStoreLease> MemoryRecordStoreFactory(CancellationToken cancellationToken);

public sealed class MemoryRecordStoreLease(IMemoryRecordStore store, Func<ValueTask>? dispose = null) : IAsyncDisposable
{
    public IMemoryRecordStore Store { get; } = store ?? throw new ArgumentNullException(nameof(store));
    public ValueTask DisposeAsync() => dispose?.Invoke() ?? ValueTask.CompletedTask;
}

public sealed record MemoryStoreConformanceScenarioResult(string Name, bool Passed, string? FailureCode = null, string? ExceptionType = null);

public sealed record MemoryStoreConformanceReport(IReadOnlyList<MemoryStoreConformanceScenarioResult> Scenarios)
{
    public bool IsConformant => Scenarios.All(value => value.Passed);

    public void EnsureConformant()
    {
        var failures = Scenarios.Where(value => !value.Passed).Select(value => $"{value.Name}:{value.FailureCode ?? value.ExceptionType ?? "failed"}");
        if (!IsConformant) throw new MemoryStoreConformanceException(string.Join(", ", failures));
    }
}

public sealed class MemoryStoreConformanceException(string failures)
    : Exception($"Memory record store conformance failed: {failures}");

public sealed class MemoryRecordStoreConformanceSuite(MemoryRecordStoreFactory factory)
{
    private static readonly DateTimeOffset Now = new(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);

    public async Task<MemoryStoreConformanceReport> RunAsync(CancellationToken cancellationToken = default)
    {
        var scenarios = new (string Name, Func<IMemoryRecordStore, CancellationToken, Task> Execute)[]
        {
            ("round-trip-and-workspace-isolation", RoundTripAndWorkspaceIsolationAsync),
            ("ordering-pagination-and-bounds", OrderingPaginationAndBoundsAsync),
            ("expiry-and-workspace-scoped-purge", ExpiryAndPurgeAsync),
            ("mutation-semantics-and-errors", MutationSemanticsAndErrorsAsync),
            ("cancellation", CancellationAsync)
        };
        var results = new List<MemoryStoreConformanceScenarioResult>(scenarios.Length);
        foreach (var scenario in scenarios)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var lease = await factory(cancellationToken);
            try
            {
                await lease.Store.InitializeAsync(cancellationToken);
                await scenario.Execute(lease.Store, cancellationToken);
                results.Add(new(scenario.Name, true));
            }
            catch (ConformanceFailureException exception)
            {
                results.Add(new(scenario.Name, false, exception.Code));
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                // Provider exception messages may contain payloads. The reusable report
                // intentionally exposes only the exception type.
                results.Add(new(scenario.Name, false, "provider-exception", exception.GetType().Name));
            }
        }
        return new(results);
    }

    private static async Task RoundTripAndWorkspaceIsolationAsync(IMemoryRecordStore store, CancellationToken cancellationToken)
    {
        var workspace = new WorkspaceId(Guid.NewGuid());
        var otherWorkspace = new WorkspaceId(Guid.NewGuid());
        var id = MemoryRecordId.New();
        var record = Record(id, workspace, MemoryScope.ForAgent(Guid.NewGuid()), "round-trip", Now, Now.AddDays(1), ["z", "fact"]);
        await store.AddAsync(record, cancellationToken);

        Equal(record, await store.GetAsync(workspace, id, cancellationToken), "round_trip_changed");
        IsNull(await store.GetAsync(otherWorkspace, id, cancellationToken), "cross_workspace_get_visible");
        IsFalse(await store.DeleteAsync(otherWorkspace, id, cancellationToken), "cross_workspace_delete_succeeded");
        Equal(record, (await store.ListAsync(workspace, record.Scope, Now, 0, 10, cancellationToken)).SingleOrDefault(), "scope_list_changed");
        IsEmpty(await store.ListAsync(otherWorkspace, null, Now, 0, 10, cancellationToken), "cross_workspace_list_visible");
    }

    private static async Task OrderingPaginationAndBoundsAsync(IMemoryRecordStore store, CancellationToken cancellationToken)
    {
        var workspace = new WorkspaceId(Guid.NewGuid());
        var scope = MemoryScope.Shared("team");
        for (var index = 0; index < 4; index++)
            await store.AddAsync(Record(MemoryRecordId.New(), workspace, scope, $"item-{index}", Now.AddMinutes(index)), cancellationToken);
        await store.AddAsync(Record(MemoryRecordId.New(), workspace, MemoryScope.Shared("other"), "other-scope", Now.AddMinutes(10)), cancellationToken);

        var firstPage = await store.ListAsync(workspace, scope, Now.AddHours(1), 0, 2, cancellationToken);
        AreEqual(2, firstPage.Count, "take_not_bounded");
        AreEqual("item-3", firstPage[0].Content, "ordering_not_recent_first");
        AreEqual("item-2", firstPage[1].Content, "ordering_not_stable");
        var secondPage = await store.ListAsync(workspace, scope, Now.AddHours(1), 2, 1, cancellationToken);
        AreEqual(1, secondPage.Count, "skip_take_not_applied");
        AreEqual("item-1", secondPage[0].Content, "pagination_overlap");
    }

    private static async Task ExpiryAndPurgeAsync(IMemoryRecordStore store, CancellationToken cancellationToken)
    {
        var workspace = new WorkspaceId(Guid.NewGuid());
        var otherWorkspace = new WorkspaceId(Guid.NewGuid());
        var scope = MemoryScope.Shared("expiry");
        var active = Record(MemoryRecordId.New(), workspace, scope, "active", Now, Now.AddMinutes(1));
        var expired1 = Record(MemoryRecordId.New(), workspace, scope, "expired-1", Now.AddMinutes(-3), Now.AddMinutes(-2));
        var expired2 = Record(MemoryRecordId.New(), workspace, scope, "expired-2", Now.AddMinutes(-2), Now.AddMinutes(-1));
        var otherExpired = Record(MemoryRecordId.New(), otherWorkspace, scope, "other-expired", Now.AddMinutes(-2), Now.AddMinutes(-1));
        foreach (var record in new[] { active, expired1, expired2, otherExpired }) await store.AddAsync(record, cancellationToken);

        var visible = await store.ListAsync(workspace, scope, Now, 0, 10, cancellationToken);
        AreEqual(1, visible.Count, "expired_record_listed");
        Equal(active, visible[0], "active_record_missing");
        AreEqual(1, await store.PurgeExpiredAsync(workspace, Now, 1, cancellationToken), "purge_batch_limit_ignored");
        AreEqual(1, await store.PurgeExpiredAsync(workspace, Now, 10, cancellationToken), "purge_remaining_count_invalid");
        IsNotNull(await store.GetAsync(otherWorkspace, otherExpired.Id, cancellationToken), "purge_crossed_workspace");
    }

    private static async Task MutationSemanticsAndErrorsAsync(IMemoryRecordStore store, CancellationToken cancellationToken)
    {
        var workspace = new WorkspaceId(Guid.NewGuid());
        var scope = MemoryScope.ForAgent(Guid.NewGuid());
        var otherScope = MemoryScope.ForAgent(Guid.NewGuid());
        var record = Record(MemoryRecordId.New(), workspace, scope, "duplicate", Now);
        await store.AddAsync(record, cancellationToken);
        var duplicateFailed = false;
        try { await store.AddAsync(record, cancellationToken); }
        catch (Exception exception) when (exception is not OperationCanceledException) { duplicateFailed = true; }
        IsTrue(duplicateFailed, "duplicate_write_accepted");
        await store.AddAsync(Record(MemoryRecordId.New(), workspace, scope, "clear", Now.AddMinutes(1)), cancellationToken);
        var survivor = Record(MemoryRecordId.New(), workspace, otherScope, "survivor", Now.AddMinutes(2));
        await store.AddAsync(survivor, cancellationToken);
        AreEqual(2, await store.ClearScopeAsync(workspace, scope, cancellationToken), "clear_scope_count_invalid");
        IsNotNull(await store.GetAsync(workspace, survivor.Id, cancellationToken), "clear_scope_too_broad");
        IsTrue(await store.DeleteAsync(workspace, survivor.Id, cancellationToken), "delete_existing_failed");
        IsFalse(await store.DeleteAsync(workspace, survivor.Id, cancellationToken), "delete_missing_succeeded");
    }

    private static async Task CancellationAsync(IMemoryRecordStore store, CancellationToken cancellationToken)
    {
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cancelled.Cancel();
        var observed = false;
        try { _ = await store.ListAsync(new WorkspaceId(Guid.NewGuid()), null, Now, 0, 1, cancelled.Token); }
        catch (OperationCanceledException) { observed = true; }
        IsTrue(observed, "cancellation_not_observed");
    }

    private static MemoryRecord Record(MemoryRecordId id, WorkspaceId workspaceId, MemoryScope scope, string content, DateTimeOffset createdAt, DateTimeOffset? expiresAt = null, IReadOnlyList<string>? tags = null) =>
        new(id, workspaceId, scope, content, tags ?? ["conformance"], new(MemorySourceKind.Manual, null, "provider conformance", Guid.Parse("10000000-0000-0000-0000-000000000001")), createdAt, expiresAt);

    private static void Equal(MemoryRecord expected, MemoryRecord? actual, string code)
    {
        if (actual is null || expected.Id != actual.Id || expected.WorkspaceId != actual.WorkspaceId || expected.Scope != actual.Scope || expected.Content != actual.Content
            || !expected.Tags.SequenceEqual(actual.Tags) || expected.Provenance != actual.Provenance || expected.CreatedAt != actual.CreatedAt || expected.ExpiresAt != actual.ExpiresAt)
            throw new ConformanceFailureException(code);
    }
    private static void AreEqual<T>(T expected, T actual, string code) where T : IEquatable<T> { if (!expected.Equals(actual)) throw new ConformanceFailureException(code); }
    private static void IsTrue(bool value, string code) { if (!value) throw new ConformanceFailureException(code); }
    private static void IsFalse(bool value, string code) => IsTrue(!value, code);
    private static void IsNull(object? value, string code) { if (value is not null) throw new ConformanceFailureException(code); }
    private static void IsNotNull(object? value, string code) { if (value is null) throw new ConformanceFailureException(code); }
    private static void IsEmpty<T>(IReadOnlyCollection<T> value, string code) { if (value.Count != 0) throw new ConformanceFailureException(code); }

    private sealed class ConformanceFailureException(string code) : Exception
    {
        public string Code { get; } = code;
    }
}
