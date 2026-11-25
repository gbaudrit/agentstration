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
                Bindings = [new("sharing", WorkProjectBindingPurpose.Sharing, Pin(tools.Tracker)), new("tracker", WorkProjectBindingPurpose.TaskOperations, Pin(tools.Tracker))],
                Sharing = new(new(Guid.NewGuid()), new(Guid.NewGuid()), "sharing", "fixture")
            };
            var created = await host.Services.GetRequiredService<WorkProjectService>().CreateAsync(new(context.WorkspaceId), WorkProjectId.New(), "issue-api", default, new(Guid.NewGuid(), 0, definition), default);
            Assert.IsTrue(created.IsSuccess, created.ErrorCode); project = created.Value!;
        }
        var prefix = $"/api/workspaces/{context.WorkspaceId}/projects/{project.Id}/coding";
        AcknowledgeCodingIssueRequest Command(WorkPlanView plan, string number) => new(Guid.NewGuid(), project.Version, plan.Plan.Id.Value,
            Repository, number, "github", "agentstration:plan:", "agentstration:instance:", 0,
            plan.Revision.Definition.TaskIds.Select(task => new WorkTaskAllocation(task, project.Definition.Sharing!.ParticipantId)).ToArray());
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
        var binding = new WorkProjectContextBinding("issues", WorkProjectBindingPurpose.TaskOperations, Pin(tools.Tracker));
        var issue = new WorkSharingIssueReference("github", Repository, "1", "agentstration:plan:", "agentstration:instance:");
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
