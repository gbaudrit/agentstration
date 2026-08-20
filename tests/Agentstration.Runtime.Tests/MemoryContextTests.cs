using System.Globalization;
using System.Text.Json;
using Agentstration.Memory;
using Agentstration.Memory.Application;
using Agentstration.Memory.Storage.Abstractions;
using Agentstration.Memory.Storage.Sqlite;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;
using Agentstration.Runtime.Core;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Runtime.Tests;

[TestClass]
public sealed class MemoryContextTests
{
    [TestMethod]
    public void ValidationRequiresValidOwnershipProvenanceRetentionAndLimits()
    {
        var now = FixedNow();
        var valid = Record(new WorkspaceId(Guid.NewGuid()), MemoryScope.ForAgent(Guid.NewGuid()), now);

        var normalized = MemoryValidator.Validate(valid with { Content = " fact ", Tags = ["z", "a"] }, now);
        Assert.AreEqual("fact", normalized.Content);
        CollectionAssert.AreEqual(new[] { "a", "z" }, normalized.Tags.ToArray());

        Assert.Throws<MemoryValidationException>(() => MemoryValidator.Validate(valid with { Scope = new(MemoryScopeKind.Agent, "agent-name") }, now));
        Assert.Throws<MemoryValidationException>(() => MemoryValidator.Validate(valid with { Scope = MemoryScope.ForAgent(Guid.Empty) }, now));
        Assert.Throws<MemoryValidationException>(() => MemoryValidator.Validate(valid with { Provenance = valid.Provenance with { SourceKind = MemorySourceKind.RuntimeRun, SourceId = null } }, now));
        Assert.Throws<MemoryValidationException>(() => MemoryValidator.Validate(valid with { Provenance = valid.Provenance with { SourceKind = MemorySourceKind.RuntimeRun, SourceId = new string('x', 257) } }, now));
        Assert.Throws<MemoryValidationException>(() => MemoryValidator.Validate(valid with { ExpiresAt = now }, now));
        Assert.Throws<MemoryValidationException>(() => MemoryValidator.Validate(valid with { Tags = Enumerable.Range(0, MemoryLimits.MaximumTags + 1).Select(value => value.ToString(CultureInfo.InvariantCulture)).ToArray() }, now));
    }

