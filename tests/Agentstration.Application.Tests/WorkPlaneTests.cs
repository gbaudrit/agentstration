using System.Net;
using System.Net.Http.Json;
using Agentstration.Application.Work;
using Agentstration.Flow;
using Agentstration.Infrastructure.Artifacts;
using Agentstration.Resources;
using Agentstration.Work;
using Agentstration.Work.Contracts;
using Agentstration.Work.Storage.Abstractions;
using Agentstration.Work.Storage.Sqlite;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Agentstration.Application.Tests;

[TestClass]
public sealed class WorkPlaneTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly WorkspaceId WorkplaceId = new(Guid.Parse("22222222-2222-2222-2222-222222222222"));

    [TestMethod]
    public void WorkItemCreationValidatesRequiredData()
    {
        var item = CreatePending();

        Assert.AreEqual(WorkItemStatus.Pending, item.Status);
        Assert.AreEqual(1, item.Version);
        Assert.AreEqual("WorkItemSubmitted", item.History.Single().Type);
        Assert.Throws<WorkValidationException>(() => WorkItem.Create(WorkItemId.New(), WorkplaceId, "", "instruction", Now));
        Assert.Throws<WorkValidationException>(() => WorkItem.Create(WorkItemId.New(), WorkplaceId, "analysis", "", Now));
        Assert.Throws<WorkValidationException>(() => WorkItem.Create(new WorkItemId(Guid.Empty), WorkplaceId, "analysis", "instruction", Now));
    }

    [TestMethod]
    public void WorkplaceResourcesEnforcePrimaryEntryAndDeterministicFieldRules()
    {
        var entryId = new EntryId("request");
        var duplicatePrimary = new WorkplaceDashboard
        {
            Id = new DashboardId("home"),
            WorkspaceId = WorkplaceId,
            Name = "home",
            DisplayName = "Home",
            Entries =
            [
                new DashboardEntryReference { EntryResourceId = entryId, Role = DashboardItemRole.Primary },
                new DashboardEntryReference { EntryResourceId = new EntryId(entryId.Value + "-two"), Role = DashboardItemRole.Primary }
            ]
        };
        Assert.Throws<WorkValidationException>(() => WorkplaceValidation.Validate(duplicatePrimary));

        var entry = new EntryResource
        {
            WorkspaceId = WorkplaceId,
            Id = entryId,
            Name = "request",
            DisplayName = "Request",
            Presentation = new EntryPresentation
            {
                Kind = EntryPresentationKind.Form,
                Fields =
                [
                    new EntryFieldDefinition { Name = "request", Type = EntryFieldType.Prompt, Required = true, Validation = new EntryFieldValidation(3, 20), Role = EntryFieldRole.PrimaryInput },
                    new EntryFieldDefinition { Name = "detail", Type = EntryFieldType.Choice, Options = [new EntryFieldOption("standard", "Standard")] }
                ]
            },
            ResolvedTarget = new EntryResolvedTarget("router", "1.0.0")
        };
        WorkplaceValidation.Validate(entry);
        Assert.AreEqual(EntryParticipantVisibility.Hidden, entry.Presentation.Participants.Visibility);
        Assert.AreEqual(EntryProgressVisibility.Compact, entry.Presentation.Progress.Visibility);
        Assert.AreEqual(EntryTaskDisplay.Auto, entry.Presentation.Task.Display);
        Assert.AreEqual(EntryResultDisplay.Auto, entry.Presentation.Results.Display);
        Assert.Throws<WorkValidationException>(() => WorkplaceValidation.ValidateSubmission(entry, new Dictionary<string, System.Text.Json.JsonElement>()));
        Assert.Throws<WorkValidationException>(() => WorkplaceValidation.ValidateSubmission(entry, new Dictionary<string, System.Text.Json.JsonElement>
        {
            ["request"] = System.Text.Json.JsonSerializer.SerializeToElement("valid request"),
            ["detail"] = System.Text.Json.JsonSerializer.SerializeToElement("unsupported")
        }));

        var draft = new EntryDraft
        {
            WorkspaceId = WorkplaceId,
            Id = entryId,
            Name = "request",
            DisplayName = "Request",
            Binding = new EntryBinding(EntryBindingKind.Flow, "router"),
            Presentation = entry.Presentation with
            {
                Fields =
                [
                    new EntryFieldDefinition { Name = "request", Type = EntryFieldType.Prompt, Required = true, Role = EntryFieldRole.PrimaryInput },
                    new EntryFieldDefinition { Name = "format", Type = EntryFieldType.Choice, Options = [new("short", "Short"), new("short", "Duplicate")] }
                ]
            }
        };
        var optionError = Assert.Throws<WorkValidationException>(() => WorkplaceValidation.Validate(draft));
        Assert.AreEqual("entry_field_options_invalid", optionError.Code);
        var presentationError = Assert.Throws<WorkValidationException>(() => WorkplaceValidation.Validate(entry with
        {
            Presentation = entry.Presentation with { Task = new((EntryTaskDisplay)99) }
        }));
        Assert.AreEqual("entry_execution_presentation_invalid", presentationError.Code);
        var primaryError = Assert.Throws<WorkValidationException>(() => WorkplaceValidation.Validate(draft with
        {
            Presentation = draft.Presentation with
            {
                Fields = draft.Presentation.Fields.Select(value => value with
                {
                    Role = EntryFieldRole.Standard,
                    Options = value.Type == EntryFieldType.Choice ? [new EntryFieldOption("short", "Short")] : value.Options
                }).ToArray()
            }
        }));
        Assert.AreEqual("entry_primary_input_required", primaryError.Code);
        var legacyBindingError = Assert.Throws<WorkValidationException>(() => WorkplaceValidation.ValidateBinding(
            new EntryBinding(EntryBindingKind.Flow, "legacy/router")));
        Assert.AreEqual("entry_binding_invalid", legacyBindingError.Code);
    }

    [TestMethod]
    public void DashboardValidationAllowsNoPrimaryAndNamespacedEntriesButRejectsDuplicates()
    {
        var packEntry = new EntryId("weather", new ResourceNamespace("daily-life"));
        var dashboard = new WorkplaceDashboard
        {
            Id = new("home"),
            WorkspaceId = WorkplaceId,
            Name = "home",
            DisplayName = "Home",
            IsDefault = true,
            Entries =
            [
                new() { EntryResourceId = packEntry, Role = DashboardItemRole.Featured },
                new() { EntryResourceId = new EntryId("summary"), Role = DashboardItemRole.Standard, Order = 10 }
            ]
        };

        WorkplaceValidation.Validate(dashboard);
        var error = Assert.Throws<WorkValidationException>(() => WorkplaceValidation.Validate(dashboard with
        {
            Entries = [dashboard.Entries[0], dashboard.Entries[0] with { Order = 20 }]
        }));
        Assert.AreEqual("dashboard_entry_duplicate", error.Code);
    }

    [TestMethod]
    public async Task DashboardAdministrationMaintainsOneDefaultAndAllowsEntryReuse()
    {
        await using var fixture = await WorkFixture.CreateAsync();
        var workspaceId = WorkplaceId;
        var entry = new EntryResource
        {
            WorkspaceId = workspaceId,
            Id = new("request"),
            Name = "request",
            DisplayName = "Request",
            Presentation = new EntryPresentation { Fields = [new() { Name = "request", Type = EntryFieldType.Prompt, Required = true, Role = EntryFieldRole.PrimaryInput }] },
            ResolvedTarget = new("router", "1.0.0"),
            PublishedAt = Now
        };
        await fixture.Workplace.UpsertEntryAsync(entry, default);
        var service = new DashboardAdministrationService(fixture.Workplace, TimeProvider.System);
        var home = await service.SaveAsync(new WorkplaceDashboardDraft
        {
            Id = new("home"),
            WorkspaceId = workspaceId,
            Name = "home",
            DisplayName = "Home",
            IsDefault = true,
            Entries = [new() { EntryResourceId = entry.Id, Role = DashboardItemRole.Primary }]
        }, default);
        await service.PublishAsync(workspaceId, home.Id, default);
        var travel = await service.SaveAsync(new WorkplaceDashboardDraft
        {
            Id = new("travel"),
            WorkspaceId = workspaceId,
            Name = "travel",
            DisplayName = "Travel",
            IsDefault = true,
            Entries = [new() { EntryResourceId = entry.Id, Role = DashboardItemRole.Featured }]
        }, default);
        await service.PublishAsync(workspaceId, travel.Id, default);

        var published = await fixture.Workplace.ListDashboardsAsync(workspaceId, default);
        Assert.HasCount(2, published);
        Assert.AreEqual("travel", published.Single(value => value.IsDefault).Name);
        Assert.IsTrue(published.All(value => value.Entries.Single().EntryResourceId == entry.Id));
        var drafts = await fixture.Workplace.ListDashboardDraftsAsync(workspaceId, default);
        Assert.AreEqual("travel", drafts.Single(value => value.IsDefault).Name);

        await service.SaveAsync(travel with { IsDefault = false }, default);
        var error = await Assert.ThrowsAsync<WorkValidationException>(() => service.PublishAsync(workspaceId, travel.Id, default));
        Assert.AreEqual("dashboard_default_required", error.Code);
    }

    [TestMethod]
    public void WorkItemSupportsInputAndApprovalLifecycle()
    {
        var item = CreatePending();
        var executionId = WorkExecutionId.New();
        item.MarkQueued(executionId, null, Guid.NewGuid(), Now.AddSeconds(1));
        Assert.AreEqual(WorkItemStatus.Queued, item.Status);
        item.ApplyRuntimeEvent(new WorkExecutionStarted(Guid.NewGuid(), item.WorkspaceId, item.Id, executionId, Now.AddSeconds(2), "sql-expert"));
        Assert.AreEqual(WorkItemStatus.Running, item.Status);
        item.ApplyRuntimeEvent(new WorkExecutionInputRequested(Guid.NewGuid(), item.WorkspaceId, item.Id, executionId, Now.AddSeconds(3), "Which database?"));
        Assert.AreEqual(WorkItemStatus.WaitingForInput, item.Status);
        item.ProvideInput(new WorkInput("SQL Server"), "requester-1", Guid.NewGuid(), Now.AddSeconds(4));
        Assert.AreEqual(WorkItemStatus.Running, item.Status);
        item.ApplyRuntimeEvent(new WorkExecutionApprovalRequested(Guid.NewGuid(), item.WorkspaceId, item.Id, executionId, Now.AddSeconds(5), "Apply the recommendation?"));
        Assert.AreEqual(WorkItemStatus.WaitingForApproval, item.Status);
        item.SubmitApproval(WorkApprovalDecision.Approved, "requester-1", "Approved", Guid.NewGuid(), Now.AddSeconds(6));
        Assert.AreEqual(WorkItemStatus.Running, item.Status);
        item.ApplyRuntimeEvent(new WorkExecutionCompleted(Guid.NewGuid(), item.WorkspaceId, item.Id, executionId, Now.AddSeconds(7), Result("Done")));
        Assert.AreEqual(WorkItemStatus.Completed, item.Status);
        Assert.AreEqual("Done", item.Result!.Contents.Single().Text);
        Assert.Throws<WorkTransitionException>(() => item.Cancel(null, Guid.NewGuid(), Now.AddSeconds(8)));
    }

    [TestMethod]
    public void WorkItemRejectsInvalidTransitionsAndTerminalMutations()
    {
        var pending = CreatePending();
        Assert.Throws<WorkTransitionException>(() => pending.ProvideInput(new WorkInput("unexpected"), null, Guid.NewGuid(), Now));
        Assert.Throws<WorkTransitionException>(() => pending.SubmitApproval(WorkApprovalDecision.Approved, null, null, Guid.NewGuid(), Now));

        pending.Cancel(null, Guid.NewGuid(), Now.AddSeconds(1));
        Assert.AreEqual(WorkItemStatus.Cancelled, pending.Status);
        Assert.Throws<WorkTransitionException>(() => pending.MarkQueued(WorkExecutionId.New(), null, Guid.NewGuid(), Now.AddSeconds(2)));

        var rejected = Running();
        rejected.ApplyRuntimeEvent(new WorkExecutionApprovalRequested(Guid.NewGuid(), rejected.WorkspaceId, rejected.Id, rejected.CurrentExecutionId!.Value, Now.AddSeconds(3), "Approve?"));
        rejected.SubmitApproval(WorkApprovalDecision.Rejected, "requester", "No", Guid.NewGuid(), Now.AddSeconds(4));
        Assert.AreEqual(WorkItemStatus.Failed, rejected.Status);
        Assert.AreEqual("approval_rejected", rejected.Error!.Code);

        var failed = Running();
        failed.ApplyRuntimeEvent(new WorkExecutionFailed(Guid.NewGuid(), failed.WorkspaceId, failed.Id, failed.CurrentExecutionId!.Value, Now.AddSeconds(3),
            new WorkError("model_timeout", "Timed out", WorkErrorCategory.Timeout, true, Now.AddSeconds(3), failed.CurrentExecutionId)));
        Assert.AreEqual(WorkItemStatus.Failed, failed.Status);
        Assert.Throws<WorkTransitionException>(() => failed.Cancel(null, Guid.NewGuid(), Now.AddSeconds(4)));
    }

    [TestMethod]
    public void DuplicateRuntimeEventIsIdempotent()
    {
        var item = Running();
        var completed = new WorkExecutionCompleted(Guid.NewGuid(), item.WorkspaceId, item.Id, item.CurrentExecutionId!.Value, Now.AddSeconds(3), Result("Once"));
        Assert.IsTrue(item.ApplyRuntimeEvent(completed));
        var version = item.Version;
        Assert.IsFalse(item.ApplyRuntimeEvent(completed));
        Assert.AreEqual(version, item.Version);
        Assert.AreEqual(1, item.History.Count(value => value.EventId == completed.EventId));
    }

    [TestMethod]
    public void PauseAndResumeAreIdempotentAndPreserveExecutionCorrelation()
    {
        var item = Running();
        var executionId = item.CurrentExecutionId;

        Assert.IsTrue(item.Pause(Guid.NewGuid(), Now.AddSeconds(3)));
        Assert.AreEqual(WorkItemStatus.Paused, item.Status);
        Assert.IsFalse(item.Pause(Guid.NewGuid(), Now.AddSeconds(4)));
        Assert.AreEqual(executionId, item.CurrentExecutionId);

        Assert.IsTrue(item.Resume(Guid.NewGuid(), Now.AddSeconds(5)));
        Assert.AreEqual(WorkItemStatus.Running, item.Status);
        Assert.IsFalse(item.Resume(Guid.NewGuid(), Now.AddSeconds(6)));
        Assert.AreEqual(executionId, item.CurrentExecutionId);
        CollectionAssert.IsSubsetOf(new[] { "WorkItemPaused", "WorkItemResumed" }, item.History.Select(value => value.Type).ToArray());
    }

    [TestMethod]
    public async Task ApplicationSubmissionPersistsDelegatesAndAppliesRuntimeResult()
    {
        await using var fixture = await WorkFixture.CreateAsync();
        var service = fixture.Service;
        var created = await service.SubmitAsync(new SubmitWorkItemCommand(WorkplaceId, "analysis", "Analyze this", RequesterIdentity: "requester-1"), default);

        Assert.AreEqual(WorkItemStatus.Queued, created.Value.Status);
        Assert.AreEqual(created.Value.Id, fixture.Gateway.Request!.WorkItemId);
        Assert.AreEqual(TestWorkExecutionScopeAccessor.Scope, fixture.Gateway.Request.ExecutionScope);
        Assert.IsTrue(fixture.Gateway.Confirmed);
        var executionId = created.Value.CurrentExecutionId!.Value;
        await service.ApplyExecutionEventAsync(new WorkExecutionStarted(Guid.NewGuid(), WorkplaceId, created.Value.Id, executionId, Now, "dotnet-expert"), default);
        var completedEvent = new WorkExecutionCompleted(Guid.NewGuid(), WorkplaceId, created.Value.Id, executionId, Now.AddSeconds(1), Result("Analysis complete"));
        var completed = await service.ApplyExecutionEventAsync(completedEvent, default);
        var duplicate = await service.ApplyExecutionEventAsync(completedEvent, default);

        Assert.AreEqual(WorkItemStatus.Completed, completed.Value.Status);
        Assert.AreEqual("Analysis complete", completed.Value.Result!.Contents.Single().Text);
        Assert.AreEqual(completed.Value.Version, duplicate.Value.Version);
        Assert.AreEqual(WorkItemStatus.Completed, (await fixture.Repository.GetAsync(WorkplaceId, created.Value.Id, default))!.Value.Status);
    }

    [TestMethod]
    public async Task SqliteStorageSupportsFilteringPaginationAndOptimisticConcurrency()
    {
        await using var fixture = await WorkFixture.CreateAsync();
        var first = CreatePending("analysis", "requester-a");
        var second = CreatePending("question", "requester-b");
        await fixture.Repository.CreateAsync(first, default);
        await fixture.Repository.CreateAsync(second, default);

        var page = await fixture.Repository.QueryAsync(new WorkItemQuery(WorkplaceId, Take: 1, Type: "analysis"), default);
        Assert.AreEqual(1, page.Items.Count);
        Assert.AreEqual("analysis", page.Items.Single().Value.Type);

        var copyA = (await fixture.Repository.GetAsync(WorkplaceId, first.Id, default))!.Value;
        var copyB = (await fixture.Repository.GetAsync(WorkplaceId, first.Id, default))!.Value;
        copyA.AddMessage("first update", "a", Guid.NewGuid(), Now.AddMinutes(1));
        await fixture.Repository.SaveAsync(copyA, 1, default);
        copyB.AddMessage("stale update", "b", Guid.NewGuid(), Now.AddMinutes(2));
        await Assert.ThrowsAsync<WorkItemConcurrencyException>(() => fixture.Repository.SaveAsync(copyB, 1, default));
    }

    [TestMethod]
    public async Task SqliteStorageAllowsTheSameWorkItemIdInDifferentWorkspaces()
    {
        await using var fixture = await WorkFixture.CreateAsync();
        var id = WorkItemId.New();
        var otherWorkspaceId = new WorkspaceId(Guid.NewGuid());
        await fixture.Repository.CreateAsync(WorkItem.Create(id, WorkplaceId, "analysis", "First", Now), default);
        await fixture.Repository.CreateAsync(WorkItem.Create(id, otherWorkspaceId, "question", "Second", Now), default);

        Assert.AreEqual("analysis", (await fixture.Repository.GetAsync(WorkplaceId, id, default))?.Value.Type);
        Assert.AreEqual("question", (await fixture.Repository.GetAsync(otherWorkspaceId, id, default))?.Value.Type);
        Assert.HasCount(1, (await fixture.Repository.QueryAsync(new WorkItemQuery(WorkplaceId), default)).Items);
        Assert.HasCount(1, (await fixture.Repository.QueryAsync(new WorkItemQuery(otherWorkspaceId), default)).Items);
    }

    [TestMethod]
    public async Task FileSystemArtifactsCannotBeReadFromAnotherWorkspace()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"agentstration-artifacts-{Guid.NewGuid():N}");
        try
        {
            var store = new FileSystemArtifactStore(directory);
            await using var content = new MemoryStream("workspace-owned"u8.ToArray());
            var reference = await store.SaveAsync(WorkplaceId, new ArtifactContent("result.txt", "text/plain", content), default);

            await using var readable = await store.OpenReadAsync(WorkplaceId, reference, default);
            using var reader = new StreamReader(readable);
            Assert.AreEqual("workspace-owned", await reader.ReadToEndAsync(default));
            await Assert.ThrowsAsync<DirectoryNotFoundException>(() =>
                store.OpenReadAsync(new WorkspaceId(Guid.NewGuid()), reference, default));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task SqliteWorkplaceStorageIsolatesHomonymousEntriesByNamespace()
    {
        await using var fixture = await WorkFixture.CreateAsync();
        var firstNamespace = new ResourceNamespace("team-a");
        var secondNamespace = new ResourceNamespace("team-b");
        const string entryName = "request";
        var first = Entry(new EntryId(entryName, firstNamespace));
        var second = Entry(new EntryId(entryName, secondNamespace));

        await fixture.Workplace.UpsertEntryDraftAsync(first, default);
        await fixture.Workplace.UpsertEntryDraftAsync(second, default);

        Assert.AreEqual(firstNamespace, (await fixture.Workplace.GetEntryDraftAsync(WorkplaceId, first.Id, default))?.Id.Namespace);
        Assert.AreEqual(secondNamespace, (await fixture.Workplace.GetEntryDraftAsync(WorkplaceId, second.Id, default))?.Id.Namespace);
        Assert.IsNull(await fixture.Workplace.GetEntryDraftAsync(WorkplaceId, new EntryId(entryName), default));
        await fixture.Workplace.DeleteEntryDraftAsync(WorkplaceId, first.Id, default);
        Assert.IsNull(await fixture.Workplace.GetEntryDraftAsync(WorkplaceId, first.Id, default));
        Assert.IsNotNull(await fixture.Workplace.GetEntryDraftAsync(WorkplaceId, second.Id, default));
    }

    [TestMethod]
    public async Task WorkApiValidatesCreatesGetsAndReportsUnavailableResult()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IWorkExecutionGateway>();
                services.AddSingleton<IWorkExecutionGateway, PausedGateway>();
            });
        });
        using var client = factory.CreateClient();
        var routes = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Select(endpoint => endpoint.RoutePattern.RawText)
            .Where(route => route is not null)
            .ToArray();
        CollectionAssert.IsSubsetOf(new[]
        {
            "/api/work/workitems/",
            "/api/work/workitems/{workItemId:guid}",
            "/api/work/workitems/{workItemId:guid}/cancel",
            "/api/work/workitems/{workItemId:guid}/messages",
            "/api/work/workitems/{workItemId:guid}/input",
            "/api/work/workitems/{workItemId:guid}/approval",
            "/api/work/workitems/{workItemId:guid}/events",
            "/api/work/workitems/{workItemId:guid}/result"
        }, routes!);
        using var invalid = await client.PostAsJsonAsync("/api/work/workitems", new CreateWorkItemRequest("", ""));
        Assert.AreEqual(HttpStatusCode.BadRequest, invalid.StatusCode);

        using var createdResponse = await client.PostAsJsonAsync("/api/work/workitems", new CreateWorkItemRequest("analysis", "Analyze this", "Test work", RequesterIdentity: "requester-1"));
        Assert.AreEqual(HttpStatusCode.Created, createdResponse.StatusCode);
        Assert.IsNotNull(createdResponse.Headers.ETag);
        var created = await createdResponse.Content.ReadFromJsonAsync<WorkItemResponse>();
        Assert.IsNotNull(created);
        Assert.AreEqual(WorkItemStatus.Queued, created.Status);

        using var get = await client.GetAsync($"/api/work/workitems/{created.Id}");
        Assert.AreEqual(HttpStatusCode.OK, get.StatusCode);
        using var result = await client.GetAsync($"/api/work/workitems/{created.Id}/result");
        Assert.AreEqual(HttpStatusCode.Conflict, result.StatusCode);
        using var missing = await client.GetAsync($"/api/work/workitems/{Guid.NewGuid()}");
        Assert.AreEqual(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [TestMethod]
    public async Task LocalRuntimeCompletesWorkAndExposesResultThroughCanonicalApi()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.UseEnvironment("Testing"));
        using var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/work/workitems", new CreateWorkItemRequest("question", "How can I optimize a SQL query?"));
        response.EnsureSuccessStatusCode();
        var created = await response.Content.ReadFromJsonAsync<WorkItemResponse>();
        Assert.IsNotNull(created);

        WorkItemResponse? current = null;
        for (var attempt = 0; attempt < 50; attempt++)
        {
            current = await client.GetFromJsonAsync<WorkItemResponse>($"/api/work/workitems/{created.Id}");
            if (current?.Status is WorkItemStatus.Completed or WorkItemStatus.Failed) break;
            await Task.Delay(20);
        }

        Assert.AreEqual(WorkItemStatus.Completed, current!.Status);
        var result = await client.GetFromJsonAsync<WorkResultResponse>($"/api/work/workitems/{created.Id}/result");
        Assert.IsNotNull(result);
        Assert.IsFalse(string.IsNullOrWhiteSpace(result.Contents.Single().Text));
        var events = await client.GetFromJsonAsync<WorkEventResponse[]>($"/api/work/workitems/{created.Id}/events");
        CollectionAssert.IsSubsetOf(new[] { "WorkItemSubmitted", "WorkItemQueued", "WorkItemStarted", "WorkItemCompleted" }, events!.Select(value => value.Type).ToArray());
    }

    [TestMethod]
    public async Task WorkplaceApiRunsPrimaryEntryThroughFlowAndReturnsWorkspaceScopedTask()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.UseEnvironment("Testing"));
        using var client = factory.CreateClient();
        var workspaces = await client.GetFromJsonAsync<WorkplaceWorkspaceResponse[]>("/api/workplace/workspaces");
        var workspace = workspaces!.Single(value => value.Name == "personal");
        var workspaceRoute = workspace.Id.ToString("D");
        var dashboard = await client.GetFromJsonAsync<WorkplaceDashboardResponse>($"/api/workspaces/{workspaceRoute}/dashboard");
        Assert.AreEqual(DashboardItemRole.Primary, dashboard!.Entries.Single(value => value.Role == DashboardItemRole.Primary).Role);

        using var submittedResponse = await client.PostAsJsonAsync($"/api/workspaces/{workspaceRoute}/entries/universal-request/interactions", new CreateInteractionRequest(
            new Dictionary<string, System.Text.Json.JsonElement> { ["request"] = System.Text.Json.JsonSerializer.SerializeToElement("Explain dependency injection in .NET") }));
        Assert.AreEqual(HttpStatusCode.Created, submittedResponse.StatusCode);
        var submitted = await submittedResponse.Content.ReadFromJsonAsync<EntrySubmissionResponse>();
        Assert.IsNotNull(submitted?.Task);
        Assert.IsInstanceOfType<CreateTaskAction>(submitted.Action);

        WorkTaskResponse? task = null;
        for (var attempt = 0; attempt < 100; attempt++)
        {
            task = await client.GetFromJsonAsync<WorkTaskResponse>($"/api/workspaces/{workspaceRoute}/tasks/{submitted.Task.Id}");
            if (task?.Status is WorkTaskStatus.Completed or WorkTaskStatus.Failed) break;
            await Task.Delay(25);
        }
        Assert.AreEqual(WorkTaskStatus.Completed, task!.Status);
        Assert.IsFalse(string.IsNullOrWhiteSpace(task.FlowRunId));
        Assert.IsNotNull(task.Result);

        using var wrongWorkspace = await client.GetAsync($"/api/workspaces/other/tasks/{task.Id}");
        Assert.AreEqual(HttpStatusCode.NotFound, wrongWorkspace.StatusCode);
    }

    private static WorkItem CreatePending(string type = "analysis", string? requester = "requester-1") =>
        WorkItem.Create(WorkItemId.New(), WorkplaceId, type, "Perform the requested work", Now, requesterIdentity: requester);

    private static WorkItem Running()
    {
        var item = CreatePending();
        var executionId = WorkExecutionId.New();
        item.MarkQueued(executionId, null, Guid.NewGuid(), Now.AddSeconds(1));
        item.ApplyRuntimeEvent(new WorkExecutionStarted(Guid.NewGuid(), item.WorkspaceId, item.Id, executionId, Now.AddSeconds(2), "agent-1"));
        return item;
    }

    private static WorkResult Result(string text) => new([new WorkResultContent(text)], [], new Dictionary<string, string>(), Now);

    private sealed class FakeGateway : IWorkExecutionGateway
    {
        public WorkExecutionRequest? Request { get; private set; }
        public bool Confirmed { get; private set; }
        public Task<WorkExecutionAccepted> RequestExecutionAsync(WorkExecutionRequest request, CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult(new WorkExecutionAccepted(WorkExecutionId.New(), request.RequestedAgentId, Now, Guid.NewGuid()));
        }
        public Task ConfirmQueuedAsync(WorkExecutionAccepted accepted, CancellationToken cancellationToken)
        {
            Confirmed = true;
            return Task.CompletedTask;
        }
    }

    private sealed class PausedGateway : IWorkExecutionGateway
    {
        public Task<WorkExecutionAccepted> RequestExecutionAsync(WorkExecutionRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new WorkExecutionAccepted(WorkExecutionId.New(), request.RequestedAgentId, Now, Guid.NewGuid()));
        public Task ConfirmQueuedAsync(WorkExecutionAccepted accepted, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    [TestMethod]
    public async Task FlowBackedWorkIsRejectedBeforeQueueingWithoutAnExecutionScope()
    {
        await using var fixture = await WorkFixture.CreateAsync(withExecutionScope: false);

        var exception = await Assert.ThrowsExactlyAsync<WorkValidationException>(() => fixture.Service.SubmitAsync(
            new SubmitWorkItemCommand(WorkplaceId, "flow", "Run", Flow: new FlowReference(new FlowId("main"))), default));

        Assert.AreEqual("work_execution_scope_required", exception.Code);
        Assert.IsNull(fixture.Gateway.Request);
    }

    private sealed class TestWorkExecutionScopeAccessor : IWorkExecutionScopeAccessor
    {
        public static FlowRunScope Scope { get; } = new(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            new(Guid.Parse("22222222-2222-2222-2222-222222222222")),
            Guid.Parse("33333333-3333-3333-3333-333333333333"));

        public FlowRunScope Current => Scope;
    }

    private static EntryDraft Entry(EntryId id) => new()
    {
        WorkspaceId = WorkplaceId,
        Id = id,
        Name = id.Value,
        DisplayName = id.Value,
        Binding = new EntryBinding(EntryBindingKind.Flow, "router"),
        Presentation = new EntryPresentation
        {
            Fields = [new EntryFieldDefinition { Name = "request", Type = EntryFieldType.Prompt, Required = true, Role = EntryFieldRole.PrimaryInput }]
        }
    };

    private sealed class WorkFixture : IAsyncDisposable
    {
        private readonly string _directory;
        private readonly ServiceProvider _provider;
        public WorkItemService Service => _provider.GetRequiredService<WorkItemService>();
        public IWorkItemRepository Repository => _provider.GetRequiredService<IWorkItemRepository>();
        public IWorkplaceRepository Workplace => _provider.GetRequiredService<IWorkplaceRepository>();
        public FakeGateway Gateway => _provider.GetRequiredService<FakeGateway>();

        private WorkFixture(string directory, ServiceProvider provider) { _directory = directory; _provider = provider; }

        public static async Task<WorkFixture> CreateAsync(bool withExecutionScope = true)
        {
            var directory = Path.Combine(Path.GetTempPath(), $"agentstration-work-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton(TimeProvider.System);
            services.AddSqliteWorkPlane($"Data Source={Path.Combine(directory, "work.db")};Pooling=False");
            services.AddSingleton<FakeGateway>();
            services.AddSingleton<IWorkExecutionGateway>(provider => provider.GetRequiredService<FakeGateway>());
            if (withExecutionScope) services.AddSingleton<IWorkExecutionScopeAccessor, TestWorkExecutionScopeAccessor>();
            services.AddSingleton<WorkItemService>();
            var provider = services.BuildServiceProvider();
            var fixture = new WorkFixture(directory, provider);
            await fixture.Service.InitializeAsync(default);
            return fixture;
        }

        public async ValueTask DisposeAsync()
        {
            await _provider.DisposeAsync();
            if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        }
    }
}
