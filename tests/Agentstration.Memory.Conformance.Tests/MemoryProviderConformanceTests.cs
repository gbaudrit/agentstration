using System.Collections.Concurrent;
using System.Text.Json;
using Agentstration.Aep.Abstractions;
using Agentstration.Aep.AspNetCore;
using Agentstration.Aep.Client;
using Agentstration.Infrastructure.Memory;
using Agentstration.Memory.Storage.Abstractions;
using Agentstration.Memory.Storage.Sqlite;
using Agentstration.Memory.Testing;
using Agentstration.Resources;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Memory.Conformance.Tests;

[TestClass]
public sealed class MemoryProviderConformanceTests
{
    [TestMethod]
    public async Task SqliteStoreSatisfiesTheReusableContract()
    {
        var report = await new MemoryRecordStoreConformanceSuite(CreateSqliteAsync).RunAsync();

        report.EnsureConformant();
        Assert.HasCount(5, report.Scenarios);
    }

    [TestMethod]
    public async Task AepStoreAdapterSatisfiesTheSameReusableContract()
    {
        var report = await new MemoryRecordStoreConformanceSuite(CreateAepAsync).RunAsync();

        report.EnsureConformant();
        Assert.HasCount(5, report.Scenarios);
    }

    [TestMethod]
    public async Task ReportNeverCopiesProviderExceptionMessagesOrMemoryContent()
    {
        const string sensitiveMarker = "memory-secret-marker";
        var report = await new MemoryRecordStoreConformanceSuite(_ => ValueTask.FromResult(new MemoryRecordStoreLease(new FaultingStore(sensitiveMarker)))).RunAsync();

        Assert.IsFalse(report.IsConformant);
        var serialized = JsonSerializer.Serialize(report);
        Assert.IsFalse(serialized.Contains(sensitiveMarker, StringComparison.Ordinal));
        Assert.IsTrue(report.Scenarios.Any(value => value.ExceptionType == nameof(InvalidOperationException)));
    }

    private static ValueTask<MemoryRecordStoreLease> CreateSqliteAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = Path.Combine(Path.GetTempPath(), $"agentstration-memory-conformance-{Guid.NewGuid():N}.db");
        var services = new ServiceCollection().AddSqliteMemoryStorage($"Data Source={path};Pooling=False").BuildServiceProvider();
        var store = services.GetRequiredService<IMemoryRecordStore>();
        return ValueTask.FromResult(new MemoryRecordStoreLease(store, async () =>
        {
            await services.DisposeAsync();
            SqliteConnection.ClearAllPools();
            if (File.Exists(path)) File.Delete(path);
        }));
    }

    private static async ValueTask<MemoryRecordStoreLease> CreateAepAsync(CancellationToken cancellationToken)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseTestServer();
        builder.Services.AddAep(options => options.Extension = new("conformance.memory", "Memory conformance", "1.0.0"));
        builder.Services.AddSingleton<IAepMemoryProvider, InMemoryAepMemoryProvider>();
        var app = builder.Build();
        app.MapAep();
        await app.StartAsync(cancellationToken);
        var http = app.GetTestClient();
        var store = new AepMemoryRecordStore(new AepClient(http).CreateMemoryProvider("memory"));
        return new(store, async () =>
        {
            http.Dispose();
            await app.DisposeAsync();
        });
    }

    private sealed class InMemoryAepMemoryProvider : IAepMemoryProvider
    {
        private readonly ConcurrentDictionary<(Guid WorkspaceId, Guid Id), AepMemoryRecord> records = new();
        public AepMemoryProviderDescriptor Descriptor { get; } = new("memory", "In-memory conformance provider", new());

        public Task WriteAsync(AepMemoryRecord record, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!records.TryAdd((record.WorkspaceId, record.Id), record)) throw new InvalidOperationException("The Memory record already exists.");
            return Task.CompletedTask;
        }

        public Task<AepMemoryRecord?> GetAsync(AepMemoryRecordRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            records.TryGetValue((request.WorkspaceId, request.RecordId), out var value);
            return Task.FromResult(value);
        }

        public Task<IReadOnlyList<AepMemoryRecord>> ListAsync(AepMemoryListRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var values = records.Values.Where(value => value.WorkspaceId == request.WorkspaceId
                    && (request.Scope is null || value.Scope == request.Scope)
                    && (value.ExpiresAt is null || value.ExpiresAt > request.Now))
                .OrderByDescending(value => value.CreatedAt).ThenBy(value => value.Id).Skip(request.Skip).Take(request.Take).ToArray();
            return Task.FromResult<IReadOnlyList<AepMemoryRecord>>(values);
        }

        public Task<bool> DeleteAsync(AepMemoryRecordRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(records.TryRemove((request.WorkspaceId, request.RecordId), out _));
        }

        public Task<int> ClearScopeAsync(AepMemoryScopeRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var keys = records.Where(value => value.Key.WorkspaceId == request.WorkspaceId && value.Value.Scope == request.Scope).Select(value => value.Key).ToArray();
            return Task.FromResult(keys.Count(key => records.TryRemove(key, out _)));
        }

        public Task<int> PurgeExpiredAsync(AepMemoryPurgeRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var keys = records.Where(value => value.Key.WorkspaceId == request.WorkspaceId && value.Value.ExpiresAt is not null && value.Value.ExpiresAt <= request.Now)
                .OrderBy(value => value.Value.ExpiresAt).Take(request.Take).Select(value => value.Key).ToArray();
            return Task.FromResult(keys.Count(key => records.TryRemove(key, out _)));
        }
    }

    private sealed class FaultingStore(string sensitiveMarker) : IMemoryRecordStore
    {
        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task AddAsync(Agentstration.Memory.MemoryRecord record, CancellationToken cancellationToken) => throw new InvalidOperationException(sensitiveMarker);
        public Task<Agentstration.Memory.MemoryRecord?> GetAsync(WorkspaceId workspaceId, Agentstration.Memory.MemoryRecordId id, CancellationToken cancellationToken) => throw new InvalidOperationException(sensitiveMarker);
        public Task<IReadOnlyList<Agentstration.Memory.MemoryRecord>> ListAsync(WorkspaceId workspaceId, Agentstration.Memory.MemoryScope? scope, DateTimeOffset now, int skip, int take, CancellationToken cancellationToken) => throw new InvalidOperationException(sensitiveMarker);
        public Task<bool> DeleteAsync(WorkspaceId workspaceId, Agentstration.Memory.MemoryRecordId id, CancellationToken cancellationToken) => throw new InvalidOperationException(sensitiveMarker);
        public Task<int> ClearScopeAsync(WorkspaceId workspaceId, Agentstration.Memory.MemoryScope scope, CancellationToken cancellationToken) => throw new InvalidOperationException(sensitiveMarker);
        public Task<int> PurgeExpiredAsync(WorkspaceId workspaceId, DateTimeOffset now, int take, CancellationToken cancellationToken) => throw new InvalidOperationException(sensitiveMarker);
    }
}
