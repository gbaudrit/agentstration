using System.Text.Json;
using Agentstration.Flow;
using Agentstration.Flow.Storage.Abstractions;
using Agentstration.Flow.Storage.Sqlite;
using Agentstration.Infrastructure.Flows;
using Agentstration.Resources;
using Agentstration.Work;
using Agentstration.Work.Storage.Abstractions;
using Agentstration.Work.Storage.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agentstration.Application.Tests;

[TestClass]
public sealed class WorkplaceFlowConversationProjectionTests
{
    [TestMethod]
    public async Task ParticipantTurnIsProjectedOnceAsFunctionalActivitiesAndAttributedMessage()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"agentstration-participant-projection-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var services = new ServiceCollection();
            services.AddSingleton(TimeProvider.System);
            services.AddSqliteFlowStorage($"Data Source={Path.Combine(directory, "flow.db")};Pooling=False");
            services.AddSqliteWorkPlane($"Data Source={Path.Combine(directory, "work.db")};Pooling=False");
            await using var provider = services.BuildServiceProvider();
            var flows = provider.GetRequiredService<IFlowRepository>();
            var workplace = provider.GetRequiredService<IWorkplaceRepository>();
            await flows.InitializeAsync(default);
            await workplace.InitializeAsync(default);

            var now = new DateTimeOffset(2026, 8, 18, 10, 0, 0, TimeSpan.Zero);
            var workspaceId = new WorkspaceId(Guid.Parse("22222222-2222-2222-2222-222222222222"));
            var interactionId = InteractionId.New();
            var taskId = WorkTaskId.FromWorkItem(WorkItemId.New());
            await workplace.CreateInteractionAsync(new WorkplaceInteraction
            {
                Id = interactionId,
                WorkspaceId = workspaceId,
                EntryId = new("game"),
                StartedAt = now,
                LastActivityAt = now,
                TaskId = taskId
            }, default);
            var definition = new FlowVersion(
                workspaceId,
                new("game-flow"),
                "1.0.0",
                null,
                new OrchestrationFlowDefinition([new(FlowTargetKind.Agent, "alice-agent")], new SequentialOrchestrationPattern()),
                new Dictionary<string, string>(),
                now);
            await flows.CreateRunAsync(new FlowRun
            {
                WorkspaceId = workspaceId,
                Id = "run-1",
                FlowId = definition.FlowId,
                FlowVersion = definition.Version,
                Scope = new(Guid.NewGuid(), workspaceId, Guid.NewGuid()),
                Input = JsonSerializer.SerializeToElement(new { prompt = "Start" }),
                CreatedAt = now,
                DefinitionSnapshot = definition,
                InteractionId = interactionId.ToString(),
                WorkTaskId = taskId.ToString(),
                RuntimeBindings =
                [
                    new RuntimeExecutionBinding
                    {
                        ParticipantId = "alice-player",
                        AgentNamespace = ResourceNamespace.Default,
                        AgentResourceId = "alice-agent",
                        AgentGeneration = 1,
                        DeploymentId = "deployment-1",
                        RevisionId = "revision-1",
                        RuntimeProfileName = "local",
                        ModelProfileName = "deterministic"
                    }
                ]
            }, default);
            var started = await flows.AppendRunEventAsync(new(workspaceId, "run-1", 0, FlowRunEventType.ParticipantTurnStarted, "alice-player", JsonSerializer.SerializeToElement(new { turn = 1 }), now), default);
            await flows.AppendRunEventAsync(new(workspaceId, "run-1", 0, FlowRunEventType.StepOutputDelta, "alice-player", JsonSerializer.SerializeToElement(new { content = "Is it " }), now.AddSeconds(1)), default);
            await flows.AppendRunEventAsync(new(workspaceId, "run-1", 0, FlowRunEventType.StepOutputDelta, "alice-player", JsonSerializer.SerializeToElement(new { content = "a real person?" }), now.AddSeconds(2)), default);
            var completed = await flows.AppendRunEventAsync(new(workspaceId, "run-1", 0, FlowRunEventType.ParticipantTurnCompleted, "alice-player", JsonSerializer.SerializeToElement(new { turn = 1 }), now.AddSeconds(3)), default);
            var sink = new WorkplaceFlowConversationProjectionSink(flows, workplace, [], NullLogger<WorkplaceFlowConversationProjectionSink>.Instance);

            await sink.PublishAsync(started, default);
            await sink.PublishAsync(completed, default);
            await sink.PublishAsync(completed, default);

            var messages = await workplace.ListMessagesAsync(workspaceId, interactionId, default);
            Assert.HasCount(1, messages);
            Assert.AreEqual("Is it a real person?", messages[0].Content);
            Assert.AreEqual("alice-agent", messages[0].AgentResourceId);
            Assert.AreEqual("alice-player", messages[0].Metadata?["participantId"]);
            var activities = await workplace.ListActivitiesAsync(workspaceId, taskId, default);
            Assert.HasCount(2, activities);
            Assert.AreEqual(WorkTaskActivityType.ProgressStarted, activities[0].Type);
            Assert.AreEqual("Preparing a response", activities[0].Title);
            Assert.AreEqual(WorkTaskActivityType.ProgressCompleted, activities[1].Type);
            Assert.AreEqual("Response prepared", activities[1].Title);
            Assert.IsTrue(activities.All(activity => activity.Metadata?["participantId"] == "alice-player"));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
