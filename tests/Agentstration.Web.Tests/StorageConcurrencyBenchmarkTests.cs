using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Agentstration.Flow;
using Agentstration.Flow.Storage.Abstractions;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;
using Agentstration.Work;
using Agentstration.Work.Storage.Abstractions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class StorageConcurrencyBenchmarkTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [TestCategory("Benchmark")]
    public async Task ReportsConcurrentRelationalWriteMetrics()
    {
        var provider = Environment.GetEnvironmentVariable("AGENTSTRATION_STORAGE_BENCHMARK_PROVIDER");
        if (string.IsNullOrWhiteSpace(provider))
            Assert.Inconclusive("Set AGENTSTRATION_STORAGE_BENCHMARK_PROVIDER to Sqlite or PostgreSql to run the storage benchmark.");

        var connectionString = Environment.GetEnvironmentVariable("AGENTSTRATION_TEST_POSTGRES");
        if (provider.Equals("PostgreSql", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(connectionString))
            Assert.Inconclusive("Set AGENTSTRATION_TEST_POSTGRES when benchmarking PostgreSql.");

        var operations = ReadPositiveInteger("AGENTSTRATION_STORAGE_BENCHMARK_OPERATIONS", 100);
        var concurrency = ReadPositiveInteger("AGENTSTRATION_STORAGE_BENCHMARK_CONCURRENCY", 8);
        var dataDirectory = Path.Combine(Path.GetTempPath(), $"agentstration-storage-benchmark-{Guid.NewGuid():N}");
        try
        {
            await using var host = new WebApplicationFactory<global::Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Testing");
                builder.UseSetting("Agentstration:Storage:Provider", provider);
                builder.UseSetting("ConnectionStrings:Agentstration", connectionString);
                builder.UseSetting("Data:Directory", dataDirectory);
                builder.UseSetting("Agentstration:Bootstrap:InitialBootstrapEnabled", "false");
                builder.ConfigureLogging(logging => logging.ClearProviders());
            });
            using var client = host.CreateClient();
            using var readiness = await client.GetAsync("/health/ready");
            Assert.AreEqual(HttpStatusCode.OK, readiness.StatusCode);

            var workItems = host.Services.GetRequiredService<IWorkItemRepository>();
            var flows = host.Services.GetRequiredService<IFlowRepository>();
            var runtimeRuns = host.Services.GetRequiredService<IRuntimeRunStore>();
            var checkpoints = host.Services.GetRequiredService<IRuntimeExecutionStateStore>();
            var workspaceId = new WorkspaceId(Guid.NewGuid());
            var tenantId = Guid.NewGuid();
            var principalId = Guid.NewGuid();
            var latencies = new ConcurrentBag<double>();
            var errors = 0;
            var conflicts = 0;
            using var gate = new SemaphoreSlim(concurrency, concurrency);
            var benchmarkStarted = Stopwatch.GetTimestamp();

            await Task.WhenAll(Enumerable.Range(0, operations).Select(async index =>
            {
                await gate.WaitAsync();
                var operationStarted = Stopwatch.GetTimestamp();
                try
                {
                    var now = DateTimeOffset.UtcNow;
                    var workItem = WorkItem.Create(WorkItemId.New(), workspaceId, "benchmark", $"Operation {index}", now);
                    var storedWorkItem = await workItems.CreateAsync(workItem, default);
                    var expectedVersion = storedWorkItem.Value.Version;
                    workItem.AddMessage("update", "benchmark", Guid.NewGuid(), now.AddMilliseconds(1));
                    await workItems.SaveAsync(workItem, expectedVersion, default);

                    var flowId = new FlowId("benchmark-flow");
                    var flowRunId = $"flow-{Guid.NewGuid():N}";
                    var flowScope = new FlowRunScope(tenantId, workspaceId, principalId);
                    var definition = new DirectFlowDefinition(new FlowTargetReference(FlowTargetKind.Agent, "benchmark-agent"));
                    var version = new FlowVersion(workspaceId, flowId, "1.0.0", null, definition, new Dictionary<string, string>(), now);
                    await flows.CreateRunAsync(new FlowRun
                    {
                        WorkspaceId = workspaceId,
                        Id = flowRunId,
                        FlowId = flowId,
                        FlowVersion = version.Version,
                        Status = FlowRunStatus.Succeeded,
                        Trigger = FlowRunTrigger.Manual,
                        Scope = flowScope,
                        Input = JsonSerializer.SerializeToElement(new { index }),
                        CreatedAt = now,
                        DefinitionSnapshot = version
                    }, default);
                    await flows.AppendRunEventAsync(new FlowRunEvent(
                        workspaceId, flowRunId, 0, FlowRunEventType.FlowRunCompleted, null,
                        JsonSerializer.SerializeToElement(new { index }), now), default);

                    var runtimeRunId = $"runtime-{Guid.NewGuid():N}";
                    var runtimeScope = new RuntimeRunScope(tenantId, workspaceId, principalId);
                    await runtimeRuns.CreateAsync(new RuntimeRun
                    {
                        WorkspaceId = workspaceId,
                        Scope = runtimeScope,
                        Id = runtimeRunId,
                        Name = runtimeRunId,
                        Properties = new RuntimeRunProperties
                        {
                            Agent = new RuntimeAgentReference("benchmark-agent", 1),
                            Input = new RuntimeRunInput { Messages = [new(RuntimeMessageRole.User, "benchmark")] },
                            Execution = new RuntimeExecutionOptions(),
                            Scope = runtimeScope,
                            Initiator = "benchmark"
                        },
                        Status = new RuntimeRunStatus { State = RuntimeRunState.Succeeded, CreatedAt = now, CompletedAt = now }
                    }, default);
                    await runtimeRuns.AppendEventAsync(new RuntimeRunEvent
                    {
                        WorkspaceId = workspaceId,
                        EventId = Guid.NewGuid(),
                        RunId = runtimeRunId,
                        Kind = RuntimeRunEventKind.RunCompleted,
                        Timestamp = now,
                        State = RuntimeRunState.Succeeded
                    }, default);
                    await checkpoints.StoreAsync(new RuntimeExecutionState(
                        workspaceId, runtimeRunId, "benchmark", "checkpoint", JsonSerializer.SerializeToElement(new { index }), now), default);
                }
                catch (Exception exception)
                {
                    Interlocked.Increment(ref errors);
                    if (exception.GetType().Name.Contains("Concurrency", StringComparison.Ordinal))
                        Interlocked.Increment(ref conflicts);
                }
                finally
                {
                    latencies.Add(Stopwatch.GetElapsedTime(operationStarted).TotalMilliseconds);
                    gate.Release();
                }
            }));

            var elapsed = Stopwatch.GetElapsedTime(benchmarkStarted);
            var ordered = latencies.Order().ToArray();
            var report = new
            {
                provider,
                operations,
                concurrency,
                throughputPerSecond = operations / elapsed.TotalSeconds,
                medianMilliseconds = Percentile(ordered, 0.50),
                p95Milliseconds = Percentile(ordered, 0.95),
                errors,
                conflicts,
                retries = 0
            };
            var serializedReport = JsonSerializer.Serialize(report);
            System.Console.WriteLine(serializedReport);
            TestContext.WriteLine(serializedReport);
            var reportPath = Environment.GetEnvironmentVariable("AGENTSTRATION_STORAGE_BENCHMARK_REPORT")
                ?? Path.Combine(TestContext.ResultsDirectory ?? Path.GetTempPath(), $"storage-benchmark-{provider.ToLowerInvariant()}.json");
            var reportDirectory = Path.GetDirectoryName(reportPath);
            if (!string.IsNullOrWhiteSpace(reportDirectory)) Directory.CreateDirectory(reportDirectory);
            await File.WriteAllTextAsync(reportPath, serializedReport);
            TestContext.AddResultFile(reportPath);
            Assert.AreEqual(0, errors, "The benchmark encountered storage errors; inspect the emitted report.");
        }
        finally
        {
            if (Directory.Exists(dataDirectory)) Directory.Delete(dataDirectory, true);
        }
    }

    private static int ReadPositiveInteger(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var value) && value > 0 ? value : fallback;

    private static double Percentile(double[] ordered, double percentile) =>
        ordered[Math.Clamp((int)Math.Ceiling(ordered.Length * percentile) - 1, 0, ordered.Length - 1)];
}
