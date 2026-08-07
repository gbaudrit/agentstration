extern alias workapi;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agentstration.Application.Work;
using Agentstration.Flow.Application;
using Agentstration.Flow.Contracts;
using Agentstration.Management.Core;
using Agentstration.Work;
using Agentstration.Work.Contracts;
using Agentstration.Work.Storage.Abstractions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using WorkApiProgram = workapi::Program;

namespace Agentstration.Work.Api.Tests;

[TestClass]
public sealed class WorkplaceApiTests
{
    [TestMethod]
    public async Task AgentBindingPublishesPinnedDirectAgentFlowAndDraftDoesNotAffectWorkplace()
    {
        var dataDirectory = Path.Combine(Path.GetTempPath(), "agentstration-work-api-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDirectory);
        try
        {
            await using var factory = new WebApplicationFactory<WorkApiProgram>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Testing");
                builder.UseSetting("Data:Directory", dataDirectory);
            });
            using var client = factory.CreateClient();

            var apiFlowName = "specialized-agent-routing";
            using (var createdFlow = await client.PostAsJsonAsync("/api/flows/", new CreateFlowRequest(
                apiFlowName,
                "Created through the canonical Flow API.",
                Agentstration.Flow.FlowKind.Direct,
                "1.0.0",
                true,
                new Agentstration.Flow.DirectFlowSpec(new Agentstration.Flow.FlowTargetReference(
                    Agentstration.Flow.FlowTargetKind.Agent,
                    "/resourceGroups/default/providers/Agentstration.Agents/agents/dotnet-expert")))))
            {
                Assert.AreEqual(HttpStatusCode.Created, createdFlow.StatusCode);
            }
            using (var publishedFlow = await client.PostAsJsonAsync($"/api/flows/{apiFlowName}/versions", new CreateFlowVersionRequest("1.0.0")))
            {
                publishedFlow.EnsureSuccessStatusCode();
            }
            var availableFlows = await client.GetFromJsonAsync<ResourcePickerItem[]>("/api/resources?type=Agentstration.Flows%2Fflows") ?? [];
            Assert.IsTrue(availableFlows.Any(value => value.Name == apiFlowName));

            var administered = await client.GetFromJsonAsync<EntryDraftResponse>("/api/management/entries/universal-request");
            Assert.IsNotNull(administered);
            Assert.AreEqual(EntryBindingKind.Agent, administered.Value.Binding.Kind);
            Assert.IsNotNull(administered.Published);
            StringAssert.Contains(administered.Published.ResolvedTarget.FlowResourceId, "system-direct-agent-dotnet-expert");
            Assert.AreEqual(EntryVersionStrategy.Pinned, administered.Published.ResolvedTarget.VersionStrategy);
            var apiFlowEntry = administered.Value with
            {
                Id = new EntryId("/resourceGroups/default/providers/Agentstration.Work/entries/specialized-request"),
                Name = "specialized-request",
                Binding = new EntryBinding(EntryBindingKind.Flow, $"/resourceGroups/default/providers/Agentstration.Flows/flows/{apiFlowName}")
            };
            using (var savedApiFlowEntry = await client.PutAsJsonAsync("/api/management/entries/specialized-request", apiFlowEntry))
            {
                savedApiFlowEntry.EnsureSuccessStatusCode();
            }
            using (var publishedApiFlowEntry = await client.PostAsync("/api/management/entries/specialized-request/publish", null))
            {
                publishedApiFlowEntry.EnsureSuccessStatusCode();
                var value = await publishedApiFlowEntry.Content.ReadFromJsonAsync<EntryResource>();
                Assert.AreEqual(apiFlowName, value?.ResolvedTarget.FlowResourceId.Split('/').Last());
                Assert.AreEqual("1.0.0", value?.ResolvedTarget.Version);
            }
            using (var dependencyScope = factory.Services.CreateScope())
            {
                var management = dependencyScope.ServiceProvider.GetRequiredService<AgentManagementService>();
                var agent = await management.GetAgentAsync(administered.Value.Binding.ResourceId, default);
                Assert.IsNotNull(agent);
                var directFlow = await dependencyScope.ServiceProvider.GetRequiredService<Agentstration.Flow.Application.FlowService>().GetAsync(new Agentstration.Flow.FlowId("system-direct-agent-dotnet-expert"), default);
                Assert.IsNotNull(directFlow);
                Assert.AreEqual(agent.Value.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture), directFlow.Value.Metadata["sourceAgentGeneration"]);
                Assert.AreEqual($"1.0.{agent.Value.Generation - 1}", administered.Published.ResolvedTarget.Version);
                var exception = await Assert.ThrowsAsync<AgentDefinitionValidationException>(() => management.DeleteAgentAsync(agent.Value.Id, agent.ETag, default));
                Assert.AreEqual("resource_in_use", exception.Code);

                var flows = dependencyScope.ServiceProvider.GetRequiredService<FlowService>();
                var systemDelete = await Assert.ThrowsAsync<Agentstration.Flow.FlowValidationException>(() => flows.DeleteAsync(directFlow.Value.Id, directFlow.ETag, default));
                Assert.AreEqual("system_flow_managed", systemDelete.Code);
                var referencedFlow = await flows.GetAsync(new("universal-router"), default);
                Assert.IsNotNull(referencedFlow);
                var referencedDelete = await Assert.ThrowsAsync<Agentstration.Flow.FlowValidationException>(() => flows.DeleteAsync(referencedFlow.Value.Id, referencedFlow.ETag, default));
                Assert.AreEqual("flow_in_use", referencedDelete.Code);
                var schemaFlow = await flows.CreateAsync(new CreateFlowCommand(
                    "schema-flow", null, Agentstration.Flow.FlowKind.Direct, "1.0.0", true,
                    new Agentstration.Flow.DirectFlowSpec(new Agentstration.Flow.FlowTargetReference(Agentstration.Flow.FlowTargetKind.Agent, administered.Value.Binding.ResourceId))), default);
                var graph = new Agentstration.Flow.FlowGraphDefinition
                {
                    EntryStep = "input",
                    InputSchema = JsonSerializer.SerializeToElement(new { type = "object", properties = new { request = new { type = "string" } } }),
                    Steps = [new Agentstration.Flow.InputFlowStepDefinition { Name = "input" }, new Agentstration.Flow.OutputFlowStepDefinition { Name = "output" }],
                    Transitions = [new("complete", "input", "completed", "output")]
                };
                await flows.UpdateAsync(new("schema-flow"), new UpdateFlowCommand(null, Agentstration.Flow.FlowKind.Direct, "1.0.0", true, schemaFlow.Value.Spec, Graph: graph), schemaFlow.ETag, default);
                await flows.PublishVersionAsync(new("schema-flow"), "1.0.0", activate: true, default);
            }