    [TestMethod]
    public async Task SqliteRoundTripsOrdersExpiresDeletesAndIsolatesWorkspaces()
    {
        var clock = new MutableTimeProvider(FixedNow());
        var path = Path.Combine(Path.GetTempPath(), $"agentstration-memory-{Guid.NewGuid():N}.db");
        var provider = new ServiceCollection()
            .AddSqliteMemoryStorage($"Data Source={path};Pooling=False")
            .AddSingleton<TimeProvider>(clock)
            .AddSingleton<MemoryService>()
            .BuildServiceProvider();
        try
        {
            var service = provider.GetRequiredService<MemoryService>();
            await service.InitializeAsync(default);
            var workspace = new WorkspaceId(Guid.NewGuid());
            var otherWorkspace = new WorkspaceId(Guid.NewGuid());
            var scope = MemoryScope.ForAgent(Guid.NewGuid());
            var principal = Guid.NewGuid();

            var first = await service.WriteAsync(new(workspace, scope, "first", ["fact"], MemorySourceKind.Manual, null, "test", principal), default);
            clock.Advance(TimeSpan.FromMinutes(1));
            var second = await service.WriteAsync(new(workspace, scope, "second", [], MemorySourceKind.RuntimeRun, "run-2", "explicit run write", principal, clock.GetUtcNow().AddMinutes(1)), default);

            var listed = await service.ListAsync(workspace, scope, 0, 10, default);
            CollectionAssert.AreEqual(new[] { second.Id, first.Id }, listed.Select(value => value.Id).ToArray());
            Assert.IsNotNull(await service.GetAsync(workspace, first.Id, default));
            Assert.IsNull(await service.GetAsync(otherWorkspace, first.Id, default));
            Assert.IsFalse(await service.DeleteAsync(otherWorkspace, first.Id, default));

            clock.Advance(TimeSpan.FromMinutes(2));
            listed = await service.ListAsync(workspace, scope, 0, 10, default);
            CollectionAssert.AreEqual(new[] { first.Id }, listed.Select(value => value.Id).ToArray());
            Assert.AreEqual(1, await service.PurgeExpiredAsync(workspace, 100, default));
            Assert.IsTrue(await service.DeleteAsync(workspace, first.Id, default));
            await service.WriteAsync(new(workspace, scope, "clear-1", [], MemorySourceKind.Manual, null, "test", principal), default);
            await service.WriteAsync(new(workspace, scope, "clear-2", [], MemorySourceKind.Manual, null, "test", principal), default);
            Assert.AreEqual(2, await service.ClearScopeAsync(workspace, scope, default));
            Assert.AreEqual(0, (await service.ListAsync(workspace, scope, 0, 10, default)).Count);
            var audit = await service.ListAuditAsync(workspace, MemoryProviderReference.Local, 0, 100, default);
            Assert.IsTrue(audit.Any(value => value.Operation == MemoryMutationOperation.Write && value.Outcome == MemoryMutationOutcome.Succeeded));
            Assert.IsTrue(audit.Any(value => value.Operation == MemoryMutationOperation.ClearScope && value.Affected == 2));
            var auditJson = JsonSerializer.Serialize(audit);
            Assert.IsFalse(auditJson.Contains("clear-1", StringComparison.Ordinal));
            Assert.IsFalse(auditJson.Contains("clear-2", StringComparison.Ordinal));
        }
        finally
        {
            await provider.DisposeAsync();
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [TestMethod]
    public async Task ContextAssemblyKeepsConversationContextAndBoundedMemoryDistinct()
    {
        var clock = new MutableTimeProvider(FixedNow());
        var store = new InMemoryMemoryRecordStore();
        var memories = new MemoryService(new SingleMemoryRecordStoreResolver(store), clock);
        var workspace = new WorkspaceId(Guid.NewGuid());
        var principal = Guid.NewGuid();
        var agentId = Guid.NewGuid();
        for (var index = 1; index <= 3; index++)
        {
            await memories.WriteAsync(new(workspace, MemoryScope.ForAgent(agentId), $"fact-{index}", [], MemorySourceKind.Manual, null, "test", principal), default);
            clock.Advance(TimeSpan.FromSeconds(1));
        }

        var authorization = new AllowMemoryReadAuthorization();
        var assembler = new AgentExecutionContextAssembler(memories, authorization);
        var agent = Agent(agentId, new() { MaximumRecords = 2 });
        var result = await assembler.AssembleAsync(new(
            new(Guid.NewGuid(), workspace, principal), agent,
            [new(RuntimeMessageRole.Assistant, "previous answer"), new(RuntimeMessageRole.User, "current input")],
            "work result reference", "session"), default);

        Assert.AreEqual(1, authorization.CallCount);
        Assert.AreEqual(2, result.MemoryRecordIds.Count);
        Assert.HasCount(4, result.Request.Messages!);
        StringAssert.Contains(result.Request.Messages![0].Content, "Execution context");
        StringAssert.Contains(result.Request.Messages[1].Content, "fact-3");
        StringAssert.Contains(result.Request.Messages[1].Content, "fact-2");
        Assert.IsFalse(result.Request.Messages[1].Content.Contains("fact-1", StringComparison.Ordinal));
        Assert.AreEqual(RuntimeMessageRole.Assistant, result.Request.Messages[2].Role);
        Assert.AreEqual(RuntimeMessageRole.User, result.Request.Messages[3].Role);
    }

    [TestMethod]
    public async Task AgentWithoutMemoryDoesNotReadOrWriteMemory()
    {
        var clock = new MutableTimeProvider(FixedNow());
        var store = new InMemoryMemoryRecordStore();
        var authorization = new AllowMemoryReadAuthorization();
        var assembler = new AgentExecutionContextAssembler(new MemoryService(new SingleMemoryRecordStoreResolver(store), clock), authorization);
        var workspace = new WorkspaceId(Guid.NewGuid());

        var agent = Agent(Guid.NewGuid(), null);
        var result = await assembler.AssembleAsync(new(
            new(Guid.NewGuid(), workspace, Guid.NewGuid()), agent,
            [new(RuntimeMessageRole.Tool, "secret-tool-output"), new(RuntimeMessageRole.User, "token=secret")],
            null, "session"), default);

        Assert.AreEqual(0, authorization.CallCount);
        Assert.AreEqual(0, result.MemoryRecordIds.Count);
        Assert.HasCount(2, result.Request.Messages!);
        Assert.AreEqual(0, (await store.ListAsync(workspace, null, clock.GetUtcNow(), 0, 10, default)).Count);
        Assert.IsNull(agent.Memory);
        Assert.AreEqual("hash", agent.DefinitionHash);
    }

    private static MemoryRecord Record(WorkspaceId workspaceId, MemoryScope scope, DateTimeOffset now) => new(
        MemoryRecordId.New(), workspaceId, scope, "fact", [],
        new(MemorySourceKind.Manual, null, "test", Guid.NewGuid()), now, now.AddDays(1));

    private static DateTimeOffset FixedNow() => new(2026, 8, 20, 10, 0, 0, TimeSpan.Zero);

    private static ExecutableAgentDefinition Agent(Guid id, ExecutableAgentMemoryConfiguration? memory) => new()
    {
        AgentId = id,
        AgentKey = "default/test-agent",
        DisplayName = "Test",
        Description = "Test",
        AgentVersion = 1,
        EffectiveInstructions = "Test",
        ModelProfileName = "deterministic",
        RuntimeProfileName = "local",
        EffectiveToolNames = [],
        MiddlewareIds = [],
        Memory = memory,
        Capabilities = [],
        Handler = "default",
        DefinitionHash = "hash"
    };

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan amount) => _now += amount;
    }

