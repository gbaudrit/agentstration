extern alias GitHubExtension;
extern alias codingworker;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Agentstration.Application.Work;
using Agentstration.Awp.Abstractions;
using Agentstration.Bootstrap.Contracts;
using Agentstration.Coding;
using Agentstration.Coding.Contracts;
using Agentstration.Extensions.Contracts;
using Agentstration.Flows;
using Agentstration.Flows.Application;
using Agentstration.Identity.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;
using Agentstration.Runtime.Core;
using Agentstration.Runtime.Worker.Core;
using Agentstration.Secrets;
using Agentstration.Secrets.Abstractions;
using Agentstration.Testing;
using Agentstration.Tools;
using Agentstration.Work;
using Agentstration.Work.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using GitHub = GitHubExtension::Agentstration.Extensions.GitHub;
using Native = codingworker::Agentstration.Runtime.Worker.Coding;

namespace Agentstration.Web.Tests;

public sealed partial class ProjectSharingApiTests
{
    [TestMethod]
    public async Task SeededIssueDeliveryRunsRealNativeCodeAndRecoversOneCommitOnePullRequestAndSharedAccounting()
    {
        if (!OperatingSystem.IsLinux()) Assert.Inconclusive("The optional native Coding worker requires Linux Bubblewrap.");
        await using var native = await AuthenticatedGitFixture.CreateAsync();
        await native.GitAsync(native.RepositoryPath, "config", "remote.origin.url", native.Repository);
        using var vendor = new GitHubProviderTests.NativeGitHub(native); vendor.AddIssue("Upstream fixture"); vendor.AddIssue("Deliver fixture");
        vendor.Issues[2].Dependencies.Add(1);
        await using var github = DeliveryGitHubFactory(native, vendor);
        _ = github.CreateClient();
        await using var left = await ConnectedHostAsync(native, "delivery-left", false, github, true);
        var leftSources = await DeliveryPrerequisitesAsync(left);
        var seed = DeliverySeed(native, left.Tool, leftSources);
        var lp = await InitializeDeliverySeedAsync(left, seed, Guid.NewGuid());
        Assert.HasCount(4, lp.Bindings);
        using (left.Scope())
        {
            var pinned = await left.Host.Services.GetRequiredService<FlowService>().ResolveAsync(new WorkspaceId(left.Context.WorkspaceId), new(new FlowId("seed-code")), default);
            var configuration = ((RuntimeFlowDefinition)pinned.Definition).Configuration.Deserialize<CodingExecutionConfiguration>(Json);
            Assert.IsTrue(CodingExecutionValidation.Configuration(configuration, true), JsonSerializer.Serialize(configuration, Json));
        }
        var upstream = await DeliveryPlanAsync(left, lp, "Upstream fixture");
        var target = await DeliveryPlanAsync(left, lp, "Deliver fixture");
        lp = (await left.Http.GetFromJsonAsync<WorkProjectResponse>(left.Root(lp.Id)))!;
        await AcknowledgeDeliveryIssueAsync(left, lp, upstream, "1"); await AcknowledgeDeliveryIssueAsync(left, lp, target, "2");
        var planUrl = $"/api/workspaces/{left.Context.WorkspaceId}/work-plans/{target.Id}";
        await using var worker = await DeliveryWorkerAsync(left, native);
        using var started = await left.Http.PostAsJsonAsync(planUrl + "/execution", new StartWorkPlanExecutionRequest(Guid.NewGuid(), target.Version, target.CurrentRevision, target.Revision.Digest,
            [new(target.Tasks.Single().TaskId, new(new FlowId("seed-code")), JsonSerializer.SerializeToElement(new CodingTaskInput(native.InitialRevision, new Dictionary<string, string>()), Json))])
        { ProjectId = lp.Id });
        Assert.AreEqual(HttpStatusCode.Accepted, started.StatusCode, await started.Content.ReadAsStringAsync());
        using var blocked = await left.Http.PostAsync(planUrl + "/execution/advance", null);
        Assert.AreEqual(HttpStatusCode.BadRequest, blocked.StatusCode, await blocked.Content.ReadAsStringAsync());
        StringAssert.Contains(await blocked.Content.ReadAsStringAsync(), "coding_issue_dependencies_blocked");
        Assert.HasCount(0, (await left.Http.GetFromJsonAsync<WorkPlanResponse>(planUrl))!.Attempts);
        var wait = (await left.Http.GetFromJsonAsync<CodingIssueReadiness>(left.Root(lp.Id) + $"/coding/plans/{target.Id}/issue-readiness"))!;
        Assert.IsFalse(wait.Eligible); Assert.AreEqual("1", wait.Waits.Single().IssueId); Assert.AreEqual(upstream.Id, wait.Waits.Single().PlanId);
        vendor.Issues[1].State = "closed"; vendor.Issues[1].CompletionReason = "completed";
        var executed = await WaitDeliveryPlanAsync(left, planUrl, value => value.Attempts.Count == 1 && value.Attempts[0].Status == WorkPlanAttemptStatus.Completed);
        await worker.DisposeAsync();
        var attempt = executed.Attempts.Single();
        var pulled = await PullAsync(left, lp); var planRecord = pulled.Records.Single(value => value.Key == new WorkSharingRecordKey(WorkSharingDomain.Plan, target.Id.ToString("D")));
        var command = new BeginCodingDeliveryRequest(Guid.NewGuid(), target.Id, attempt.Id, lp.Version, executed.Version, executed.Tasks.Single().Version,
            planRecord.Version, planRecord.Digest, "feat(fixture): deliver existing issue", "## Summary\nDeliver existing fixture issue.\n\n## Changes\n- Add the deterministic answer.\n\n## Validation\n- Native fixture check passed.\n\n## Breaking changes\nNone.");
        var deliveryUrl = left.Root(lp.Id) + "/coding/deliveries/";
        native.LoseNextWriteResponse = true;
        using var first = await left.Http.PostAsJsonAsync(deliveryUrl, command);
        var delivery = await DeliveryResponseAsync(first);
        Assert.AreEqual(WorkTaskDeliveryStatus.Uncertain, delivery.Status); Assert.AreEqual(WorkTaskDeliveryStage.CodePublication, delivery.Stage);
        Assert.IsNotNull(delivery.Code, delivery.ErrorCode);
        Assert.AreEqual(WorkTaskDeliveryEffectStatus.Uncertain, delivery.Code.Status, delivery.ErrorCode);
        Assert.AreEqual(0, vendor.PullPosts);
        native.Unavailable = false; await left.RestartAsync(); vendor.LosePullResponse = true;
        using var second = await left.Http.PostAsync(deliveryUrl + delivery.Id + "/resume", null);
        delivery = await DeliveryResponseAsync(second);
        Assert.AreEqual(WorkTaskDeliveryStage.PullRequest, delivery.Stage, delivery.ErrorCode); Assert.AreEqual(WorkTaskDeliveryStatus.Uncertain, delivery.Status);
        Assert.AreEqual(1, vendor.PullPosts);
        await left.RestartAsync(); var writes = 0;
        Task BeforeUsageWrite()
        {
            if (Interlocked.Increment(ref writes) == 2) native.LoseNextWriteResponse = true;
            else native.BeforeWrite = BeforeUsageWrite;
            return Task.CompletedTask;
        }
        native.BeforeWrite = BeforeUsageWrite;
        using var third = await left.Http.PostAsync(deliveryUrl + delivery.Id + "/resume", null);
        delivery = await DeliveryResponseAsync(third);
        Assert.AreEqual(WorkTaskDeliveryStage.Usage, delivery.Stage, delivery.ErrorCode); Assert.AreEqual(WorkTaskDeliveryStatus.Uncertain, delivery.Status); Assert.IsTrue(delivery.ContributionPublished);
        native.Unavailable = false; native.BeforeWrite = null; await left.RestartAsync();
        using var last = await left.Http.PostAsync(deliveryUrl + delivery.Id + "/resume", null);
        delivery = await DeliveryResponseAsync(last);
        Assert.AreEqual(WorkTaskDeliveryStatus.Delivered, delivery.Status); Assert.AreEqual(WorkTaskDeliveryStage.Finished, delivery.Stage);
        Assert.AreEqual(native.InitialRevision, delivery.Code!.BaseRevision); Assert.AreEqual(delivery.Code.Revision, delivery.PullRequest!.HeadRevision);
        Assert.AreEqual(native.InitialRevision, delivery.PullRequest.BaseRevision); Assert.IsTrue(delivery.PullRequest.Draft); Assert.IsFalse(delivery.PullRequest.Merged);
        Assert.AreEqual("42\n", await native.GitAsync(native.RepositoryPath, "show", delivery.Code.Revision + ":answer.txt"));
        Assert.AreEqual("answer.txt", (await native.GitAsync(native.RepositoryPath, "diff", "--name-only", native.InitialRevision, delivery.Code.Revision!)).Trim());
        Assert.AreEqual("1", (await native.GitAsync(native.RepositoryPath, "rev-list", "--count", native.InitialRevision + ".." + delivery.Code.Revision)).Trim());
        Assert.AreEqual(delivery.Intent.HeadRef, (await native.GitAsync(native.RepositoryPath, "for-each-ref", "--format=%(refname)", "refs/heads/agentstration/")).Trim());
        using var replay = await left.Http.PostAsJsonAsync(deliveryUrl, command); Assert.AreEqual(WorkTaskDeliveryStatus.Delivered, (await DeliveryResponseAsync(replay)).Status);
        Assert.AreEqual(1, vendor.PullPosts); Assert.AreEqual("open", vendor.Issues[2].State); Assert.AreEqual(0, vendor.IssuePosts);
        var unchanged = (await left.Http.GetFromJsonAsync<WorkPlanResponse>(planUrl))!;
        Assert.HasCount(1, unchanged.Attempts); Assert.AreEqual(WorkTaskOutcome.Pending, unchanged.Tasks.Single().Outcome);
        var registrations = left.Host.Services.GetRequiredService<RuntimeWorkerAssignmentService>();
        var assignment = (await registrations.GetByTargetExactAsync(ResourceScopeRef.Workspace(left.Context.WorkspaceId), RuntimeAssignmentTargetKind.FlowRun, attempt.FlowRunId, default))!.Value;
        Assert.HasCount(1, assignment.Attempts);
        await using var right = await ConnectedHostAsync(native, "delivery-right", false, github, true);
        var rightSources = await DeliveryPrerequisitesAsync(right);
        var rp = await InitializeDeliverySeedAsync(right, DeliverySeed(native, right.Tool, rightSources), Guid.NewGuid());
        Assert.HasCount(0, rp.PlanIds);
        var synchronized = await PullAsync(right, rp);
        Assert.AreEqual(1, synchronized.Records.Count(value => value.Key.Domain == WorkSharingDomain.Contribution));
        Assert.AreEqual(1, synchronized.Records.Count(value => value.Key.Domain == WorkSharingDomain.Usage));
        using var totalsResponse = await right.Http.PostAsJsonAsync(right.Root(rp.Id) + "/usage/summary", new WorkUsageSummaryQuery([new(target.Id)], Source: WorkUsageReadSource.Shared));
        Assert.AreEqual(HttpStatusCode.OK, totalsResponse.StatusCode, await totalsResponse.Content.ReadAsStringAsync());
        var totals = (await totalsResponse.Content.ReadFromJsonAsync<WorkUsageSummary>())!;
        Assert.AreEqual(13L, totals.Canonical.Inclusive.InputTokens); Assert.AreEqual(7L, totals.Canonical.Inclusive.OutputTokens); Assert.AreEqual(23L, totals.Canonical.Inclusive.TotalTokens);
        Assert.AreEqual(1, totals.Canonical.Inclusive.InvocationCount); Assert.AreEqual(1, totals.OriginalRecords); Assert.AreEqual(0, totals.Corrections);
        var wire = JsonSerializer.Serialize(synchronized.Records, Json);
        Assert.IsFalse(wire.Contains(left.Context.WorkspaceId.ToString(), StringComparison.Ordinal));
        Assert.IsFalse(wire.Contains(AuthenticatedGitFixture.Credential, StringComparison.Ordinal)); Assert.IsFalse(wire.Contains(GitHubProviderTests.Credential, StringComparison.Ordinal));
        using var foreign = await right.Http.GetAsync(right.Root(lp.Id) + "/coding/deliveries/" + delivery.Id);
        Assert.AreEqual(HttpStatusCode.NotFound, foreign.StatusCode);
    }