            var incompatibleInput = administered.Value with
            {
                Id = new EntryId("/resourceGroups/default/providers/Agentstration.Work/entries/incompatible-input"),
                Name = "incompatible-input",
                Binding = new EntryBinding(EntryBindingKind.Flow, "/resourceGroups/default/providers/Agentstration.Flows/flows/schema-flow"),
                Presentation = administered.Value.Presentation with
                {
                    Fields = [new EntryFieldDefinition { Name = "request", Type = EntryFieldType.Number, Required = true, Role = EntryFieldRole.PrimaryInput }]
                }
            };
            using var incompatibleSaved = await client.PutAsJsonAsync("/api/management/entries/incompatible-input", incompatibleInput);
            incompatibleSaved.EnsureSuccessStatusCode();
            using var incompatibleValidation = await client.PostAsync("/api/management/entries/incompatible-input/validate", null);
            incompatibleValidation.EnsureSuccessStatusCode();
            var incompatible = await incompatibleValidation.Content.ReadFromJsonAsync<EntryValidationResponse>();
            Assert.IsFalse(incompatible?.IsValid);
            Assert.AreEqual("entry_flow_input_incompatible", incompatible?.Issues.Single().Code);

            var missingTarget = administered.Value with
            {
                Id = new EntryId("/resourceGroups/default/providers/Agentstration.Work/entries/missing-target"),
                Name = "missing-target",
                Binding = new EntryBinding(EntryBindingKind.Agent, "/resourceGroups/default/providers/Agentstration.Agents/agents/missing")
            };
            using var missingSaved = await client.PutAsJsonAsync("/api/management/entries/missing-target", missingTarget);
            missingSaved.EnsureSuccessStatusCode();
            var missingValidation = await client.PostAsync("/api/management/entries/missing-target/validate", null);
            missingValidation.EnsureSuccessStatusCode();
            var invalid = await missingValidation.Content.ReadFromJsonAsync<EntryValidationResponse>();
            Assert.IsFalse(invalid?.IsValid);
            Assert.AreEqual("entry_target_not_found", invalid?.Issues.Single().Code);
            using var missingPublished = await client.PostAsync("/api/management/entries/missing-target/publish", null);
            Assert.AreEqual(HttpStatusCode.NotFound, missingPublished.StatusCode);

