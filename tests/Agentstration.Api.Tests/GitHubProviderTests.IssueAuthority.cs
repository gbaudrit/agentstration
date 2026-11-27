extern alias GitHubExtension;

using Agentstration.Coding;
using Agentstration.Application.Work;
using Agentstration.Coding.Contracts;
using Agentstration.Resources;
using System.Net;
using System.Net.Http.Json;
using Agentstration.Work.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.Work;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using GitHub = GitHubExtension::Agentstration.Extensions.GitHub;

namespace Agentstration.Web.Tests;

public sealed partial class GitHubProviderTests
{
    [TestMethod]
    public async Task GitHubCanonicalIssueIntakeAndReadinessApiUseProtectedNativeAuthorityAndSharedPlanReceipts()
    {
        using var fixture = new Fixture(); fixture.Native.AddIssue("Upstream"); fixture.Native.AddIssue("Downstream"); fixture.Native.Issues[2].Dependencies.Add(1);
        await using var extension = new WebApplicationFactory<GitHubExtension::Program>().WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.Configure<GitHub.GitHubProviderOptions>(options => options.StateDirectory = Path.Combine(fixture.Directory, "provider"));
            services.AddHttpClient<GitHub.GitHubConnection>().ConfigurePrimaryHttpMessageHandler(() => fixture.Native);
        }));
        _ = extension.CreateClient();
        var remote = new IssueSharingFixture();
        await using var host = CodingFactory(Path.Combine(fixture.Directory, "coordination-host"), extension).WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddSingleton<IWorkProjectSharingGateway>(remote)));
        using var client = host.CreateClient();
        var context = await host.Services.GetRequiredService<ILocalEnvironmentBootstrapper>().EnsureInitializedAsync(default);
        WorkProjectSnapshot project; WorkPlanView upstream; WorkPlanView downstream;
        using (host.Services.GetRequiredService<IRequestContextScopeFactory>().Push(context))
        {
            var tools = await InstallGitHubRelaysAsync(host.Services, context);
            var selected = await InstallIssueServiceAsync(host.Services, context, extension);
            var taskService = host.Services.GetRequiredService<WorkTaskTreeService>(); var planService = host.Services.GetRequiredService<WorkPlanService>();
            async Task<WorkPlanView> PlanAsync(string title)
            {
                var taskId = new WorkTaskId(Guid.NewGuid());
                Assert.IsTrue((await taskService.CreateAsync(new(context.WorkspaceId), new(taskId, [new(taskId, WorkTaskKind.Atomic, title, "Produce fixture evidence")]), default)).IsSuccess);
                var created = await planService.CreateAsync(new(context.WorkspaceId), WorkPlanId.New(), new(Guid.NewGuid(), 0, new(title, [taskId], [])), default);
                Assert.IsTrue(created.IsSuccess, created.ErrorCode); return created.Value!;
            }
            upstream = await PlanAsync("Upstream"); downstream = await PlanAsync("Downstream");
            var definition = new WorkProjectDefinition("Issue API", null, [upstream.Plan.Id, downstream.Plan.Id], [])
            {
                Bindings = [new("sharing", WorkProjectBindingPurpose.Sharing, Pin(tools.Tracker))],
                ConnectionRequirements = [selected.Requirement],
                ConnectionAssignments = [selected.Assignment],
                Sharing = new(new(Guid.NewGuid()), new(Guid.NewGuid()), "sharing", "fixture")
            };
            var created = await host.Services.GetRequiredService<WorkProjectService>().CreateAsync(new(context.WorkspaceId), WorkProjectId.New(), "issue-api", default, new(Guid.NewGuid(), 0, definition), default);
            Assert.IsTrue(created.IsSuccess, created.ErrorCode); project = created.Value!;
            var metadata = new WorkSharingProjectMetadata("Issue API", null, [project.Definition.Sharing!.ParticipantId]) { ConnectionRequirements = project.Definition.ConnectionRequirements };
            var publication = await host.Services.GetRequiredService<WorkProjectSharingService>().PublishAsync(new(context.WorkspaceId), project.Id,
                new(new(Guid.NewGuid()), project.Version, [new(WorkSharingProjectMetadata.Key, 0, System.Text.Json.JsonSerializer.SerializeToElement(metadata, Json))]), default);
            Assert.AreEqual(WorkSharingOperationStatus.Succeeded, publication.Value!.Status, publication.ErrorCode ?? publication.Value.ErrorCode);
        }
        var prefix = $"/api/workspaces/{context.WorkspaceId}/projects/{project.Id}/coding";
        AcknowledgeCodingIssueRequest Command(WorkPlanView plan, string number) => new(Guid.NewGuid(), project.Version, plan.Plan.Id.Value,
            Repository, number, "github", "agentstration:plan:", "agentstration:instance:", 0,
            plan.Revision.Definition.TaskIds.Select(task => new WorkTaskAllocation(task, project.Definition.Sharing!.ParticipantId)).ToArray())
        { TrackingConnection = "issues" };
        foreach (var (plan, number) in new[] { (upstream, "1"), (downstream, "2") })
        {
            using var response = await client.PostAsJsonAsync(prefix + "/issues/acknowledge", Command(plan, number));
            Assert.AreEqual(HttpStatusCode.Accepted, response.StatusCode, await response.Content.ReadAsStringAsync());
            Assert.AreEqual(CodingIssueIntakeStatus.Confirmed, (await response.Content.ReadFromJsonAsync<CodingIssueIntakeResponse>())!.Status);
        }
        var readinessRoute = prefix + $"/plans/{downstream.Plan.Id}/issue-readiness";
        var blocked = (await client.GetFromJsonAsync<CodingIssueReadiness>(readinessRoute))!;
        Assert.IsFalse(blocked.Eligible); Assert.AreEqual(upstream.Plan.Id.Value, blocked.Waits.Single().PlanId);
        fixture.Native.Issues[1].State = "closed"; fixture.Native.Issues[1].CompletionReason = "not_planned";
        Assert.IsFalse((await client.GetFromJsonAsync<CodingIssueReadiness>(readinessRoute))!.Eligible);
        fixture.Native.Issues[1].CompletionReason = "completed";
        Assert.IsTrue((await client.GetFromJsonAsync<CodingIssueReadiness>(readinessRoute))!.Eligible);
        using var foreign = await client.GetAsync($"/api/workspaces/{Guid.NewGuid()}/projects/{project.Id}/coding/plans/{downstream.Plan.Id}/issue-readiness");
        Assert.IsTrue(foreign.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden);
        Assert.AreEqual(2, fixture.Native.LabelPosts);
        // A second SQLite instance uses another account, service UID and Workspace scope for the same native target.
        var secondDirectory = Path.Combine(fixture.Directory, "second-coordination-host");
        await using var second = CodingFactory(secondDirectory, extension).WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddSingleton<IWorkProjectSharingGateway>(remote)));
        using var secondClient = second.CreateClient();
        var otherContext = await second.Services.GetRequiredService<ILocalEnvironmentBootstrapper>().EnsureInitializedAsync(default);
        using (second.Services.GetRequiredService<IRequestContextScopeFactory>().Push(otherContext))
        {
            var otherTools = await InstallGitHubRelaysAsync(second.Services, otherContext);
            var selection = await InstallIssueServiceAsync(second.Services, otherContext, extension, true);
            Assert.AreNotEqual(project.Definition.ConnectionAssignments.Single().Service.Uid, selection.Assignment.Service.Uid);
            Assert.AreNotEqual(project.Definition.ConnectionAssignments.Single().Service.Address.ScopeRef, selection.Assignment.Service.Address.ScopeRef);
            var definition = new WorkProjectDefinition("Other participant", null, [], [])
            {
                Bindings = [new("sharing", WorkProjectBindingPurpose.Sharing, Pin(otherTools.Tracker))],
                ConnectionRequirements = [selection.Requirement],
                ConnectionAssignments = [selection.Assignment],
                Sharing = project.Definition.Sharing! with { ParticipantId = new(Guid.NewGuid()) }
            };
            var projects = second.Services.GetRequiredService<WorkProjectService>();
            var other = (await projects.CreateAsync(new(otherContext.WorkspaceId), WorkProjectId.New(), "issue-api", default,
                new(Guid.NewGuid(), 0, definition), default)).Value!;
            var sharing = second.Services.GetRequiredService<WorkProjectSharingService>();
            var pulled = (await sharing.PullAsync(new(otherContext.WorkspaceId), other.Id, null, default)).Value!;
            var record = pulled.Records.Single(value => value.Key == new WorkSharingRecordKey(WorkSharingDomain.Plan, upstream.Plan.Id.ToString()));
            var imported = await sharing.ImportPlanAsync(new(otherContext.WorkspaceId), other.Id, upstream.Plan.Id, record.Digest, default);
            Assert.IsTrue(imported.IsSuccess, imported.ErrorCode);
            other = (await projects.GetAsync(new(otherContext.WorkspaceId), other.Id, default)).Value!;
            var command = Command(upstream, "1") with { Id = Guid.NewGuid(), ExpectedProjectVersion = other.Version };
            using var acknowledged = await secondClient.PostAsJsonAsync($"/api/workspaces/{otherContext.WorkspaceId}/projects/{other.Id}/coding/issues/acknowledge", command);
            Assert.AreEqual(HttpStatusCode.Accepted, acknowledged.StatusCode, await acknowledged.Content.ReadAsStringAsync());
            Assert.AreEqual(CodingIssueIntakeStatus.Confirmed, (await acknowledged.Content.ReadFromJsonAsync<CodingIssueIntakeResponse>())!.Status);
            Assert.AreEqual(3, fixture.Native.LabelPosts);
            Assert.AreEqual(1, fixture.Native.Issues[1].Labels.Count(value => value.StartsWith("agentstration:plan:", StringComparison.Ordinal)));
            Assert.AreEqual(2, fixture.Native.Issues[1].Labels.Count(value => value.StartsWith("agentstration:instance:", StringComparison.Ordinal)));
        }
    }

    [TestMethod]
    public async Task GitHubIssueAuthorityNegotiatesImmutableCapabilitiesProtectedPolicyAndExactWorkspace()
    {
        using var fixture = new Fixture(); fixture.Native.AddIssue("Upstream");
        await using var extension = new WebApplicationFactory<GitHubExtension::Program>().WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.Configure<GitHub.GitHubProviderOptions>(options => options.StateDirectory = Path.Combine(fixture.Directory, "provider"));
            services.AddHttpClient<GitHub.GitHubConnection>().ConfigurePrimaryHttpMessageHandler(() => fixture.Native);
        }));
        _ = extension.CreateClient();
        await using var host = CodingFactory(Path.Combine(fixture.Directory, "authority-host"), extension);
        using var client = host.CreateClient();
        var context = await host.Services.GetRequiredService<ILocalEnvironmentBootstrapper>().EnsureInitializedAsync(default);
        using var scope = host.Services.GetRequiredService<IRequestContextScopeFactory>().Push(context);
        var tools = await InstallGitHubRelaysAsync(host.Services, context);
        var selected = await InstallIssueServiceAsync(host.Services, context, extension);
        var binding = WorkProjectConnectionRules.Binding(selected);
        var issue = new WorkSharingIssueReference("github", Repository, "1", "agentstration:plan:", "agentstration:instance:")
        { ConnectionName = "issues", Tracking = selected.Requirement.Target };
        var policy = host.Services.GetRequiredService<ICodingIssueBindingPolicy>();
        var result = await policy.ResolveAsync(new(context.WorkspaceId), context.PrincipalId, binding, issue, default);
        Assert.IsTrue(result.IsSuccess, result.ErrorCode); Assert.AreEqual("github", result.Value!.Provider);
        Assert.IsFalse((await policy.ResolveAsync(new(context.WorkspaceId), context.PrincipalId, binding, issue with { PlanLabelPrefix = "foreign:" }, default)).IsSuccess);
        Assert.IsFalse((await policy.ResolveAsync(new(context.WorkspaceId), context.PrincipalId, binding, issue with { Repository = "foreign/repository" }, default)).IsSuccess);
        Assert.IsFalse((await policy.ResolveAsync(new(Guid.NewGuid()), context.PrincipalId, binding, issue, default)).IsSuccess);
        Assert.IsFalse((await policy.ResolveAsync(new(context.WorkspaceId), Guid.NewGuid(), binding, issue, default)).IsSuccess);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }
    private sealed class IssueSharingFixture : IWorkProjectSharingGateway
    {
        private WorkSharingSnapshot? snapshot;
        private readonly Dictionary<string, WorkSharingProviderResult> receipts = [];
        public Task<Result<WorkSharingProviderCapabilities>> DiscoverAsync(WorkspaceId workspaceId, Guid principalId, WorkProjectContextBinding binding,
            WorkProjectSharingConnection connection, CancellationToken token) => Task.FromResult(Result<WorkSharingProviderCapabilities>.Success(new(WorkSharingFormat.Version, true, true, true, true, true, true, true)));
        public Task<Result<WorkSharingSnapshot>> ReadAsync(WorkspaceId workspaceId, Guid principalId, WorkProjectContextBinding binding,
            WorkProjectSharingConnection connection, string? revision, CancellationToken token) => Task.FromResult(Result<WorkSharingSnapshot>.Success(snapshot ?? new(null, new(connection.ProjectId, []), [])));
        public Task<WorkSharingProviderResult> PublishAsync(WorkspaceId workspaceId, Guid principalId, WorkProjectContextBinding binding,
            WorkProjectSharingConnection connection, WorkSharingTransportAttempt attempt, bool recover, CancellationToken token)
        {
            if (receipts.TryGetValue(attempt.OperationKey, out var result)) return Task.FromResult(result);
            if (recover) return Task.FromResult(new WorkSharingProviderResult(WorkSharingProviderStatus.Missing, attempt.OperationKey, attempt.IntentDigest));
            if (snapshot?.Revision != attempt.ExpectedRevision) return Task.FromResult(new WorkSharingProviderResult(WorkSharingProviderStatus.Conflict, attempt.OperationKey, attempt.IntentDigest));
            var records = (snapshot?.Records ?? []).ToDictionary(record => record.Key);
            foreach (var record in attempt.Records) records[record.Key] = record;
            var revision = "sharing-" + (receipts.Count + 1);
            snapshot = new(revision, attempt.Space, records.Values.ToArray()); result = new(WorkSharingProviderStatus.Succeeded, attempt.OperationKey, attempt.IntentDigest, revision);
            receipts[attempt.OperationKey] = result; return Task.FromResult(result);
        }
    }

}
