extern alias GitHubExtension;

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Agentstration.Coding.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Secrets;
using Agentstration.Secrets.Abstractions;
using Agentstration.Tools;
using Agentstration.Work;
using Agentstration.Work.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using GitHub = GitHubExtension::Agentstration.Extensions.GitHub;

namespace Agentstration.Web.Tests;

public sealed partial class GitHubProviderTests
{
    [TestMethod]
    public async Task GitHubAepProtectedProjectRelaysResolveRepositoriesAndRecoverIssueAndPullRequestAcrossHostRestart()
    {
        using var fixture = new Fixture();
        var hostDirectory = Path.Combine(fixture.Directory, "host");
        await using var extension = new WebApplicationFactory<GitHubExtension::Program>().WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.Configure<GitHub.GitHubProviderOptions>(options => options.StateDirectory = Path.Combine(fixture.Directory, "provider"));
            services.AddHttpClient<GitHub.GitHubConnection>().ConfigurePrimaryHttpMessageHandler(() => fixture.Native);
        }));
        _ = extension.CreateClient();
        var projectId = Guid.NewGuid(); var parent = Guid.NewGuid(); var leaf = Guid.NewGuid(); var operation = Guid.NewGuid();
        var prefix = $"/api/workspaces/default/projects/{projectId}";
        InvokeCodingPullRequestRequest pullCommand;
        await using (var host = CodingFactory(hostDirectory, extension))
        {
            using var http = host.CreateClient();
            var context = await host.Services.GetRequiredService<ILocalEnvironmentBootstrapper>().EnsureInitializedAsync(default);
            using var scope = host.Services.GetRequiredService<IRequestContextScopeFactory>().Push(context);
            var (tracker, repository) = await InstallGitHubRelaysAsync(host.Services, context);
            using var tree = await http.PostAsJsonAsync("/api/workspaces/default/tasks/tree", new CreateWorkTaskTreeRequest(parent,
                [new(parent, WorkTaskKind.Composite, "Delivery", "Composite work"), new(leaf, WorkTaskKind.Atomic, "Implement", "Keep child internal", parent)]));
            Assert.AreEqual(HttpStatusCode.Created, tree.StatusCode);
            using var project = await http.PostAsJsonAsync("/api/workspaces/default/projects", new CreateWorkProjectRequest(projectId, Guid.NewGuid(), "github",
                new("GitHub fixture", null, [], [parent]) { Bindings = [new("tracker", WorkProjectBindingPurpose.TaskOperations, Pin(tracker)),
                    new("repository", WorkProjectBindingPurpose.Repository, Pin(repository))] }));
            Assert.AreEqual(HttpStatusCode.Created, project.StatusCode, await project.Content.ReadAsStringAsync());
            using var resolution = await http.PostAsJsonAsync(prefix + "/coding/repositories/resolve", new ResolveCodingRepositoriesRequest(leaf, [Repository], 1));
            Assert.AreEqual(HttpStatusCode.OK, resolution.StatusCode, await resolution.Content.ReadAsStringAsync());
            var selected = (await resolution.Content.ReadFromJsonAsync<CodingRepositorySelection>())!;
            Assert.AreEqual("42", selected.Repositories.Single().NativeId); Assert.AreEqual(Pin(repository), selected.Connection);
            using var ambiguous = await http.PostAsJsonAsync(prefix + "/coding/repositories/resolve", new ResolveCodingRepositoriesRequest(leaf, [Repository, "fixture/second"], 1));
            Assert.AreEqual(HttpStatusCode.BadRequest, ambiguous.StatusCode);
            using var composite = await http.PostAsJsonAsync(prefix + "/coding/repositories/resolve", new ResolveCodingRepositoriesRequest(parent, [Repository, "fixture/second"], 1));
            Assert.AreEqual(HttpStatusCode.OK, composite.StatusCode, await composite.Content.ReadAsStringAsync());
            using var foreign = await http.PostAsJsonAsync($"/api/workspaces/{Guid.NewGuid()}/projects/{projectId}/coding/repositories/resolve", new ResolveCodingRepositoriesRequest(leaf, [Repository], 1));
            Assert.IsTrue(foreign.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound);
            fixture.Native.LoseIssueResponse = true;
            using var publication = await http.PostAsJsonAsync(prefix + "/task-operations", new BeginWorkTaskOperationRequest(operation, 1, "tracker",
                new(ExternalTaskOperationKind.Publish, Repository, Title: "Composite publication")));
            Assert.AreEqual(HttpStatusCode.Accepted, publication.StatusCode, await publication.Content.ReadAsStringAsync());
            Assert.AreEqual(WorkTaskOperationStatus.Uncertain, (await publication.Content.ReadFromJsonAsync<WorkTaskOperationResponse>())!.Status);
            fixture.Native.LosePullResponse = true;
            pullCommand = new(Guid.NewGuid(), leaf, 1, CodingProviderProtocol.CreatePullRequest,
                new(Repository, Title: "Deliver fixture", Head: "agentstration/task", Base: "main", ExpectedHeadRevision: HeadRevision), selected.Connection, selected.ProjectVersion);
            using var pull = await http.PostAsJsonAsync(prefix + "/coding/pull-requests", pullCommand);
            Assert.AreEqual(HttpStatusCode.Accepted, pull.StatusCode, await pull.Content.ReadAsStringAsync());
            Assert.AreEqual(CodingProviderStatus.Uncertain, (await pull.Content.ReadFromJsonAsync<CodingPullRequestResponse>())!.Result.Status);
            var exported = ResourceManifestSerializer.ToJson((await host.Services.GetRequiredService<ToolDefinitionService>().ListVersionsAsync("github-repository", default, default)).Single());
            Assert.IsFalse(exported.Contains(Credential, StringComparison.Ordinal));
        }
        await using (var restarted = CodingFactory(hostDirectory, extension))
        {
            using var http = restarted.CreateClient();
            using var resume = await http.PostAsync(prefix + $"/task-operations/{operation}/resume", null);
            Assert.AreEqual(HttpStatusCode.Accepted, resume.StatusCode, await resume.Content.ReadAsStringAsync());
            var receipt = (await resume.Content.ReadFromJsonAsync<WorkTaskOperationResponse>())!;
            Assert.AreEqual(WorkTaskOperationStatus.Succeeded, receipt.Status, receipt.ErrorCode); Assert.AreEqual("1", receipt.Task!.TaskId);
            using var pull = await http.PostAsJsonAsync(prefix + "/coding/pull-requests", pullCommand with { Recover = true });
            Assert.AreEqual(HttpStatusCode.Accepted, pull.StatusCode, await pull.Content.ReadAsStringAsync());
            var result = (await pull.Content.ReadFromJsonAsync<CodingPullRequestResponse>())!;
            Assert.AreEqual(CodingProviderStatus.Succeeded, result.Result.Status, result.Result.ErrorCode);
            Assert.AreEqual(1, fixture.Native.IssuePosts); Assert.AreEqual(1, fixture.Native.PullPosts);
            var tree = (await http.GetFromJsonAsync<WorkTaskTreeResponse>($"/api/workspaces/default/tasks/{parent}/tree"))!;
            Assert.IsTrue(tree.Tasks.All(task => task.Status == WorkItemStatus.Pending));
            Assert.HasCount(1, fixture.Native.Issues);
            using var openApi = await http.GetAsync("/openapi/v1.json");
            Assert.AreEqual(HttpStatusCode.OK, openApi.StatusCode);
            var specification = (await openApi.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("paths");
            var documented = specification.GetProperty("/api/workspaces/{workspaceName}/projects/{projectId}/coding/repositories/resolve")
                .GetProperty("post").GetProperty("responses").GetProperty("200").GetProperty("content").GetProperty("application/json").GetProperty("schema");
            Assert.IsTrue(documented.GetProperty("properties").TryGetProperty("repositories", out _));
            Assert.IsTrue(specification.GetProperty("/api/workspaces/{workspaceName}/projects/{projectId}/coding/pull-requests")
                .GetProperty("post").GetProperty("responses").TryGetProperty("202", out _));
            using var staleHead = await http.PostAsJsonAsync(prefix + "/coding/pull-requests", pullCommand with
                { Id = Guid.NewGuid(), Input = pullCommand.Input with { ExpectedHeadRevision = new string('b', 40) } });
            Assert.AreEqual(HttpStatusCode.Conflict, staleHead.StatusCode, await staleHead.Content.ReadAsStringAsync());
            var context = await restarted.Services.GetRequiredService<ILocalEnvironmentBootstrapper>().EnsureInitializedAsync(default);
            using var scope = restarted.Services.GetRequiredService<IRequestContextScopeFactory>().Push(context);
            using (var denied = new SecretValue(Encoding.UTF8.GetBytes("public-disposable-synthetic-denied-github-fixture")))
                await restarted.Services.GetRequiredService<SecretManagementService>().SetValueExactAsync(ResourceScopeRef.Workspace(context.WorkspaceId), "github-token", denied, default);
            using var forbidden = await http.PostAsJsonAsync(prefix + "/coding/repositories/resolve", new ResolveCodingRepositoriesRequest(leaf, [Repository], 1));
            Assert.AreEqual(HttpStatusCode.Forbidden, forbidden.StatusCode, await forbidden.Content.ReadAsStringAsync());
            Assert.IsFalse((await forbidden.Content.ReadAsStringAsync()).Contains(Credential, StringComparison.Ordinal));
            Assert.AreEqual(1, fixture.Native.IssuePosts); Assert.AreEqual(1, fixture.Native.PullPosts);
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    private static WorkProjectResourceReference Pin(ToolResource tool) => new(ScopedResourceAddress.Create(tool.ScopeRef!.Value, tool.Namespace, tool.Kind, tool.Name), tool.Uid, tool.Generation);
    private static WebApplicationFactory<global::Program> CodingFactory(string directory, WebApplicationFactory<GitHubExtension::Program> extension) =>
        new WebApplicationFactory<global::Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development"); builder.UseSetting("Data:Directory", directory);
            builder.UseSetting("Agentstration:Authentication:Mode", "Development"); builder.UseSetting("AI:Provider", "Deterministic");
            builder.UseSetting("Agentstration:Extensions:DiscoverOnStartup", "false"); builder.UseSetting("Logging:LogLevel:Default", "Warning");
            builder.ConfigureTestServices(services => services.AddSingleton<IHttpMessageHandlerFactory>(new ExtensionHandlers(extension)));
        });
    private sealed class ExtensionHandlers(WebApplicationFactory<GitHubExtension::Program> extension) : IHttpMessageHandlerFactory
    { public HttpMessageHandler CreateHandler(string name) => extension.Server.CreateHandler(); }

    private static async Task<(ToolResource Tracker, ToolResource Repository)> InstallGitHubRelaysAsync(IServiceProvider services, RequestContext context)
    {
        var scope = ResourceScopeRef.Workspace(context.WorkspaceId); var store = services.GetRequiredService<IResourceStore>();
        await store.PutExactAsync(scope, new Agentstration.Extensions.Contracts.ExtensionRegistrationResource
        {
            ApiVersion = ResourceApiVersions.CoreV1, Kind = Agentstration.Extensions.Contracts.ExtensionKinds.ExtensionRegistration, Metadata = new() { Name = "github-fixture" }, ScopeRef = scope,
            Definition = new() { DisplayName = "GitHub fixture", Endpoint = new("http://github-fixture/"), ExpectedExtensionId = "Agentstration.Extensions.GitHub", Enabled = true,
                AuthenticationMode = Agentstration.Extensions.Contracts.AepTransportAuthenticationMode.None, EnrollmentMode = Agentstration.Extensions.Contracts.AepEnrollmentMode.PairingCode }
        }, null, true, default);
        var tools = services.GetRequiredService<ToolManagementService>();
        await tools.PutProviderAsync(new ToolProviderResource
        {
            ApiVersion = ResourceApiVersions.CoreV1, Kind = ToolResourceKinds.ToolProvider, Metadata = new() { Name = "github-fixture" }, ScopeRef = scope,
            Definition = new() { DisplayName = "GitHub fixture", ProviderType = ToolProviderType.Aep, Aep = new() { Extension = new("github-fixture", scope, default) } }
        }, null, true, default);
        await tools.RefreshDiscoveryExactAsync(scope, "github-fixture", default, default);
        var sources = (await tools.ListVisibleToolsAsync(scope, default)).Where(value => value.Value.Definition.Provider?.Name == "github-fixture").ToArray();
        foreach (var source in sources) await tools.SetToolEnabledAsync(source.Value.Name, true, source.ETag, default);
        var secrets = services.GetRequiredService<SecretManagementService>();
        await secrets.CreateVaultAsync(new VaultResource { ApiVersion = ResourceApiVersions.CoreV1, Kind = SecretResourceKinds.Vault, Metadata = new() { Name = "github-vault" }, ScopeRef = scope,
            Definition = new() { DisplayName = "GitHub vault", ProviderType = "local" } }, default);
        await secrets.InitializeVaultExactAsync(scope, "github-vault", default);
        var secret = await secrets.CreateSecretAsync(new SecretResource { ApiVersion = ResourceApiVersions.CoreV1, Kind = SecretResourceKinds.Secret, Metadata = new() { Name = "github-token" }, ScopeRef = scope,
            Definition = new() { DisplayName = "GitHub fixture token", Vault = new("github-vault", scope), Key = "token" } }, default);
        using (var value = new SecretValue(Encoding.UTF8.GetBytes(Credential))) await secrets.SetValueExactAsync(scope, secret.Value.Name, value, default);
        var result = new List<ToolResource>();
        foreach (var (id, name, capability) in new[] { ("github.issue", "github-tracker", WorkTaskProviderProtocol.MetadataKey), ("github.coding", "github-repository", CodingProviderProtocol.MetadataKey) })
        {
            var source = (await tools.ListVisibleToolsAsync(scope, default)).Single(value => value.Value.Definition.ExternalId == id).Value;
            var bindings = new List<ToolArgumentBinding>
            {
                new SecretToolArgumentBinding { Path = "/credential", Secret = new(secret.Value.Address, scope) },
                new LiteralToolArgumentBinding { Path = "/apiBaseUrl", Value = JsonSerializer.SerializeToElement("https://api.github.com") },
                new LiteralToolArgumentBinding { Path = "/allowedRepositories", Value = JsonSerializer.SerializeToElement(Allowed, Json) }
            };
            if (id == "github.issue") bindings.Add(new LiteralToolArgumentBinding { Path = "/managedLabelPrefix", Value = JsonSerializer.SerializeToElement("agentstration:") });
            await services.GetRequiredService<ToolDefinitionService>().PutAsync(new ToolDefinitionResource
            {
                ApiVersion = ResourceApiVersions.CoreV1, Kind = ToolResourceKinds.ToolDefinition, Metadata = new() { Name = name }, ScopeRef = scope,
                Definition = new RelayToolDefinitionProperties { DisplayName = name, Publish = true, Version = "1.0.0", Bindings = bindings,
                    Metadata = source.Definition.Metadata.Where(pair => pair.Key == capability || pair.Key == CodingIssueProtocol.MetadataKey).ToDictionary(pair => pair.Key, pair => pair.Value),
                    Target = new() { Name = source.Name, Namespace = source.Namespace, ScopeRef = scope, Uid = source.Uid, Generation = source.Generation } }
            }, null, true, default);
            result.Add((await store.GetExactAsync<ToolResource>(ScopedResourceAddress.Create(scope, default, ToolResourceKinds.Tool, "agentstration." + name + ".v1.0.0"), default))!.Value);
        }
        return (result[0], result[1]);
    }
}