            var changedDraft = administered.Value with { DisplayName = "Unpublished name" };
            using var saved = await client.PutAsJsonAsync("/api/management/entries/universal-request", changedDraft);
            saved.EnsureSuccessStatusCode();
            var workplaceBeforePublish = await client.GetFromJsonAsync<EntryResponse>("/api/entries/universal-request");
            Assert.AreNotEqual("Unpublished name", workplaceBeforePublish?.DisplayName);

            using var publishedResponse = await client.PostAsync("/api/management/entries/universal-request/publish", null);
            publishedResponse.EnsureSuccessStatusCode();
            var workplaceAfterPublish = await client.GetFromJsonAsync<EntryResponse>("/api/entries/universal-request");
            Assert.AreEqual("Unpublished name", workplaceAfterPublish?.DisplayName);
            Assert.IsFalse(string.IsNullOrWhiteSpace(workplaceAfterPublish?.ResolvedTarget.Version));

            var workspaceDraft = await client.GetFromJsonAsync<WorkplaceWorkspaceDraftResponse>("/api/management/workspaces/personal");
            Assert.IsNotNull(workspaceDraft?.Published);
            using var workspaceSaved = await client.PutAsJsonAsync("/api/management/workspaces/personal", workspaceDraft!.Value with { DisplayName = "Unpublished workspace" });
            workspaceSaved.EnsureSuccessStatusCode();
            var workspaceBeforePublish = await client.GetFromJsonAsync<WorkplaceWorkspaceResponse>("/api/workspaces/personal");
            Assert.AreNotEqual("Unpublished workspace", workspaceBeforePublish?.DisplayName);
            using var workspacePublished = await client.PostAsync("/api/management/workspaces/personal/publish", null);
            workspacePublished.EnsureSuccessStatusCode();
            var workspaceAfterPublish = await client.GetFromJsonAsync<WorkplaceWorkspaceResponse>("/api/workspaces/personal");
            Assert.AreEqual("Unpublished workspace", workspaceAfterPublish?.DisplayName);
            Assert.AreEqual(1, workspaceAfterPublish?.Entries.Count(value => value.Role == WorkspaceEntryRole.Primary));