    private static async Task<WorkTaskDeliveryOperation> DeliveryResponseAsync(HttpResponseMessage response)
    {
        Assert.AreEqual(HttpStatusCode.Accepted, response.StatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<WorkTaskDeliveryOperation>())!;
    }
    private static WebApplicationFactory<GitHubExtension::Program> DeliveryGitHubFactory(AuthenticatedGitFixture native, GitHubProviderTests.NativeGitHub vendor) =>
        new WebApplicationFactory<GitHubExtension::Program>().WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.Configure<GitHub.GitHubProviderOptions>(options => options.StateDirectory = Path.Combine(native.Root, "github-delivery-journal"));
            services.AddHttpClient<GitHub.GitHubConnection>().ConfigurePrimaryHttpMessageHandler(() => new BorrowedVendorHandler(vendor));
        }));
    private sealed class BorrowedVendorHandler(HttpMessageHandler vendor) : HttpMessageHandler
    {
        private readonly HttpMessageInvoker invoker = new(vendor, false);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => invoker.SendAsync(request, token);
        protected override void Dispose(bool disposing) { if (disposing) invoker.Dispose(); base.Dispose(disposing); }
    }
    private sealed class CombinedExtensionHandler(HttpMessageHandler git, HttpMessageHandler github) : HttpMessageHandler
    {
        private readonly HttpMessageInvoker gitInvoker = new(git); private readonly HttpMessageInvoker githubInvoker = new(github);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            (request.RequestUri!.Host == "github-fixture" ? githubInvoker : gitInvoker).SendAsync(request, token);
        protected override void Dispose(bool disposing) { if (disposing) { gitInvoker.Dispose(); githubInvoker.Dispose(); } base.Dispose(disposing); }
    }
    private static async Task<Dictionary<string, ToolResource>> DeliveryPrerequisitesAsync(SharingHost host)
    {
        using var actor = host.Scope(); await SeedPrerequisitesAsync(host);
        var owner = ResourceScopeRef.Workspace(host.Context.WorkspaceId); var resources = host.Host.Services.GetRequiredService<IResourceStore>();
        await resources.PutExactAsync(owner, new ExtensionRegistrationResource
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = ExtensionKinds.ExtensionRegistration,
            ScopeRef = owner,
            Metadata = new() { Name = "github-fixture" },
            Definition = new()
            {
                DisplayName = "GitHub fixture",
                Endpoint = new("http://github-fixture/"),
                ExpectedExtensionId = "Agentstration.Extensions.GitHub",
                Enabled = true,
                AuthenticationMode = AepTransportAuthenticationMode.None,
                EnrollmentMode = AepEnrollmentMode.PairingCode
            }
        }, null, true, default);
        var tools = host.Host.Services.GetRequiredService<ToolManagementService>();
        await tools.PutProviderAsync(new ToolProviderResource
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = ToolResourceKinds.ToolProvider,
            ScopeRef = owner,
            Metadata = new() { Name = "github-fixture" },
            Definition = new() { DisplayName = "GitHub fixture", ProviderType = ToolProviderType.Aep, Aep = new() { Extension = new("github-fixture", owner, default) } }
        }, null, true, default);
        await tools.RefreshDiscoveryExactAsync(owner, "github-fixture", default, default);
        var selected = (await tools.ListVisibleToolsAsync(owner, default)).Where(value => value.Value.Definition.Provider?.Name == "github-fixture").ToArray();
        foreach (var tool in selected) await tools.SetToolEnabledAsync(tool.Value.Name, true, tool.ETag, default);
        var result = new Dictionary<string, ToolResource>();
        var authority = ResourceGovernance.Extension($"{owner.Value}/ExtensionRegistration/default/github-fixture", "Agentstration.Extensions.GitHub", "1.0.0");
        using (host.Host.Services.GetRequiredService<IResourceGovernanceContext>().Push(authority))
        {
            foreach (var tool in (await tools.ListVisibleToolsAsync(owner, default)).Where(value => value.Value.Definition.Provider?.Name == "github-fixture"))
            {
                var managed = await resources.PutExactAsync(owner, new ToolResource
                {
                    ApiVersion = ResourceApiVersions.CoreV1,
                    Kind = ToolResourceKinds.Tool,
                    ScopeRef = owner,
                    Governance = authority,
                    Metadata = new() { Name = "seed-" + tool.Value.Definition.ExternalId!.Replace('.', '-') },
                    Definition = tool.Value.Definition
                }, null, true, default);
                result.Add(tool.Value.Definition.ExternalId!, managed.Value);
            }
        }
        var secrets = host.Host.Services.GetRequiredService<SecretManagementService>();
        var secret = await secrets.CreateSecretAsync(new SecretResource
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = SecretResourceKinds.Secret,
            ScopeRef = owner,
            Metadata = new() { Name = "github-token" },
            Definition = new() { DisplayName = "Public fixture token", Vault = new("git-vault", owner), Key = "github-token" }
        }, default);
        using (var value = new SecretValue(Encoding.UTF8.GetBytes(GitHubProviderTests.Credential))) await secrets.SetValueExactAsync(owner, secret.Value.Name, value, default);
        return result;
    }
    private static byte[] DeliverySeed(AuthenticatedGitFixture native, ToolResource git, Dictionary<string, ToolResource> github)
    {
        var original = SeedArchive(native, git, new(Guid.NewGuid(), new string('a', 64)));
        using var input = new MemoryStream(original); using var zip = new ZipArchive(input, ZipArchiveMode.Read);
        var entries = zip.Entries.ToDictionary(entry => entry.FullName, entry => { using var reader = new StreamReader(entry.Open()); return reader.ReadToEnd(); });
        var manifest = JsonSerializer.Deserialize<WorkspaceSeedManifest>(entries[WorkspaceSeedFormat.ManifestPath], Json)!;
        manifest = manifest with
        {
            ImportPlans = [],
            Requirements = manifest.Requirements with
            {
                Contributions = [.. manifest.Requirements.Contributions,
            new() { Kind = "Tool", ExtensionId = "Agentstration.Extensions.GitHub", ContributionId = "github.issue" },
            new() { Kind = "Tool", ExtensionId = "Agentstration.Extensions.GitHub", ContributionId = "github.coding" }]
            }
        };
        entries[WorkspaceSeedFormat.ManifestPath] = JsonSerializer.Serialize(manifest, Json);
        var sharing = ResourceManifestSerializer.FromYaml<JsonElement>(entries["bootstrap/10-relay.yaml"]);
        var sharingNode = JsonNode.Parse(sharing.GetRawText())!;
        var shareBindings = sharingNode["definition"]!["bindings"]!.AsArray();
        foreach (var binding in shareBindings)
        {
            if (binding!["path"]!.GetValue<string>() == "/request/refName") binding["value"] = "refs/heads/agentstration-sharing";
            if (binding["path"]!.GetValue<string>() == "/allowedRefPrefixes") binding["value"] = JsonSerializer.SerializeToNode(new[] { "refs/heads/agentstration-sharing" }, Json);
        }
        entries["bootstrap/10-relay.yaml"] = ResourceManifestSerializer.ToYaml(sharingNode);
        var code = sharingNode.DeepClone(); code["metadata"]!["name"] = "seed-code-publication"; code["definition"]!["displayName"] = "Code publication fixture";
        var bindings = code["definition"]!["bindings"]!.AsArray();
        var fixedRef = bindings.Single(value => value!["path"]!.GetValue<string>() == "/request/refName"); bindings.Remove(fixedRef);
        foreach (var binding in bindings)
        {
            if (binding!["path"]!.GetValue<string>() == "/allowedRefPrefixes") binding["value"] = JsonSerializer.SerializeToNode(new[] { "refs/heads/agentstration/" }, Json);
            if (binding["path"]!.GetValue<string>() == "/allowedPathPrefixes") binding["value"] = JsonSerializer.SerializeToNode(new[] { "answer.txt" }, Json);
        }
        entries["bootstrap/11-code-relay.yaml"] = ResourceManifestSerializer.ToYaml(code);
        foreach (var (id, name, capability) in new[] { ("github.issue", "seed-issue", WorkTaskProviderProtocol.MetadataKey), ("github.coding", "seed-repository", CodingProviderProtocol.MetadataKey) })
        {
            var relay = JsonSerializer.SerializeToNode<ToolDefinitionProperties>(new RelayToolDefinitionProperties
            {
                DisplayName = name,
                Publish = true,
                Target = new() { Name = github[id].Name, ScopeRef = github[id].ScopeRef!.Value, Uid = github[id].Uid, Generation = github[id].Generation },
                Metadata = github[id].Definition.Metadata.Where(pair => pair.Key == capability || pair.Key == CodingIssueProtocol.MetadataKey).ToDictionary(pair => pair.Key, pair => pair.Value),
                Bindings = [new SecretToolArgumentBinding { Path = "/credential", Secret = new(new(default, "Secret", "github-token")) },
                    Literal("/apiBaseUrl", "https://api.github.com"), Literal("/allowedRepositories", new[] { new { repository = "fixture/repository", nativeId = "42" } }),
                    .. id == "github.issue" ? new ToolArgumentBinding[] { Literal("/managedLabelPrefix", "agentstration:") } : []]
            }, Json)!.AsObject();
            relay["target"] = JsonSerializer.SerializeToNode(new { extensionContribution = new { kind = "Tool", extensionId = "Agentstration.Extensions.GitHub", contributionId = id } }, Json);
            relay["bindings"]![0]!["secret"] = JsonSerializer.SerializeToNode(new { resourceRef = new { name = "github-token" } }, Json);
            entries["bootstrap/" + (id == "github.issue" ? "12" : "13") + "-" + name + ".yaml"] = SeedResource("ToolDefinition", name, relay);
        }
        var project = JsonNode.Parse(ResourceManifestSerializer.FromYaml<JsonElement>(entries["bootstrap/20-project.yaml"]).GetRawText())!;
        project["definition"]!["repositoryToolName"] = "agentstration.seed-repository.v1.0.0";
        project["definition"]!["taskOperationsToolName"] = "agentstration.seed-issue.v1.0.0";
        project["definition"]!["codePublicationToolName"] = "agentstration.seed-code-publication.v1.0.0";
        entries["bootstrap/20-project.yaml"] = ResourceManifestSerializer.ToYaml(project);
        var fake = DeliveryFakeCli();
        var flow = JsonNode.Parse(ResourceManifestSerializer.FromYaml<JsonElement>(entries["bootstrap/50-flow.yaml"]).GetRawText())!;
        var configuration = new CodingExecutionConfiguration(new("seed-runtime", default),
            new("seed-coder", default, "1.0.0", flow["definition"]!["spec"]!["configuration"]!["harness"]!["digest"]!.GetValue<string>()), "fixture/repository")
        { Checks = [new("check", "fixture-check", [fake, "check", "pass"])] };
        flow["definition"]!["spec"]!["configuration"] = JsonSerializer.SerializeToNode(configuration, Json);
        entries["bootstrap/50-flow.yaml"] = ResourceManifestSerializer.ToYaml(flow);
        using var output = new MemoryStream(); using (var package = new ZipArchive(output, ZipArchiveMode.Create, true)) foreach (var entry in entries) WriteSeed(package, entry.Key, entry.Value);
        return output.ToArray();
    }
    private static async Task<WorkProjectResponse> InitializeDeliverySeedAsync(SharingHost host, byte[] bytes, Guid participant)
    {
        var target = new BootstrapApplicationTarget(host.Context.TenantId, host.Context.WorkspaceId);
        using var ingestion = await host.Http.PostAsJsonAsync("/api/bootstrap/workspace-seeds", new IngestWorkspaceSeedRequest(Guid.NewGuid(), target, Convert.ToBase64String(bytes)));
        Assert.AreEqual(HttpStatusCode.Created, ingestion.StatusCode, await ingestion.Content.ReadAsStringAsync());
        var receipt = (await ingestion.Content.ReadFromJsonAsync<WorkspaceSeedReceipt>())!;
        var selection = new WorkspaceSeedSelection(receipt.Provenance.Id, receipt.Provenance.ArchiveDigest);
        var input = new BootstrapInputSelection("seed", "Agentstration:Bootstrap:Parameters:ParticipantId", "literal", Literal: JsonSerializer.SerializeToElement(participant.ToString()));
        var preview = await SeedPreviewAsync(host, target, selection, input); Assert.IsTrue(preview.CanApply, JsonSerializer.Serialize(preview, Json));
        var application = await ApplySeedAsync(host, target, selection, input, preview.Digest);
        Assert.AreEqual(BootstrapApplicationStatus.Succeeded, application.Definition.Status, JsonSerializer.Serialize(application, Json));
        return (await host.Http.GetFromJsonAsync<WorkProjectResponse>(host.Root(application.Definition.SeedHandover!.ProjectId)))!;
    }
    private static async Task<WorkPlanResponse> DeliveryPlanAsync(SharingHost host, WorkProjectResponse project, string title)
    {
        using var actor = host.Scope(); var workspace = new WorkspaceId(host.Context.WorkspaceId); var task = new WorkTaskId(Guid.NewGuid());
        Assert.IsTrue((await host.Host.Services.GetRequiredService<WorkTaskTreeService>().CreateAsync(workspace, new(task, [new(task, WorkTaskKind.Atomic, title, "Produce the deterministic fixture answer.")]), default)).IsSuccess);
        var plans = host.Host.Services.GetRequiredService<WorkPlanService>();
        var created = await plans.CreateAsync(workspace, WorkPlanId.New(), new(Guid.NewGuid(), 0, new(title, [task], [])), default);
        Assert.IsTrue(created.IsSuccess, created.ErrorCode); var view = created.Value!;
        foreach (var action in new[] { WorkPlanAction.Validate, WorkPlanAction.Activate })
        {
            var changed = await plans.TransitionAsync(workspace, view.Plan.Id, new(Guid.NewGuid(), view.Plan.Version, view.Revision.Number, view.Revision.Digest, action), default);
            Assert.IsTrue(changed.IsSuccess, changed.ErrorCode); view = changed.Value!;
        }
        var projects = host.Host.Services.GetRequiredService<WorkProjectService>(); var current = (await projects.GetAsync(workspace, new(project.Id), default)).Value!;
        var associated = await projects.UpdateAsync(workspace, current.Id, new(Guid.NewGuid(), current.Version, current.Definition with { PlanIds = [.. current.Definition.PlanIds, view.Plan.Id] }), default);
        Assert.IsTrue(associated.IsSuccess, associated.ErrorCode);
        return WorkPlanContractMapping.ToPlanResponse(view);
    }
    private static async Task AcknowledgeDeliveryIssueAsync(SharingHost host, WorkProjectResponse project, WorkPlanResponse plan, string issue)
    {
        using var response = await host.Http.PostAsJsonAsync(host.Root(project.Id) + "/coding/issues/acknowledge", new AcknowledgeCodingIssueRequest(Guid.NewGuid(), project.Version,
            plan.Id, "fixture/repository", issue, "github", "agentstration:plan:", "agentstration:instance:", 0,
            [new(new(plan.Tasks.Single().TaskId), project.Sharing!.ParticipantId)]));
        Assert.AreEqual(HttpStatusCode.Accepted, response.StatusCode, await response.Content.ReadAsStringAsync());
        Assert.AreEqual(CodingIssueIntakeStatus.Confirmed, (await response.Content.ReadFromJsonAsync<CodingIssueIntakeResponse>())!.Status);
    }
    private static async Task<AwpTestWorker> DeliveryWorkerAsync(SharingHost host, AuthenticatedGitFixture native)
    {
        var fake = DeliveryFakeCli(); var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? Path.GetFullPath(Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "../../../dotnet"));
        var options = new Native.CodingWorkerOptions
        {
            CliExecutable = dotnet,
            CliPrefixArguments = [fake, "run-mode", "success", Path.Combine(native.Root, "authority-key-not-mounted")],
            ReadOnlyRuntimeDirectories = [Path.GetDirectoryName(fake)!],
            CheckExecutables = new Dictionary<string, string> { ["fixture-check"] = dotnet },
            Repositories = [new("github", "42", native.Repository, native.RepositoryPath)]
        };
        var executor = new Native.CodingAssignmentExecutor(new Native.CodingSandbox(Options.Create(options), Options.Create(new RuntimeWorkerOptions())), TimeProvider.System);
        return await AwpTestWorker.StartAsync(host.Host, true, executor, [new("coding-cli", "1.0", ["1.0"])]);
    }
    private static string DeliveryFakeCli()
    {
        for (var root = new DirectoryInfo(AppContext.BaseDirectory); root is not null; root = root.Parent)
            if (File.Exists(Path.Combine(root.FullName, "Agentstration.slnx"))) return Path.Combine(root.FullName, "tests/fixtures/NativeCodingCli/bin", new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name, "net10.0/NativeCodingCli.dll");
        throw new InvalidOperationException("Fixture unavailable.");
    }
    private static async Task<WorkPlanResponse> WaitDeliveryPlanAsync(SharingHost host, string url, Func<WorkPlanResponse, bool> ready)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60)); WorkPlanResponse? last = null;
        try { while (true) { last = await host.Http.GetFromJsonAsync<WorkPlanResponse>(url, timeout.Token); if (last is not null && ready(last)) return last; await Task.Delay(50, timeout.Token); } }
        catch (OperationCanceledException) { Assert.Fail("Delivery plan did not reach expected state: " + JsonSerializer.Serialize(last, Json)); throw; }
    }
}
