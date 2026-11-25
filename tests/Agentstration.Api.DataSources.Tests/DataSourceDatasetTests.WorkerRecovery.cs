using System.Text.Json;
using Agentstration.Artifacts.Contracts;
using Agentstration.DataSources;
using Agentstration.DataSources.Contracts;
using Agentstration.Flows;
using Agentstration.Flows.Application;
using Agentstration.Flows.Storage.Abstractions;
using Agentstration.Identity.Contracts;
using Agentstration.Infrastructure.Storage;
using Agentstration.Runtime.Abstractions;
using Agentstration.Runtime.Core;
using Agentstration.Storage.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Agentstration.Management.Tests;

public sealed partial class DataSourceDatasetTests
{
    [TestMethod]
    [DataRow("before-prepare")]
    [DataRow("after-source")]
    [DataRow("mid-transform")]
    [DataRow("child-source")]
    [DataRow("after-register")]
    [DataRow("after-output")]
    public async Task WorkerLeaseLossResumesAcceptedPublicationAcrossTheTerminalAcknowledgementBoundary(string checkpoint)
    {
        var clock = new RecoveryClock();
        PublicationBarrier? publication = null;
        RegistrationInterruption? registrar = null;
        await using var factory = Factory().WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.AddSingleton(provider => ActivatorUtilities.CreateInstance<RuntimeWorkerAssignmentService>(provider, clock));
            services.AddSingleton<StoragePublicationGateway>();
            services.RemoveAll<IStoragePublicationGateway>();
            services.AddSingleton<IStoragePublicationGateway>(provider => publication = new(provider.GetRequiredService<StoragePublicationGateway>()));
            services.RemoveAll<IStoredArtifactRegistrar>();
            services.AddSingleton<IStoredArtifactRegistrar>(provider => registrar = new(provider.GetRequiredService<Agentstration.Artifacts.ArtifactManagementService>(), false));
        }));
        Fixture fixture;
        await using (var preparationWorker = await AwpTestWorker.StartAsync(factory))
            fixture = await PrepareAsync(factory.Services);
        var scopes = factory.Services.GetRequiredService<IRequestContextScopeFactory>();
        using var scope = scopes.PushInstance(fixture.Context);
        var desired = Source("worker-recovery", fixture, JsonSerializer.SerializeToElement(new { dataset = "checkpoint" }));
        if (checkpoint == "mid-transform")
        {
            var call = new FlowCompositionCall
            {
                Function = new("json-project-builtin", "1.0.0", Owner),
                Parameters = JsonSerializer.SerializeToElement(new { properties = new[] { new { name = "dataset", path = "/dataset" } } })
            };
            desired = desired with { Definition = desired.Definition with { Dataset = desired.Definition.Dataset! with { Transformations = [call, call] } } };
        }
        var source = await factory.Services.GetRequiredService<DataSourceManagementService>().CreateAsync(desired, default);
        var acquisitions = factory.Services.GetRequiredService<DataSourceAcquisitionService>();
        var accepted = await acquisitions.StartAsync(source.Value.Namespace, source.Value.Name, Owner, Empty, null, null, default);
        var assignments = factory.Services.GetRequiredService<RuntimeWorkerAssignmentService>();
        var first = (await assignments.ClaimNextAsync(new(Guid.NewGuid()), new(Guid.NewGuid()), "microsoft-agent-framework",
            new HashSet<string> { "1.0" }, new HashSet<string> { RuntimeExecutionMaterialVersions.DurableGraph }, 1, default))!;
        Assert.AreEqual(accepted.Value.FlowRunId, first.Assignment.TargetRunId);
        var repository = factory.Services.GetRequiredService<IFlowRepository>();
        var retained = JsonSerializer.SerializeToElement(new { dataset = "checkpoint" });
        FlowStepRun[] savedSteps;
        FlowStepRun[] savedChildSteps = [];
        string? savedChildId = null;
        using (scopes.Push(fixture.Context))
        {
            var stored = (await repository.GetRunExactAsync(accepted.Value.ScopeRef!.Value, accepted.Value.FlowRunId, default))!;
            var graph = stored.Value.DefinitionSnapshot.Graph!;
            var sourceCall = graph.Steps.OfType<FlowCallStepDefinition>().Single(step => step.CompositionPoint == "acquisition");
            if (checkpoint == "child-source")
            {
                var child = await factory.Services.GetRequiredService<FlowRunService>().CreateOrGetAssignedChildExactAsync(
                    accepted.Value.ScopeRef!.Value, accepted.Value.FlowRunId, sourceCall.Name, stored.Value.Input, "flowCall", null, default);
                savedChildId = child.Value.Id;
                var childGraph = child.Value.DefinitionSnapshot.Graph!;
                var childSteps = child.Value.Steps.Select(step => step.StepType == "output" ? step : step with
                {
                    Status = FlowStepRunStatus.Succeeded,
                    Output = retained,
                    SelectedTransition = childGraph.Transitions.Single(value => value.FromStep == step.StepName && value.Event is not ("failed" or "error")).Id
                }).ToArray();
                savedChildSteps = childSteps.Where(step => step.Status == FlowStepRunStatus.Succeeded).ToArray();
                await repository.UpdateRunAsync(child.Value with
                {
                    Status = FlowRunStatus.Running,
                    Steps = childSteps,
                    ExecutionLeaseId = first.Assignment.Id.Value.ToString("N"),
                    ExecutionLeaseExpiresAt = first.Assignment.CurrentAttempt!.LeaseExpiresAt
                }, child.ETag, default);
                stored = (await repository.GetRunExactAsync(accepted.Value.ScopeRef!.Value, accepted.Value.FlowRunId, default))!;
            }
            var beforePublication = true;
            var functions = 0;
            var steps = stored.Value.Steps.Select(step =>
            {
                if (step.StepName == "publication-prepare" || checkpoint == "after-source" && step.StepName == "transformations"
                    || checkpoint == "child-source" && step.StepName == sourceCall.Name) beforePublication = false;
                if (checkpoint == "mid-transform" && graph.Steps.Single(value => value.Name == step.StepName) is ToolFlowStepDefinition { FunctionBinding: not null }
                    && ++functions == 2) beforePublication = false;
                return beforePublication ? step with
                {
                    Status = FlowStepRunStatus.Succeeded,
                    Output = retained,
                    SelectedTransition = graph.Transitions.Single(value => value.FromStep == step.StepName).Id
                } : step;
            }).ToArray();
            savedSteps = steps.Where(step => step.Status == FlowStepRunStatus.Succeeded).ToArray();
            await repository.UpdateRunAsync(stored.Value with
            {
                Status = FlowRunStatus.Running,
                Steps = steps,
                ExecutionLeaseId = first.Assignment.Id.Value.ToString("N"),
                ExecutionLeaseExpiresAt = first.Assignment.CurrentAttempt!.LeaseExpiresAt
            }, stored.ETag, default);
        }
        string? registeredArtifact = null;
        if (checkpoint is "after-register" or "after-output")
        {
            var execution = factory.Services.GetRequiredService<DataSourceDatasetAcquisitionService>();
            foreach (var phase in new[] { "prepare", "seal", "register" })
                await execution.ExecutePublicationStepAsync(accepted.Value.Name, accepted.Value.FlowRunId, phase, default);
            registeredArtifact = (await acquisitions.GetAsync(accepted.Value.Namespace, accepted.Value.Name, default)).Value.Manifest!.Artifacts.Single().ArtifactId;
            using var workspace = scopes.Push(fixture.Context);
            var stored = (await repository.GetRunExactAsync(accepted.Value.ScopeRef!.Value, accepted.Value.FlowRunId, default))!;
            var graph = stored.Value.DefinitionSnapshot.Graph!;
            await repository.UpdateRunAsync(stored.Value with
            {
                Steps = stored.Value.Steps.Select(step => step.StepName is "publication-prepare" or "publication-seal" or "publication-register"
                    ? step with
                    {
                        Status = FlowStepRunStatus.Succeeded,
                        Output = Empty,
                        SelectedTransition = graph.Transitions.Single(value => value.FromStep == step.StepName && value.Event == "success").Id
                    }
                    : checkpoint == "after-output" && step.StepName == "output"
                        ? step with { Status = FlowStepRunStatus.Succeeded, Output = retained } : step).ToArray()
            }, stored.ETag, default);
        }
        clock.Advance(TimeSpan.FromSeconds(46));
        Assert.IsTrue((await assignments.ExpireLeasesAsync(10, default)).Count <= 1);
        var failed = (await acquisitions.GetAsync(accepted.Value.Namespace, accepted.Value.Name, default)).Value;
        for (var attempt = 0; attempt < 300 && failed.State != DataSourceAcquisitionState.Failed; attempt++)
        {
            await Task.Delay(20);
            failed = (await acquisitions.GetAsync(accepted.Value.Namespace, accepted.Value.Name, default)).Value;
        }
        Assert.AreEqual(DataSourceAcquisitionState.Failed, failed.State);
        Assert.AreEqual("worker_lost", failed.ErrorCode);
        _ = await acquisitions.RetryAsync(failed.Namespace, failed.Name, null, default);
        await using var worker = await AwpTestWorker.StartAsync(factory);
        using var client = factory.CreateClient(); SelectConsoleContext(client, Owner);
        var completed = await WaitAsync(client, failed.Name);
        Assert.AreEqual(accepted.Value.Uid, completed.Uid);
        Assert.AreEqual(accepted.Value.FlowRunId, completed.FlowRunId);
        Assert.AreEqual(1, publication!.Prepares);
        Assert.AreEqual(1, publication.Seals);
        Assert.AreEqual(1, registrar!.Calls);
        if (registeredArtifact is not null) Assert.AreEqual(registeredArtifact, completed.Manifest!.Artifacts.Single().ArtifactId);
        Assert.AreEqual("checkpoint", await ReadDatasetAsync(client, completed.Name));
        using (scopes.Push(fixture.Context))
        {
            var root = (await repository.GetRunExactAsync(completed.ScopeRef!.Value, completed.FlowRunId, default))!.Value;
            Assert.AreEqual(FlowRunStatus.Succeeded, root.Status);
            Assert.AreEqual(savedChildId is null ? 0 : 1, root.Steps.SelectMany(step => step.ChildFlowRunIds).Distinct().Count(),
                "Restored source checkpoints must not create a new operation child.");
            foreach (var saved in savedSteps)
                Assert.IsTrue(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(saved), JsonSerializer.SerializeToElement(root.Steps.Single(step => step.StepName == saved.StepName))),
                    "Successful source and transformation checkpoints must remain unchanged.");
            if (savedChildId is not null)
            {
                var child = (await repository.GetRunExactAsync(completed.ScopeRef!.Value, savedChildId, default))!.Value;
                Assert.AreEqual(FlowRunStatus.Succeeded, child.Status);
                foreach (var saved in savedChildSteps)
                    Assert.IsTrue(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(saved), JsonSerializer.SerializeToElement(child.Steps.Single(step => step.StepName == saved.StepName))),
                        "Successful child checkpoints must not execute again after transport interruption.");
            }
        }
        var resumed = (await assignments.GetByTargetExactAsync(completed.ScopeRef!.Value, RuntimeAssignmentTargetKind.FlowRun, completed.FlowRunId, default))!.Value;
        Assert.HasCount(2, resumed.Attempts);
        Assert.AreEqual(first.Ownership.FencingGeneration + 1, resumed.FencingGeneration);
        await Assert.ThrowsAsync<RuntimeAssignmentException>(() => assignments.HeartbeatAsync(first.Ownership, default));
    }

    private sealed class RecoveryClock : TimeProvider
    {
        private long offset;
        public override DateTimeOffset GetUtcNow() => TimeProvider.System.GetUtcNow().AddTicks(Interlocked.Read(ref offset));
        public void Advance(TimeSpan interval) => Interlocked.Add(ref offset, interval.Ticks);
    }
}