            using var submittedResponse = await client.PostAsJsonAsync(
                "/api/workspaces/personal/entries/universal-request/interactions",
                new CreateInteractionRequest("personal", new Dictionary<string, JsonElement> { ["request"] = JsonSerializer.SerializeToElement("Explain records briefly.") }));
            submittedResponse.EnsureSuccessStatusCode();
            var submitted = await submittedResponse.Content.ReadFromJsonAsync<EntrySubmissionResponse>();
            Assert.IsNotNull(submitted?.Task);
            var outputs = await WaitForOutputsAsync(client, submitted.Task.Id, 1);
            Assert.IsNotNull(outputs.Results.Single().FlowRunId);
            using var scope = factory.Services.CreateScope();
            var flowRuns = scope.ServiceProvider.GetRequiredService<FlowRunService>();
            var run = await flowRuns.GetAsync(outputs.Results.Single().FlowRunId!, default);
            Assert.IsNotNull(run);
            Assert.AreEqual("system-direct-agent-dotnet-expert", run.Value.FlowId.Value);
            Assert.AreEqual(Agentstration.Flow.FlowRunStatus.Succeeded, run.Value.Status);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(dataDirectory)) Directory.Delete(dataDirectory, recursive: true);
        }
    }

    [TestMethod]
    public async Task ImmediateResponseConversationCanContinueWithoutCreatingATask()
    {
        var dataDirectory = Path.Combine(Path.GetTempPath(), "agentstration-work-api-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDirectory);
        try
        {
            await using var factory = new WebApplicationFactory<WorkApiProgram>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Testing");
                builder.UseSetting("Data:Directory", dataDirectory);
            });
            using var client = factory.CreateClient();
            using var submittedResponse = await client.PostAsJsonAsync(
                "/api/workspaces/personal/entries/quick-answer/interactions",
                new CreateInteractionRequest("personal", new Dictionary<string, JsonElement> { ["request"] = JsonSerializer.SerializeToElement("Remember this idea.") }));
            submittedResponse.EnsureSuccessStatusCode();
            var submitted = await submittedResponse.Content.ReadFromJsonAsync<EntrySubmissionResponse>();
            Assert.IsNotNull(submitted);
            Assert.IsNull(submitted.Task);
            Assert.AreEqual(InteractionStatus.Idle, submitted.Interaction.Status);

            using var continuedResponse = await client.PostAsJsonAsync(
                $"/api/workspaces/personal/interactions/{submitted.Interaction.Id}/messages",
                new AddConversationMessageRequest("What did I just ask you to remember?"));
            Assert.AreEqual(HttpStatusCode.Accepted, continuedResponse.StatusCode);
            var continued = await continuedResponse.Content.ReadFromJsonAsync<AddConversationMessageResponse>();
            Assert.IsNotNull(continued);
            Assert.IsNull(continued.Task);
            Assert.AreEqual(InteractionStatus.Idle, continued.Interaction.Status);
            var messages = await client.GetFromJsonAsync<ConversationMessage[]>($"/api/workspaces/personal/interactions/{submitted.Interaction.Id}/messages") ?? [];
            Assert.HasCount(4, messages);
            Assert.AreEqual(ConversationRole.Agentstration, messages[^1].Role);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(dataDirectory)) Directory.Delete(dataDirectory, recursive: true);
        }
    }

    [TestMethod]
    public async Task CompletedTaskCanContinueWithANewCorrelatedFlowRunAndVersionedOutputs()
    {
        var dataDirectory = Path.Combine(Path.GetTempPath(), "agentstration-work-api-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDirectory);
        try
        {
            await using var factory = new WebApplicationFactory<WorkApiProgram>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Testing");
                builder.UseSetting("Data:Directory", dataDirectory);
            });
            using var client = factory.CreateClient();
            using var submittedResponse = await client.PostAsJsonAsync(
                "/api/workspaces/personal/entries/prepare-report/interactions",
                new CreateInteractionRequest("personal", new Dictionary<string, JsonElement>
                {
                    ["request"] = JsonSerializer.SerializeToElement("Prepare a monthly report about sales performance.")
                }));
            Assert.AreEqual(HttpStatusCode.Created, submittedResponse.StatusCode);
            var submitted = await submittedResponse.Content.ReadFromJsonAsync<EntrySubmissionResponse>();
            Assert.IsNotNull(submitted?.Task);
            Assert.IsInstanceOfType<CreateTaskAction>(submitted.Action);

            var firstOutputs = await WaitForOutputsAsync(client, submitted.Task.Id, 1);
            var firstFlowRunId = firstOutputs.Results.Single().FlowRunId;
            Assert.IsFalse(string.IsNullOrWhiteSpace(firstFlowRunId));
            var idle = await client.GetFromJsonAsync<InteractionResponse>($"/api/workspaces/personal/interactions/{submitted.Interaction.Id}");
            Assert.AreEqual(InteractionStatus.Idle, idle?.Status);

            using var continuationResponse = await client.PostAsJsonAsync(
                $"/api/workspaces/personal/interactions/{submitted.Interaction.Id}/messages",
                new AddConversationMessageRequest("Make it shorter and suitable for executives."));
            Assert.AreEqual(HttpStatusCode.Accepted, continuationResponse.StatusCode);
            var continuation = await continuationResponse.Content.ReadFromJsonAsync<AddConversationMessageResponse>();
            Assert.AreEqual(submitted.Task.Id, continuation?.Task?.Id, "A transformation continues the same public Task.");

            using (var scope = factory.Services.CreateScope())
            {
                var workItems = scope.ServiceProvider.GetRequiredService<WorkItemService>();
                var page = await workItems.QueryAsync(new WorkItemQuery(Take: 50), default);
                var child = page.Items.Select(value => value.Value).Single(value => value.Metadata.ContainsKey("workplace.continuation"));
                Assert.AreEqual(firstFlowRunId, child.Metadata["workplace.parentFlowRunId"]);
                Assert.AreEqual(submitted.Interaction.Id.ToString(), child.Metadata["workplace.interactionId"]);
            }

            var outputs = await WaitForOutputsAsync(client, submitted.Task.Id, 2);
            Assert.HasCount(2, outputs.Results);
            Assert.HasCount(2, outputs.Artifacts);
            Assert.AreEqual(1, outputs.Results[0].Sequence);
            Assert.AreEqual(2, outputs.Results[1].Sequence);
            Assert.AreNotEqual(outputs.Results[0].FlowRunId, outputs.Results[1].FlowRunId);
            Assert.AreEqual("executive-summary.txt", outputs.Artifacts[1].Name);
            using (var scope = factory.Services.CreateScope())
            {
                var flowRuns = scope.ServiceProvider.GetRequiredService<FlowRunService>();
                var secondRun = await flowRuns.GetAsync(outputs.Results[1].FlowRunId!, default);
                Assert.IsNotNull(secondRun);
                Assert.AreEqual(firstFlowRunId, secondRun.Value.ParentFlowRunId);
                Assert.AreEqual(submitted.Interaction.Id.ToString(), secondRun.Value.InteractionId);
                Assert.AreEqual(submitted.Task.Id.ToString(), secondRun.Value.WorkTaskId);
            }
            var continuedInteraction = await client.GetFromJsonAsync<InteractionResponse>($"/api/workspaces/personal/interactions/{submitted.Interaction.Id}");
            Assert.AreEqual(InteractionStatus.Idle, continuedInteraction?.Status);
            Assert.AreEqual(outputs.Results[1].FlowRunId, continuedInteraction?.LastFlowRunId);
            var messages = await client.GetFromJsonAsync<ConversationMessage[]>($"/api/workspaces/personal/interactions/{submitted.Interaction.Id}/messages");
            var storedMessages = messages ?? [];
            Assert.IsTrue(storedMessages.Any(value => value.Content == "Make it shorter and suitable for executives."));
            Assert.IsTrue(storedMessages.Any(value => value.Role == ConversationRole.Agentstration && value.Content.Contains("Executive version is ready", StringComparison.Ordinal)));
            var history = await client.GetFromJsonAsync<InteractionPageResponse>("/api/workspaces/personal/interactions?take=10");
            Assert.IsTrue(history!.Value.Any(value => value.Id == submitted.Interaction.Id));

            var workspaces = await client.GetFromJsonAsync<WorkplaceWorkspaceResponse[]>("/api/workspaces");
            Assert.IsTrue(workspaces?.Any(value => value.Name == "personal"));
            var operationalPage = await client.GetFromJsonAsync<WorkTaskOperationsPageResponse>("/api/tasks?page=1&pageSize=1&sort=updatedAt&direction=desc&status=Completed&search=report");
            Assert.IsNotNull(operationalPage); Assert.HasCount(1, operationalPage.Items); Assert.AreEqual(1, operationalPage.TotalCount);
            Assert.AreEqual(2, operationalPage.Items[0].FlowRunCount); Assert.AreEqual(2, operationalPage.Items[0].ResultCount); Assert.AreEqual(2, operationalPage.Items[0].ArtifactCount);
            var detail = await client.GetFromJsonAsync<WorkTaskOperationsDetailResponse>($"/api/tasks/{submitted.Task.Id}");
            Assert.IsNotNull(detail); Assert.HasCount(2, detail.FlowRuns); Assert.AreEqual(firstFlowRunId, detail.FlowRuns[1].ParentFlowRunId); Assert.HasCount(2, detail.Results); Assert.HasCount(2, detail.Artifacts);
            var supervisedRun = await client.GetFromJsonAsync<Agentstration.Flow.FlowRun>($"/api/tasks/{submitted.Task.Id}/flow-runs/{detail.FlowRuns[0].Id}");
            Assert.AreEqual(submitted.Task.Id.ToString(), supervisedRun?.WorkTaskId);
            using var foreignRunResponse = await client.GetAsync($"/api/tasks/{Guid.NewGuid()}/flow-runs/{detail.FlowRuns[0].Id}");
            Assert.AreEqual(HttpStatusCode.NotFound, foreignRunResponse.StatusCode);
            var artifactJson = await client.GetStringAsync($"/api/tasks/{submitted.Task.Id}/artifacts");
            Assert.IsFalse(artifactJson.Contains("storageKey", StringComparison.OrdinalIgnoreCase));
            var otherWorkspace = await client.GetFromJsonAsync<WorkTaskOperationsPageResponse>("/api/tasks?workspaceId=other&page=1&pageSize=25");
            Assert.AreEqual(0, otherWorkspace?.TotalCount);
            var outOfRange = await client.GetFromJsonAsync<WorkTaskOperationsPageResponse>("/api/tasks?page=999&pageSize=25");
            Assert.HasCount(0, outOfRange!.Items);
            var counters = await client.GetFromJsonAsync<WorkTaskOperationsCountersResponse>("/api/tasks/summary");
            Assert.IsTrue(counters!.CompletedRecently >= 1);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(dataDirectory)) Directory.Delete(dataDirectory, recursive: true);
        }
    }

    [TestMethod]
    public async Task PendingActionUsesSingleUseTokenAndCompletesWithWorkspaceScopedDeliverables()
    {
        var dataDirectory = Path.Combine(Path.GetTempPath(), "agentstration-work-api-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDirectory);
        try
        {
            await using var factory = new WebApplicationFactory<WorkApiProgram>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Testing");
                builder.UseSetting("Data:Directory", dataDirectory);
            });
            using var client = factory.CreateClient();
            var taskCreated = new TaskCompletionSource<TaskCreatedEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
            var taskStatusChanged = new TaskCompletionSource<TaskStatusChangedEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
            var resultAdded = new TaskCompletionSource<TaskResultAddedEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
            var artifactAdded = new TaskCompletionSource<TaskArtifactAddedEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
            var pendingResolved = new TaskCompletionSource<PendingActionResolvedEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var connection = new HubConnectionBuilder()
                .WithUrl("http://localhost/hubs/workplace", options =>
                {
                    options.Transports = HttpTransportType.LongPolling;
                    options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                })
                .Build();
            connection.On<TaskCreatedEvent>("TaskCreated", value => taskCreated.TrySetResult(value));
            connection.On<TaskStatusChangedEvent>("TaskStatusChanged", value => taskStatusChanged.TrySetResult(value));
            connection.On<TaskResultAddedEvent>("TaskResultAdded", value => resultAdded.TrySetResult(value));
            connection.On<TaskArtifactAddedEvent>("TaskArtifactAdded", value => artifactAdded.TrySetResult(value));
            connection.On<PendingActionResolvedEvent>("PendingActionResolved", value => pendingResolved.TrySetResult(value));
            await connection.StartAsync();
            await connection.InvokeAsync("SubscribeAsync", "/resourceGroups/default/providers/Agentstration.Work/workspaces/personal", 0L);

            using var submittedResponse = await client.PostAsJsonAsync(
                "/api/workspaces/personal/entries/guided-request/interactions",
                new CreateInteractionRequest("personal", new Dictionary<string, JsonElement>
                {
                    ["request"] = JsonSerializer.SerializeToElement("Summarize the standalone Workplace increment")
                }));
            Assert.AreEqual(HttpStatusCode.Created, submittedResponse.StatusCode);
            var submitted = await submittedResponse.Content.ReadFromJsonAsync<EntrySubmissionResponse>();
            Assert.IsNotNull(submitted);
            var choice = submitted.Action as RequestChoiceAction;
            Assert.IsNotNull(choice);
            Assert.IsNull(submitted.Task);
            var attentionPage = await client.GetFromJsonAsync<WorkTaskOperationsPageResponse>("/api/tasks?hasPendingAction=true&page=1&pageSize=25");
            Assert.AreEqual(0, attentionPage?.TotalCount, "A PendingAction without a WorkTask is supervised through its Interaction, not invented as a Task.");
            var persistedInteraction = await client.GetFromJsonAsync<InteractionResponse>($"/api/workspaces/personal/interactions/{submitted.Interaction.Id}");
            Assert.IsNull(persistedInteraction?.ImmediateResult, "Resume tokens must only be returned to the initiating client and never persisted with an interaction.");

            using var wrongWorkspace = await client.PostAsJsonAsync(
                $"/api/workspaces/other/interactions/{submitted.Interaction.Id}/pending-actions/{choice.PendingActionId.Value}/responses",
                new PendingActionResponseRequest(choice.ResumeToken, Choice("concise")));
            Assert.AreEqual(HttpStatusCode.NotFound, wrongWorkspace.StatusCode);

            using var invalidToken = await client.PostAsJsonAsync(
                $"/api/workspaces/personal/interactions/{submitted.Interaction.Id}/pending-actions/{choice.PendingActionId.Value}/responses",
                new PendingActionResponseRequest("invalid", Choice("concise")));
            Assert.AreEqual(HttpStatusCode.BadRequest, invalidToken.StatusCode);

            using var invalidChoice = await client.PostAsJsonAsync(
                $"/api/workspaces/personal/interactions/{submitted.Interaction.Id}/pending-actions/{choice.PendingActionId.Value}/responses",
                new PendingActionResponseRequest(choice.ResumeToken, Choice("unsupported")));
            Assert.AreEqual(HttpStatusCode.BadRequest, invalidChoice.StatusCode);

            using var choiceResponse = await client.PostAsJsonAsync(
                $"/api/workspaces/personal/interactions/{submitted.Interaction.Id}/pending-actions/{choice.PendingActionId.Value}/responses",
                new PendingActionResponseRequest(choice.ResumeToken, Choice("concise")));
            choiceResponse.EnsureSuccessStatusCode();
            var choiceResolution = await choiceResponse.Content.ReadFromJsonAsync<PendingActionResolutionResponse>();
            var resolvedTask = choiceResolution?.Task; Assert.IsNotNull(resolvedTask, "A guided one-click choice must resume directly into the Task.");
            var supervisedActions = await client.GetFromJsonAsync<PendingActionContract[]>($"/api/tasks/{resolvedTask.Id}/pending-actions");
            var supervised = supervisedActions ?? []; Assert.HasCount(1, supervised); Assert.AreEqual(resolvedTask.Id, supervised[0].WorkTaskId); Assert.AreEqual(PendingActionStatus.Completed, supervised[0].Status);
            persistedInteraction = await client.GetFromJsonAsync<InteractionResponse>($"/api/workspaces/personal/interactions/{submitted.Interaction.Id}");
            Assert.IsInstanceOfType<CreateTaskAction>(persistedInteraction?.ImmediateResult);

            using var replay = await client.PostAsJsonAsync(
                $"/api/workspaces/personal/interactions/{submitted.Interaction.Id}/pending-actions/{choice.PendingActionId.Value}/responses",
                new PendingActionResponseRequest(choice.ResumeToken, Choice("concise")));
            Assert.AreEqual(HttpStatusCode.Conflict, replay.StatusCode);
            var confirmed = choiceResolution;
            Assert.IsNotNull(confirmed?.Task);
            var createdEvent = await taskCreated.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(confirmed.Task.Id, createdEvent.TaskId);
            Assert.AreEqual(confirmed.Task.Id, (await pendingResolved.Task.WaitAsync(TimeSpan.FromSeconds(5))).TaskId);

            WorkTaskResponse? task = null;
            for (var attempt = 0; attempt < 100; attempt++)
            {
                task = await client.GetFromJsonAsync<WorkTaskResponse>($"/api/workspaces/personal/tasks/{confirmed.Task.Id}");
                if (task?.Status is WorkTaskStatus.Completed or WorkTaskStatus.Failed) break;
                await Task.Delay(25);
            }

            Assert.AreEqual(WorkTaskStatus.Completed, task?.Status);
            var completedTask = task!;
            WorkTaskActivity[] activities = [];
            WorkTaskResult[] results = [];
            WorkTaskArtifact[] artifacts = [];
            for (var attempt = 0; attempt < 50; attempt++)
            {
                activities = await client.GetFromJsonAsync<WorkTaskActivity[]>($"/api/workspaces/personal/tasks/{completedTask.Id}/activities") ?? [];
                results = await client.GetFromJsonAsync<WorkTaskResult[]>($"/api/workspaces/personal/tasks/{completedTask.Id}/results") ?? [];
                artifacts = await client.GetFromJsonAsync<WorkTaskArtifact[]>($"/api/workspaces/personal/tasks/{completedTask.Id}/artifacts") ?? [];
                if (activities.Any(value => value.Type == WorkTaskActivityType.TaskCompleted) && results.Length == 1 && artifacts.Length == 1) break;
                await Task.Delay(20);
            }
            Assert.IsTrue(activities!.Any(value => value.Type == WorkTaskActivityType.TaskCompleted));
            Assert.HasCount(1, results);
            Assert.HasCount(1, artifacts);
            var artifact = artifacts.Single();
            Assert.AreEqual(completedTask.Id, (await taskStatusChanged.Task.WaitAsync(TimeSpan.FromSeconds(5))).TaskId);
            Assert.AreEqual(completedTask.Id, (await resultAdded.Task.WaitAsync(TimeSpan.FromSeconds(5))).Result.WorkTaskId.Value);
            var artifactEvent = await artifactAdded.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(completedTask.Id, artifactEvent.Artifact.WorkTaskId);
            Assert.IsNull(typeof(WorkTaskArtifactEventContract).GetProperty("StorageKey"));

            using var artifactResponse = await client.GetAsync($"/api/workspaces/personal/tasks/{completedTask.Id}/artifacts/{artifact.Id.Value}/content");
            artifactResponse.EnsureSuccessStatusCode();
            Assert.IsFalse(string.IsNullOrWhiteSpace(await artifactResponse.Content.ReadAsStringAsync()));

            using var crossWorkspaceArtifact = await client.GetAsync($"/api/workspaces/other/tasks/{completedTask.Id}/artifacts/{artifact.Id.Value}/content");
            Assert.AreEqual(HttpStatusCode.NotFound, crossWorkspaceArtifact.StatusCode);

            var notifications = await client.GetFromJsonAsync<WorkNotificationPageResponse>("/api/workspaces/personal/notifications?unreadOnly=true");
            Assert.IsTrue(notifications!.Value.Count >= 2);
            var unreadBefore = await client.GetFromJsonAsync<UnreadNotificationCountResponse>("/api/workspaces/personal/notifications/unread-count");
            using var markRead = await client.PostAsync($"/api/workspaces/personal/notifications/{notifications.Value[0].Id.Value}/read", null);
            markRead.EnsureSuccessStatusCode();
            var unreadAfter = await client.GetFromJsonAsync<UnreadNotificationCountResponse>("/api/workspaces/personal/notifications/unread-count");
            Assert.AreEqual(unreadBefore!.Count - 1, unreadAfter!.Count);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(dataDirectory)) Directory.Delete(dataDirectory, recursive: true);
        }
    }

    private static Dictionary<string, JsonElement> Choice(string value) => new()
    {
        ["style"] = JsonSerializer.SerializeToElement(value)
    };

    private static async Task<(WorkTaskResult[] Results, WorkTaskArtifact[] Artifacts)> WaitForOutputsAsync(HttpClient client, Guid taskId, int count)
    {
        WorkTaskResult[] results = [];
        WorkTaskArtifact[] artifacts = [];
        for (var attempt = 0; attempt < 150; attempt++)
        {
            results = await client.GetFromJsonAsync<WorkTaskResult[]>($"/api/workspaces/personal/tasks/{taskId}/results") ?? [];
            artifacts = await client.GetFromJsonAsync<WorkTaskArtifact[]>($"/api/workspaces/personal/tasks/{taskId}/artifacts") ?? [];
            if (results.Length >= count && artifacts.Length >= count) return (results, artifacts);
            await Task.Delay(25);
        }
        return (results, artifacts);
    }
}
