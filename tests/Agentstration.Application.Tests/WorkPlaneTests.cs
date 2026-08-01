using System.Net;
using System.Net.Http.Json;
using Agentstration.Application.Work;
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

    [TestMethod]
    public void WorkItemCreationValidatesRequiredData()
    {
        var item = CreatePending();

        Assert.AreEqual(WorkItemStatus.Pending, item.Status);
        Assert.AreEqual(1, item.Version);
        Assert.AreEqual("WorkItemSubmitted", item.History.Single().Type);
        Assert.Throws<WorkValidationException>(() => WorkItem.Create(WorkItemId.New(), "", "instruction", Now));
        Assert.Throws<WorkValidationException>(() => WorkItem.Create(WorkItemId.New(), "analysis", "", Now));
        Assert.Throws<WorkValidationException>(() => WorkItem.Create(new WorkItemId(Guid.Empty), "analysis", "instruction", Now));
    }

    [TestMethod]
    public void WorkItemSupportsInputAndApprovalLifecycle()
    {
        var item = CreatePending();
        var executionId = WorkExecutionId.New();
        item.MarkQueued(executionId, null, Guid.NewGuid(), Now.AddSeconds(1));
        Assert.AreEqual(WorkItemStatus.Queued, item.Status);
        item.ApplyRuntimeEvent(new WorkExecutionStarted(Guid.NewGuid(), item.Id, executionId, Now.AddSeconds(2), "sql-expert"));
        Assert.AreEqual(WorkItemStatus.Running, item.Status);
        item.ApplyRuntimeEvent(new WorkExecutionInputRequested(Guid.NewGuid(), item.Id, executionId, Now.AddSeconds(3), "Which database?"));
        Assert.AreEqual(WorkItemStatus.WaitingForInput, item.Status);
        item.ProvideInput(new WorkInput("SQL Server"), "requester-1", Guid.NewGuid(), Now.AddSeconds(4));
        Assert.AreEqual(WorkItemStatus.Running, item.Status);
        item.ApplyRuntimeEvent(new WorkExecutionApprovalRequested(Guid.NewGuid(), item.Id, executionId, Now.AddSeconds(5), "Apply the recommendation?"));
        Assert.AreEqual(WorkItemStatus.WaitingForApproval, item.Status);
        item.SubmitApproval(WorkApprovalDecision.Approved, "requester-1", "Approved", Guid.NewGuid(), Now.AddSeconds(6));
        Assert.AreEqual(WorkItemStatus.Running, item.Status);
        item.ApplyRuntimeEvent(new WorkExecutionCompleted(Guid.NewGuid(), item.Id, executionId, Now.AddSeconds(7), Result("Done")));
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
        rejected.ApplyRuntimeEvent(new WorkExecutionApprovalRequested(Guid.NewGuid(), rejected.Id, rejected.CurrentExecutionId!.Value, Now.AddSeconds(3), "Approve?"));
        rejected.SubmitApproval(WorkApprovalDecision.Rejected, "requester", "No", Guid.NewGuid(), Now.AddSeconds(4));
        Assert.AreEqual(WorkItemStatus.Failed, rejected.Status);
        Assert.AreEqual("approval_rejected", rejected.Error!.Code);

        var failed = Running();
        failed.ApplyRuntimeEvent(new WorkExecutionFailed(Guid.NewGuid(), failed.Id, failed.CurrentExecutionId!.Value, Now.AddSeconds(3),
            new WorkError("model_timeout", "Timed out", WorkErrorCategory.Timeout, true, Now.AddSeconds(3), failed.CurrentExecutionId)));
        Assert.AreEqual(WorkItemStatus.Failed, failed.Status);
        Assert.Throws<WorkTransitionException>(() => failed.Cancel(null, Guid.NewGuid(), Now.AddSeconds(4)));
    }

    [TestMethod]
    public void DuplicateRuntimeEventIsIdempotent()
    {
        var item = Running();
        var completed = new WorkExecutionCompleted(Guid.NewGuid(), item.Id, item.CurrentExecutionId!.Value, Now.AddSeconds(3), Result("Once"));
        Assert.IsTrue(item.ApplyRuntimeEvent(completed));
        var version = item.Version;
        Assert.IsFalse(item.ApplyRuntimeEvent(completed));
        Assert.AreEqual(version, item.Version);
        Assert.AreEqual(1, item.History.Count(value => value.EventId == completed.EventId));
    }

    [TestMethod]
    public async Task ApplicationSubmissionPersistsDelegatesAndAppliesRuntimeResult()
    {
        await using var fixture = await WorkFixture.CreateAsync();
        var service = fixture.Service;
        var created = await service.SubmitAsync(new SubmitWorkItemCommand("analysis", "Analyze this", RequesterIdentity: "requester-1"), default);

        Assert.AreEqual(WorkItemStatus.Queued, created.Value.Status);
        Assert.AreEqual(created.Value.Id, fixture.Gateway.Request!.WorkItemId);
        Assert.IsTrue(fixture.Gateway.Confirmed);
        var executionId = created.Value.CurrentExecutionId!.Value;
        await service.ApplyExecutionEventAsync(new WorkExecutionStarted(Guid.NewGuid(), created.Value.Id, executionId, Now, "dotnet-expert"), default);
        var completedEvent = new WorkExecutionCompleted(Guid.NewGuid(), created.Value.Id, executionId, Now.AddSeconds(1), Result("Analysis complete"));
        var completed = await service.ApplyExecutionEventAsync(completedEvent, default);
        var duplicate = await service.ApplyExecutionEventAsync(completedEvent, default);

        Assert.AreEqual(WorkItemStatus.Completed, completed.Value.Status);
        Assert.AreEqual("Analysis complete", completed.Value.Result!.Contents.Single().Text);
        Assert.AreEqual(completed.Value.Version, duplicate.Value.Version);
        Assert.AreEqual(WorkItemStatus.Completed, (await fixture.Repository.GetAsync(created.Value.Id, default))!.Value.Status);
    }

    [TestMethod]
    public async Task SqliteStorageSupportsFilteringPaginationAndOptimisticConcurrency()
    {
        await using var fixture = await WorkFixture.CreateAsync();
        var first = CreatePending("analysis", "requester-a");
        var second = CreatePending("question", "requester-b");
        await fixture.Repository.CreateAsync(first, default);
        await fixture.Repository.CreateAsync(second, default);

        var page = await fixture.Repository.QueryAsync(new WorkItemQuery(Take: 1, Type: "analysis"), default);
        Assert.AreEqual(1, page.Items.Count);
        Assert.AreEqual("analysis", page.Items.Single().Value.Type);

        var copyA = (await fixture.Repository.GetAsync(first.Id, default))!.Value;
        var copyB = (await fixture.Repository.GetAsync(first.Id, default))!.Value;
        copyA.AddMessage("first update", "a", Guid.NewGuid(), Now.AddMinutes(1));
        await fixture.Repository.SaveAsync(copyA, 1, default);
        copyB.AddMessage("stale update", "b", Guid.NewGuid(), Now.AddMinutes(2));
        await Assert.ThrowsAsync<WorkItemConcurrencyException>(() => fixture.Repository.SaveAsync(copyB, 1, default));
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

    private static WorkItem CreatePending(string type = "analysis", string? requester = "requester-1") =>
        WorkItem.Create(WorkItemId.New(), type, "Perform the requested work", Now, requesterIdentity: requester);

    private static WorkItem Running()
    {
        var item = CreatePending();
        var executionId = WorkExecutionId.New();
        item.MarkQueued(executionId, null, Guid.NewGuid(), Now.AddSeconds(1));
        item.ApplyRuntimeEvent(new WorkExecutionStarted(Guid.NewGuid(), item.Id, executionId, Now.AddSeconds(2), "agent-1"));
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

    private sealed class WorkFixture : IAsyncDisposable
    {
        private readonly string _directory;
        private readonly ServiceProvider _provider;
        public WorkItemService Service => _provider.GetRequiredService<WorkItemService>();
        public IWorkItemRepository Repository => _provider.GetRequiredService<IWorkItemRepository>();
        public FakeGateway Gateway => _provider.GetRequiredService<FakeGateway>();

        private WorkFixture(string directory, ServiceProvider provider) { _directory = directory; _provider = provider; }

        public static async Task<WorkFixture> CreateAsync()
        {
            var directory = Path.Combine(Path.GetTempPath(), $"agentstration-work-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton(TimeProvider.System);
            services.AddSqliteWorkPlane($"Data Source={Path.Combine(directory, "work.db")};Pooling=False");
            services.AddSingleton<FakeGateway>();
            services.AddSingleton<IWorkExecutionGateway>(provider => provider.GetRequiredService<FakeGateway>());
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
