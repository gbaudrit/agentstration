using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json;
using Agentstration.Aep.Abstractions;
using Agentstration.Aep.AspNetCore;
using Agentstration.Aep.Client;
using Agentstration.Infrastructure.Memory;
using Agentstration.Memory;
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

    [TestMethod]
    public async Task OutOfProcessSqliteExtensionSatisfiesTheReusableContract()
    {
        var report = await new MemoryRecordStoreConformanceSuite(CreateOutOfProcessAepAsync).RunAsync();

        report.EnsureConformant();
        Assert.HasCount(5, report.Scenarios);
    }

    [TestMethod]
    public async Task OutOfProcessSqliteExtensionPersistsAcrossRestart()
    {
        var directory = CreateTemporaryDirectory();
        var databasePath = Path.Combine(directory, "memory.db");
        var workspace = new WorkspaceId(Guid.NewGuid());
        var id = MemoryRecordId.New();
        var record = new MemoryRecord(
            id,
            workspace,
            MemoryScope.Shared("restart"),
            "persisted across extension restart",
            ["durable"],
            new(MemorySourceKind.Manual, null, "out-of-process restart test", Guid.NewGuid()),
            new DateTimeOffset(2026, 8, 20, 12, 0, 0, TimeSpan.Zero));
        try
        {
            await using (var first = await MemoryExtensionProcess.StartAsync(databasePath, default))
            {
                var provider = first.Client.CreateMemoryProvider("sqlite");
                Assert.AreEqual("available", (await provider.GetHealthAsync()).Status);
                var store = new AepMemoryRecordStore(provider);
                await store.AddAsync(record, default);
            }

            await using (var second = await MemoryExtensionProcess.StartAsync(databasePath, default))
            {
                var manifest = await second.Client.DiscoverAsync();
                Assert.AreEqual("Agentstration.Extensions.Memory.Sqlite", manifest.Extension.Id);
                var store = new AepMemoryRecordStore(second.Client.CreateMemoryProvider("sqlite"));
                var restored = await store.GetAsync(workspace, id, default);
                Assert.IsNotNull(restored);
                Assert.AreEqual(record.Id, restored.Id);
                Assert.AreEqual(record.WorkspaceId, restored.WorkspaceId);
                Assert.AreEqual(record.Scope, restored.Scope);
                Assert.AreEqual(record.Content, restored.Content);
                CollectionAssert.AreEqual(record.Tags.ToArray(), restored.Tags.ToArray());
                Assert.AreEqual(record.Provenance, restored.Provenance);
                Assert.AreEqual(record.CreatedAt, restored.CreatedAt);
                Assert.AreEqual(record.ExpiresAt, restored.ExpiresAt);
                Assert.IsNull(await store.GetAsync(new WorkspaceId(Guid.NewGuid()), id, default));
            }
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
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

    private static async ValueTask<MemoryRecordStoreLease> CreateOutOfProcessAepAsync(CancellationToken cancellationToken)
    {
        var directory = CreateTemporaryDirectory();
        var host = await MemoryExtensionProcess.StartAsync(Path.Combine(directory, "memory.db"), cancellationToken);
        var store = new AepMemoryRecordStore(host.Client.CreateMemoryProvider("sqlite"));
        return new(store, async () =>
        {
            await host.DisposeAsync();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        });
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"agentstration-memory-extension-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
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

    private sealed class MemoryExtensionProcess : IAsyncDisposable
    {
        private readonly Process process;
        private readonly HttpClient http;
        private readonly ConcurrentQueue<string> output;
        public AepClient Client { get; }

        private MemoryExtensionProcess(Process process, HttpClient http, ConcurrentQueue<string> output)
        {
            this.process = process;
            this.http = http;
            this.output = output;
            Client = new(http);
        }

        public static async Task<MemoryExtensionProcess> StartAsync(string databasePath, CancellationToken cancellationToken)
        {
            var repositoryRoot = FindRepositoryRoot();
            var projectPath = Path.Combine(repositoryRoot, "src", "Agentstration.Extensions.Memory.Sqlite", "Agentstration.Extensions.Memory.Sqlite.csproj");
            var port = ReservePort();
            var startInfo = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = repositoryRoot,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.ArgumentList.Add("run");
            startInfo.ArgumentList.Add("--project");
            startInfo.ArgumentList.Add(projectPath);
            startInfo.ArgumentList.Add("--configuration");
            startInfo.ArgumentList.Add("Release");
            startInfo.ArgumentList.Add("--no-build");
            startInfo.ArgumentList.Add("--no-launch-profile");
            startInfo.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}";
            startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "Testing";
            startInfo.Environment["MemorySqlite__Path"] = databasePath;
            var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            var output = new ConcurrentQueue<string>();
            process.OutputDataReceived += (_, args) => Capture(output, args.Data);
            process.ErrorDataReceived += (_, args) => Capture(output, args.Data);
            if (!process.Start()) throw new InvalidOperationException("The Memory extension process could not be started.");
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            var http = new HttpClient { BaseAddress = new($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(2) };
            var host = new MemoryExtensionProcess(process, http, output);
            try
            {
                await host.WaitUntilReadyAsync(cancellationToken);
                return host;
            }
            catch
            {
                await host.DisposeAsync();
                throw;
            }
        }

        private async Task WaitUntilReadyAsync(CancellationToken cancellationToken)
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
            while (DateTimeOffset.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (process.HasExited) throw new InvalidOperationException($"The Memory extension exited with code {process.ExitCode}. {string.Join(' ', output.TakeLast(10))}");
                try
                {
                    using var response = await http.GetAsync(AepProtocol.DiscoveryPath, cancellationToken);
                    if (response.IsSuccessStatusCode) return;
                }
                catch (HttpRequestException) { }
                catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested) { }
                await Task.Delay(100, cancellationToken);
            }
            throw new TimeoutException($"The Memory extension did not become ready. {string.Join(' ', output.TakeLast(10))}");
        }

        public async ValueTask DisposeAsync()
        {
            http.Dispose();
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
            process.Dispose();
        }

        private static int ReservePort()
        {
            var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
            listener.Start();
            var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        private static string FindRepositoryRoot()
        {
            for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
                if (File.Exists(Path.Combine(current.FullName, "Agentstration.slnx"))) return current.FullName;
            throw new InvalidOperationException("The Agentstration repository root could not be located.");
        }

        private static void Capture(ConcurrentQueue<string> output, string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            output.Enqueue(value);
            while (output.Count > 100) output.TryDequeue(out _);
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