    private sealed class AllowMemoryReadAuthorization : IMemoryReadAuthorization
    {
        public int CallCount { get; private set; }
        public Task EnsureReadAsync(RuntimeRunScope scope, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class InMemoryMemoryRecordStore : IMemoryRecordStore
    {
        private readonly List<MemoryRecord> _records = [];

        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task AddAsync(MemoryRecord record, CancellationToken cancellationToken)
        {
            _records.Add(record);
            return Task.CompletedTask;
        }

        public Task<MemoryRecord?> GetAsync(WorkspaceId workspaceId, MemoryRecordId id, CancellationToken cancellationToken) =>
            Task.FromResult(_records.SingleOrDefault(value => value.WorkspaceId == workspaceId && value.Id == id));

        public Task<IReadOnlyList<MemoryRecord>> ListAsync(WorkspaceId workspaceId, MemoryScope? scope, DateTimeOffset now, int skip, int take, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<MemoryRecord>>(_records
                .Where(value => value.WorkspaceId == workspaceId && (scope is null || value.Scope == scope) && (value.ExpiresAt is null || value.ExpiresAt > now))
                .OrderByDescending(value => value.CreatedAt).ThenBy(value => value.Id.Value).Skip(skip).Take(take).ToArray());

        public Task<bool> DeleteAsync(WorkspaceId workspaceId, MemoryRecordId id, CancellationToken cancellationToken) =>
            Task.FromResult(_records.RemoveAll(value => value.WorkspaceId == workspaceId && value.Id == id) == 1);

        public Task<int> ClearScopeAsync(WorkspaceId workspaceId, MemoryScope scope, CancellationToken cancellationToken) =>
            Task.FromResult(_records.RemoveAll(value => value.WorkspaceId == workspaceId && value.Scope == scope));

        public Task<int> PurgeExpiredAsync(WorkspaceId workspaceId, DateTimeOffset now, int take, CancellationToken cancellationToken)
        {
            var expired = _records.Where(value => value.ExpiresAt is not null && value.ExpiresAt <= now).OrderBy(value => value.ExpiresAt).Take(take).ToArray();
            foreach (var record in expired) _records.Remove(record);
            return Task.FromResult(expired.Length);
        }
    }
}
