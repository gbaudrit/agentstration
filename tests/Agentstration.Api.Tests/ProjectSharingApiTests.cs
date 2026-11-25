extern alias GitExtension;
extern alias GitHubExtension;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Agentstration.Application.Work;
using Agentstration.Extensions.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.Infrastructure.Bootstrap;
using Agentstration.ResourceManagement;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;
using Agentstration.Secrets;
using Agentstration.Secrets.Abstractions;
using Agentstration.Testing;
using Agentstration.Tools;
using Agentstration.Tools.Contracts;
using Agentstration.Work;
using Agentstration.Work.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed partial class ProjectSharingApiTests
{
    [TestMethod]
    public async Task TwoIndependentHostsSharePlansContributionsUsageAndResumeWithProtectedNativeGit()
    {
        await using var native = await AuthenticatedGitFixture.CreateAsync();
        await using var left = await ConnectedHostAsync(native, "left");
        await using var right = await ConnectedHostAsync(native, "right");
        var portable = new WorkSharingProjectId(Guid.NewGuid()); var participant = new WorkParticipantId(Guid.NewGuid());
        var lp = await CreateProjectAsync(left, portable, participant);
        var rp = await CreateProjectAsync(right, portable, new(Guid.NewGuid()));
        var root = new WorkTaskId(Guid.NewGuid());
        var tree = new WorkTaskTreeDefinition(root, [new(root, WorkTaskKind.Atomic, "Implement", "Produce deterministic evidence")]);
        WorkPlanView local;
        using (left.Scope())
        {
            Assert.IsTrue((await left.Host.Services.GetRequiredService<WorkTaskTreeService>().CreateAsync(new(left.Context.WorkspaceId), tree, default)).IsSuccess);
            var plans = left.Host.Services.GetRequiredService<WorkPlanService>();
            var created = await plans.CreateAsync(new(left.Context.WorkspaceId), WorkPlanId.New(), new(Guid.NewGuid(), 0, new("Deliver fixture", [root], [])), default);
            Assert.IsTrue(created.IsSuccess, created.ErrorCode); local = created.Value!;
            var projects = left.Host.Services.GetRequiredService<WorkProjectService>();
            var project = (await projects.GetAsync(new(left.Context.WorkspaceId), new(lp.Id), default)).Value!;
            var updated = await projects.UpdateAsync(new(left.Context.WorkspaceId), project.Id, new(Guid.NewGuid(), project.Version,
                project.Definition with { PlanIds = [local.Plan.Id] }), default);
            Assert.IsTrue(updated.IsSuccess, updated.ErrorCode); lp = WorkProjectContractMapping.ToProjectResponse(updated.Value!);
        }
        var plan = new WorkSharingPlan(local.Plan.Id, local.Revision.Definition, [tree], [new(root, participant)]);
        var planChange = new WorkSharingChange(new(WorkSharingDomain.Plan, local.Plan.Id.ToString()), 0, JsonSerializer.SerializeToElement(plan, Json));
        var published = await PublishAsync(left, lp, [planChange]);
        Assert.AreEqual(WorkSharingOperationStatus.Succeeded, published.Status, published.ErrorCode);
        var pulled = await PullAsync(right, rp); var record = pulled.Records.Single();
        Assert.AreEqual(local.Plan.Id, record.Content.Deserialize<WorkSharingPlan>(Json)!.PlanId);
        using (var import = await right.Http.PostAsJsonAsync(right.Root(rp.Id) + $"/sharing/plans/{local.Plan.Id}/import", new ImportWorkSharingPlanRequest(record.Digest)))
        {
            Assert.AreEqual(HttpStatusCode.OK, import.StatusCode, await import.Content.ReadAsStringAsync());
            var draft = (await import.Content.ReadFromJsonAsync<WorkPlanResponse>())!;
            Assert.AreEqual(WorkPlanStatus.Draft, draft.Status); Assert.HasCount(0, draft.Attempts);
        }
        // Membership changed when importing; use the current Project configuration version.
        rp = (await right.Http.GetFromJsonAsync<WorkProjectResponse>(right.Root(rp.Id)))!;
        var first = Contribution(); var second = Contribution();
        var concurrent = await Task.WhenAll(PublishAsync(left, lp, [first]), PublishAsync(right, rp, [second]));
        Assert.IsTrue(concurrent.All(value => value.Status == WorkSharingOperationStatus.Succeeded));
        var after = await PullAsync(left, lp);
        Assert.AreEqual(2, after.Records.Count(value => value.Key.Domain == WorkSharingDomain.Contribution));
        var usage = new WorkSharingUsage(Guid.NewGuid(), local.Plan.Id, root, new Dictionary<string, long> { ["tokens"] = 42 });
        var command = new PublishWorkSharingRequest(Guid.NewGuid(), lp.Version,
            [new(new(WorkSharingDomain.Usage, usage.Id.ToString("D")), 0, JsonSerializer.SerializeToElement(usage, Json))]);
        native.LoseNextWriteResponse = true;
        using var lost = await left.Http.PostAsJsonAsync(left.Root(lp.Id) + "/sharing/operations", command);
        Assert.AreEqual(HttpStatusCode.Accepted, lost.StatusCode, await lost.Content.ReadAsStringAsync());
        var uncertain = (await lost.Content.ReadFromJsonAsync<WorkSharingOperationResponse>())!;
        Assert.AreEqual(WorkSharingOperationStatus.Uncertain, uncertain.Status);
        native.Unavailable = false;
        // Restart the independent host while retaining local Work state and the provider journal.
        await left.RestartAsync();
        using var resumed = await left.Http.PostAsync(left.Root(lp.Id) + $"/sharing/operations/{command.Id}/resume", null);
        Assert.AreEqual(HttpStatusCode.Accepted, resumed.StatusCode, await resumed.Content.ReadAsStringAsync());
        Assert.AreEqual(WorkSharingOperationStatus.Succeeded, (await resumed.Content.ReadFromJsonAsync<WorkSharingOperationResponse>())!.Status);
        using var replay = await left.Http.PostAsJsonAsync(left.Root(lp.Id) + "/sharing/operations", command);
        Assert.AreEqual(HttpStatusCode.Accepted, replay.StatusCode);
        var current = await PullAsync(right, rp);
        Assert.AreEqual(1, current.Records.Count(value => value.Key.Domain == WorkSharingDomain.Usage));
        Assert.AreEqual(2, current.Records.Count(value => value.Key.Domain == WorkSharingDomain.Contribution));
        var historical = await PullAsync(right, rp, published.Revision);
        Assert.HasCount(1, historical.Records);
        using var foreign = await right.Http.PostAsJsonAsync($"/api/workspaces/{Guid.NewGuid()}/projects/{rp.Id}/sharing/pull", new PullWorkSharingRequest());
        Assert.IsTrue(foreign.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound);
        var raw = (await native.GitAsync(native.RepositoryPath, "show", "main:shared/index.json"));
        Assert.IsFalse(raw.Contains(left.Context.WorkspaceId.ToString(), StringComparison.Ordinal));
        Assert.IsFalse(raw.Contains(AuthenticatedGitFixture.Credential, StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ProjectSharingBootstrapIsExplicitIdempotentAndInitialSynchronizationIsSeparate()
    {
        await using var native = await AuthenticatedGitFixture.CreateAsync(); await using var host = await ConnectedHostAsync(native, "bootstrap");
        using var scope = host.Scope();
        var definition = new DeclarativeProjectSharingDefinition(new(Guid.NewGuid()), new(Guid.NewGuid()), "shared", host.Tool.Name)
        { RepositoryToolName = host.Tool.Name, TaskOperationsToolName = host.Tool.Name };
        var document = JsonSerializer.Deserialize<BootstrapResourceDocument>(JsonSerializer.Serialize(new
        {
            apiVersion = "agentstration.io/v1",
            kind = "ProjectSharing",
            metadata = new { name = "bootstrap-sharing", @namespace = "default" },
            definition
        }, Json), Json)!;
        var handler = ActivatorUtilities.CreateInstance<ProjectSharingBootstrapResourceHandler>(host.Host.Services);
        var operation = new BootstrapResourceOperationContext("fixture", native.Root, BootstrapProfileScope.Workspace,
            new(host.Context.TenantId, host.Context.WorkspaceId))
        { ActorPrincipalId = host.Context.PrincipalId };
        var initial = (await native.GitAsync(native.RepositoryPath, "rev-parse", "main")).Trim();
        Assert.AreEqual(BootstrapResourceDisposition.Create, (await handler.PlanAsync(document, operation, new(), default)).Disposition);
        Assert.AreEqual(BootstrapResourceApplyResult.Created, await handler.ApplyAsync(document, operation, default));
        Assert.AreEqual(BootstrapResourceApplyResult.Skipped, await handler.ApplyAsync(document, operation, default));
        Assert.AreEqual(initial, (await native.GitAsync(native.RepositoryPath, "rev-parse", "main")).Trim());
        var projects = host.Host.Services.GetRequiredService<WorkProjectService>();
        var project = (await projects.FindAsync(new(host.Context.WorkspaceId), default, "bootstrap-sharing", default)).Value!;
        CollectionAssert.AreEquivalent(new[] { WorkProjectBindingPurpose.Sharing, WorkProjectBindingPurpose.Repository, WorkProjectBindingPurpose.TaskOperations }, project.Definition.Bindings.Select(binding => binding.Purpose).ToArray());
        Assert.IsTrue(project.Definition.Bindings.All(binding => binding.Target.Uid == host.Tool.Uid && binding.Target.Generation == host.Tool.Generation));
        var sharing = host.Host.Services.GetRequiredService<WorkProjectSharingService>();
        var pull = await sharing.PullAsync(new(host.Context.WorkspaceId), project.Id, null, default);
        Assert.IsTrue(pull.IsSuccess, pull.ErrorCode); Assert.HasCount(0, pull.Value!.Records);
        Assert.AreEqual(BootstrapResourceApplyResult.Skipped, await handler.ApplyAsync(document, operation, default));
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static WorkSharingChange Contribution()
    {
        var value = new WorkSharingContribution(Guid.NewGuid(), null, null, "evidence", "Verified native fixture", []);
        return new(new(WorkSharingDomain.Contribution, value.Id.ToString("D")), 0, JsonSerializer.SerializeToElement(value, Json));
    }
    private static async Task<WorkProjectResponse> CreateProjectAsync(SharingHost host, WorkSharingProjectId portable, WorkParticipantId participant)
    {
        var binding = new WorkProjectContextBinding("sharing", WorkProjectBindingPurpose.Sharing,
            new(ScopedResourceAddress.Create(host.Tool.ScopeRef!.Value, host.Tool.Namespace, ToolResourceKinds.Tool, host.Tool.Name), host.Tool.Uid, host.Tool.Generation));
        using var response = await host.Http.PostAsJsonAsync($"/api/workspaces/{host.Context.WorkspaceId}/projects", new CreateWorkProjectRequest(Guid.NewGuid(), Guid.NewGuid(), "shared",
            new("Shared fixture", null, [], []) { Bindings = [binding], Sharing = new(portable, participant, "sharing", "shared") }));
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<WorkProjectResponse>())!;
    }
    private static async Task<WorkSharingOperationResponse> PublishAsync(SharingHost host, WorkProjectResponse project, IReadOnlyList<WorkSharingChange> changes)
    {
        using var response = await host.Http.PostAsJsonAsync(host.Root(project.Id) + "/sharing/operations", new PublishWorkSharingRequest(Guid.NewGuid(), project.Version, changes));
        Assert.AreEqual(HttpStatusCode.Accepted, response.StatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<WorkSharingOperationResponse>())!;
    }
    private static async Task<WorkSharingStateResponse> PullAsync(SharingHost host, WorkProjectResponse project, string? revision = null)
    {
        using var response = await host.Http.PostAsJsonAsync(host.Root(project.Id) + "/sharing/pull", new PullWorkSharingRequest(revision));
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<WorkSharingStateResponse>())!;
    }
    private static async Task<SharingHost> ConnectedHostAsync(AuthenticatedGitFixture native, string name, bool publishRelay = true, WebApplicationFactory<GitHubExtension::Program>? github = null, bool coding = false)
    {
        var extension = new WebApplicationFactory<GitExtension::Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("GitOperations:Enabled", "true"); builder.UseSetting("GitOperations:AllowedRepositories:0", native.Repository);
            builder.UseSetting("GitOperations:AllowHttpLoopback", "true"); builder.UseSetting("GitOperations:StateDirectory", Path.Combine(native.Root, name, "journal"));
            builder.UseSetting("GitSourceProvider:TemporaryDirectory", Path.Combine(native.Root, name, "extension-work"));
        });
        _ = extension.CreateClient();
        var handlers = new HandlerFactory(extension, github);
        Func<WebApplicationFactory<global::Program>> hostFactory = () => new WebApplicationFactory<global::Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development"); builder.UseSetting("Data:Directory", Path.Combine(native.Root, name, "host"));
            builder.UseSetting("Agentstration:Authentication:Mode", "Development"); builder.UseSetting("AI:Provider", "Deterministic");
            builder.UseSetting("Agentstration:Authentication:DevelopmentGrantPlatformAdministrator", (!publishRelay).ToString());
            builder.UseSetting("Agentstration:Extensions:DiscoverOnStartup", "false"); builder.UseSetting("Logging:LogLevel:Default", "Warning");
            if (coding) AwpTestWorker.Configure(builder);
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<IHttpMessageHandlerFactory>(handlers);
                services.AddHttpClient("workspace-seed-ingestion").ConfigurePrimaryHttpMessageHandler(() => handlers.CreateHandler("workspace-seed-ingestion"));
            });
        });
        var host = hostFactory();
        var http = host.CreateClient();
        var context = await host.Services.GetRequiredService<ILocalEnvironmentBootstrapper>().EnsureInitializedAsync(default);
        using var scope = host.Services.GetRequiredService<IRequestContextScopeFactory>().Push(context);
        var owner = ResourceScopeRef.Workspace(context.WorkspaceId); var store = host.Services.GetRequiredService<IResourceStore>();
        await store.PutExactAsync(owner, new ExtensionRegistrationResource
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = ExtensionKinds.ExtensionRegistration,
            Metadata = new() { Name = "git-fixture" },
            ScopeRef = owner,
            Definition = new()
            {
                DisplayName = "Git fixture",
                Endpoint = new("http://git-fixture/"),
                ExpectedExtensionId = "Agentstration.Extensions.Git",
                Enabled = true,
                AuthenticationMode = AepTransportAuthenticationMode.None,
                EnrollmentMode = AepEnrollmentMode.PairingCode
            }
        }, null, true, default);
        var tools = host.Services.GetRequiredService<ToolManagementService>();
        await tools.PutProviderAsync(new ToolProviderResource
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = ToolResourceKinds.ToolProvider,
            Metadata = new() { Name = "git-fixture" },
            ScopeRef = owner,
            Definition = new() { DisplayName = "Git fixture", ProviderType = ToolProviderType.Aep, Aep = new() { Extension = new("git-fixture", owner, default) } }
        }, null, true, default);
        await tools.RefreshDiscoveryExactAsync(owner, "git-fixture", default, default);
        var discovered = (await tools.ListVisibleToolsAsync(owner, default)).Single(value => value.Value.Definition.ExternalId == "git.repository");
        await tools.SetToolEnabledAsync(discovered.Value.Name, true, discovered.ETag, default);
        var source = (await tools.ListVisibleToolsAsync(owner, default)).Single(value => value.Value.Definition.ExternalId == "git.repository").Value;
        var secrets = host.Services.GetRequiredService<SecretManagementService>();
        await secrets.CreateVaultAsync(new VaultResource
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = SecretResourceKinds.Vault,
            Metadata = new() { Name = "git-vault" },
            ScopeRef = owner,
            Definition = new() { DisplayName = "Git vault", ProviderType = "local" }
        }, default);
        await secrets.InitializeVaultExactAsync(owner, "git-vault", default);
        var secret = await secrets.CreateSecretAsync(new SecretResource
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = SecretResourceKinds.Secret,
            Metadata = new() { Name = "git-credential" },
            ScopeRef = owner,
            Definition = new() { DisplayName = "Git fixture credential", Vault = new("git-vault", owner), Key = "credential" }
        }, default);
        using (var value = new SecretValue(Encoding.UTF8.GetBytes(AuthenticatedGitFixture.Credential))) await secrets.SetValueExactAsync(owner, secret.Value.Name, value, default);
        if (!publishRelay) return new(host, extension, http, context, source, hostFactory);
        const string capability = "agentstration.git.repository";
        var definitions = host.Services.GetRequiredService<ToolDefinitionService>();
        await definitions.PutAsync(new ToolDefinitionResource
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = ToolResourceKinds.ToolDefinition,
            Metadata = new() { Name = "git-connection" },
            ScopeRef = owner,
            Definition = new RelayToolDefinitionProperties
            {
                DisplayName = "Git connection",
                Version = "1.0.0",
                Publish = true,
                Target = new() { Name = source.Name, Namespace = source.Namespace, ScopeRef = owner, Uid = source.Uid, Generation = source.Generation },
                Metadata = new Dictionary<string, JsonElement> { [capability] = source.Definition.Metadata[capability] },
                Bindings = [new SecretToolArgumentBinding { Path = "/credential", Secret = new(secret.Value.Address, owner) },
                    Literal("/repository", native.Repository), Literal("/authentication", "Basic"), Literal("/username", "fixture"),
                    Literal("/request/refName", "refs/heads/main"), Literal("/allowedRefPrefixes", new[] { "refs/heads/main" }), Literal("/allowedPathPrefixes", new[] { "shared" })]
            }
        }, null, true, default);
        var publication = (await definitions.ListVersionsAsync("git-connection", default, default)).Single();
        Assert.IsFalse(publication.Definition.InputSchema.GetProperty("properties").TryGetProperty("credential", out _));
        Assert.IsFalse(ResourceManifestSerializer.ToJson(publication).Contains(AuthenticatedGitFixture.Credential, StringComparison.Ordinal));
        var published = (await tools.ListVisibleToolsAsync(owner, default)).Single(value => value.Value.Definition.ExternalId == publication.Name).Value;
        return new(host, extension, http, context, published, hostFactory);

    }
    private static LiteralToolArgumentBinding Literal<T>(string path, T value) => new() { Path = path, Value = JsonSerializer.SerializeToElement(value) };
    private sealed class HandlerFactory(WebApplicationFactory<GitExtension::Program> extension, WebApplicationFactory<GitHubExtension::Program>? github = null) : IHttpMessageHandlerFactory
    {
        internal byte[]? SeedContent { get; set; }
        public HttpMessageHandler CreateHandler(string name) => name == "workspace-seed-ingestion" && SeedContent is { } seed
            ? new SeedContentHandler(seed) : github is null ? extension.Server.CreateHandler() : new CombinedExtensionHandler(extension.Server.CreateHandler(), github.Server.CreateHandler());
    }
    private sealed class SeedContentHandler(byte[] bytes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.AreEqual("https://seed.example/content.zip", request.RequestUri!.AbsoluteUri);
            var content = new ByteArrayContent(bytes); content.Headers.ContentType = new("application/zip");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }
    private sealed class SharingHost(WebApplicationFactory<global::Program> host, WebApplicationFactory<GitExtension::Program> extension,
        HttpClient http, RequestContext context, ToolResource tool, Func<WebApplicationFactory<global::Program>> hostFactory) : IAsyncDisposable
    {
        public WebApplicationFactory<global::Program> Host { get; private set; } = host;
        public HttpClient Http { get; private set; } = http;
        public RequestContext Context { get; } = context;
        public ToolResource Tool { get; } = tool;
        public string Root(Guid id) => $"/api/workspaces/{Context.WorkspaceId}/projects/{id}";
        public IDisposable Scope() => Host.Services.GetRequiredService<IRequestContextScopeFactory>().Push(Context);
        public async Task RestartAsync()
        {
            Http.Dispose(); await Host.DisposeAsync();
            // The extension journal remains durable and is read by the same provider after reconnect.
            Host = hostFactory(); Http = Host.CreateClient();
        }
        public async ValueTask DisposeAsync() { Http.Dispose(); await Host.DisposeAsync(); await extension.DisposeAsync(); Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); }
    }
}
