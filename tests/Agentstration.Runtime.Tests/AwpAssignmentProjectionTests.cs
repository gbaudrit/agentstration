using System.Text.Json;
using Agentstration.Awp.Abstractions;
using Agentstration.Flows;
using Agentstration.Flows.Application;
using Agentstration.Flows.Storage.Abstractions;
using Agentstration.Flows.Storage.Sqlite;
using Agentstration.Infrastructure.Runtime;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Agentstration.Runtime.Tests;

[TestClass]
public sealed class AwpAssignmentProjectionTests
{
    [TestMethod]
    public async Task StepProjectionPreservesEffectiveToolRouteChildAndDurableArtifact()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"agentstration-awp-projection-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var services = new ServiceCollection();
            services.AddSingleton(TimeProvider.System);
            services.AddSqliteFlowStorage($"Data Source={Path.Combine(directory, "flow.db")}");
            await using var provider = services.BuildServiceProvider();
            var repository = provider.GetRequiredService<IFlowRepository>();
            await repository.InitializeAsync(default);
            var now = DateTimeOffset.Parse("2026-10-10T08:00:00Z",
                System.Globalization.CultureInfo.InvariantCulture);
            var workspaceId = new WorkspaceId(Guid.NewGuid());
            const string runId = "flowrun-root";
            const string storageRunId = "flowrun-child-storage";
            var graph = new FlowGraphDefinition
            {
                EntryStep = "lookup",
                Steps =
                [
                    new ToolRouteFlowStepDefinition
                    {
                        Name = "lookup",
                        ToolSet = new("knowledge", "1.0"),
                        Capability = "search"
                    },
                    new OutputFlowStepDefinition { Name = "completed", Outcome = FlowOutputOutcome.Success }
                ],
                Transitions = [new("lookup-completed", "lookup", "success", "completed")]
            };
            var version = new FlowVersion(workspaceId, new("flow", ResourceNamespace.Default), "1.0", null,
                new DirectFlowDefinition(new(FlowTargetKind.Agent, "unused")),
                new Dictionary<string, string>(), now, graph, "sha256:flow");
            await repository.CreateRunAsync(new FlowRun
            {
                WorkspaceId = workspaceId,
                Id = runId,
                FlowId = version.FlowId,
                FlowVersion = version.Version,
                DefinitionHash = version.DefinitionHash,
                DefinitionSnapshot = version,
                Status = FlowRunStatus.Running,
                Scope = new(Guid.NewGuid(), workspaceId, Guid.NewGuid()),
                Input = JsonSerializer.SerializeToElement(new { query = "hello" }),
                CreatedAt = now,
                StartedAt = now,
                Steps =
                [
                    new FlowStepRun
                    {
                        StepName = "lookup",
                        StepType = "toolRoute",
                        Status = FlowStepRunStatus.Running,
                        Attempt = 1,
                        StartedAt = now
                    },
                    new FlowStepRun { StepName = "completed", StepType = "output" }
                ]
            }, default);
            var stepExecutionId = Guid.NewGuid();
            var assignment = new RuntimeWorkerAssignment
            {
                WorkspaceId = workspaceId,
                TenantId = Guid.NewGuid(),
                Id = new(Guid.NewGuid()),
                TargetKind = RuntimeAssignmentTargetKind.FlowRun,
                TargetRunId = runId,
                RuntimeCapability = AwpRuntimeKinds.MicrosoftAgentFramework,
                RuntimeCapabilityVersion = "1.0",
                ExecutionMaterialVersion = "1.0",
                ExecutionMaterialId = "material-1",
                ExecutionMaterialDigest = "sha256:material",
                State = RuntimeAssignmentState.Assigned,
                CreatedAt = now,
                UpdatedAt = now,
                StepExecutions =
                [
                    new RuntimeAssignmentStepExecution
                    {
                        CommandId = Guid.NewGuid(),
                        Id = stepExecutionId,
                        FlowRunId = runId,
                        FlowVersion = "1.0",
                        FlowDefinitionHash = "sha256:flow",
                        StepDefinitionId = "lookup",
                        StepName = "lookup",
                        StepType = "toolRoute",
                        DefinitionPosition = 0,
                        OpenedAt = now
                    }
                ]
            };
            var payload = JsonSerializer.SerializeToElement(new
            {
                status = "succeeded",
                output = new { answer = "found" },
                tool = new
                {
                    ToolName = "knowledge-search",
                    ToolNamespace = "default",
                    ToolUid = Guid.Parse("20000000-0000-0000-0000-000000000001"),
                    ToolGeneration = 3L,
                    ProviderName = "builtin",
                    ProviderNamespace = "default",
                    ProviderType = "Internal",
                    ExternalToolId = "knowledge.search"
                },
                toolRoute = new AwpFlowToolRoute("knowledge", "default", "1.0", "search", "default"),
                childFlowRunIds = new[] { storageRunId },
                artifactStorageFlowRunId = storageRunId,
                artifacts = new[]
                {
                    new AwpFlowArtifact("flow-artifact-1", "result.json", "application/json", "durable",
                        storageRunId, "staged-artifact-1")
                }
            });
            var projection = new AwpAssignmentProjection(null!, repository, new NullFlowRunEventSink(),
                TimeProvider.System, Options.Create(new FlowRunExecutionOptions()));

            await projection.ProjectEventsAsync(assignment,
            [
                new RuntimeAssignmentExecutionEvent
                {
                    EventId = Guid.NewGuid(),
                    AttemptEventSequence = 1,
                    OccurredAt = now.AddSeconds(1),
                    Kind = "StepCompleted",
                    RunId = runId,
                    StepExecutionId = stepExecutionId,
                    Payload = payload
                }
            ], default);

            var projected = (await repository.GetRunAsync(workspaceId, runId, default))!.Value
                .Steps.Single(value => value.StepName == "lookup");
            Assert.AreEqual(FlowStepRunStatus.Succeeded, projected.Status);
            Assert.AreEqual("knowledge-search", projected.Tools.Single());
            Assert.AreEqual("builtin", projected.Provider);
            Assert.AreEqual("knowledge", projected.ToolRoute?.ToolSetName);
            Assert.AreEqual(storageRunId, projected.ArtifactStorageFlowRunId);
            Assert.IsNull(projected.ChildFlowRunId);
            CollectionAssert.Contains(projected.ChildFlowRunIds.ToArray(), storageRunId);
            var artifact = projected.Artifacts.Single();
            Assert.AreEqual("flow-artifact-1", artifact.ArtifactId);
            Assert.AreEqual("staged-artifact-1", artifact.LocalArtifactId);
            Assert.AreEqual(storageRunId, artifact.StorageFlowRunId);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
