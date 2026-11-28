extern alias GitExtension;

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Agentstration.Testing;
using Agentstration.Extensions.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Secrets;
using Agentstration.Secrets.Abstractions;
using Agentstration.Tools;
using Agentstration.Tools.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class ProtectedGitRelayApiTests
{
    [TestMethod]
    public async Task PublishedGitRelayUsesGovernedSecretAndRejectsCredentialOverrideAndForeignWorkspace()
    {
        await using var native = await AuthenticatedGitFixture.CreateAsync();
        await using var extension = new WebApplicationFactory<GitExtension::Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("GitOperations:Enabled", "true"); builder.UseSetting("GitOperations:AllowedRepositories:0", native.Repository);
            builder.UseSetting("GitOperations:AllowHttpLoopback", "true"); builder.UseSetting("GitOperations:StateDirectory", Path.Combine(native.Root, "journal"));
            builder.UseSetting("GitSourceProvider:TemporaryDirectory", Path.Combine(native.Root, "extension-work"));
        });
        _ = extension.CreateClient();
        await using var host = new WebApplicationFactory<global::Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development"); builder.UseSetting("Data:Directory", Path.Combine(native.Root, "host"));
            builder.UseSetting("Agentstration:Authentication:Mode", "Development"); builder.UseSetting("AI:Provider", "Deterministic");
            builder.UseSetting("Agentstration:Extensions:DiscoverOnStartup", "false"); builder.UseSetting("Logging:LogLevel:Default", "Warning");
            builder.ConfigureTestServices(services => services.AddSingleton<IHttpMessageHandlerFactory>(new HandlerFactory(extension)));
        });
        using var http = host.CreateClient();
        var context = await host.Services.GetRequiredService<ILocalEnvironmentBootstrapper>().EnsureInitializedAsync(default);
        using var scope = host.Services.GetRequiredService<IRequestContextScopeFactory>().Push(context);
        var owner = ResourceScopeRef.Workspace(context.WorkspaceId); var store = host.Services.GetRequiredService<IResourceStore>();
        await store.PutExactAsync(owner, new ExtensionRegistrationResource
        {
            ApiVersion = ResourceApiVersions.CoreV1, Kind = ExtensionKinds.ExtensionRegistration, Metadata = new() { Name = "git-fixture" }, ScopeRef = owner,
            Definition = new() { DisplayName = "Git fixture", Endpoint = new("http://git-fixture/"), ExpectedExtensionId = "Agentstration.Extensions.Git", Enabled = true,
                AuthenticationMode = AepTransportAuthenticationMode.None, EnrollmentMode = AepEnrollmentMode.PairingCode }
        }, null, true, default);
        var tools = host.Services.GetRequiredService<ToolManagementService>();
        await tools.PutProviderAsync(new ToolProviderResource
        {
            ApiVersion = ResourceApiVersions.CoreV1, Kind = ToolResourceKinds.ToolProvider, Metadata = new() { Name = "git-fixture" }, ScopeRef = owner,
            Definition = new() { DisplayName = "Git fixture", ProviderType = ToolProviderType.Aep, Aep = new() { Extension = new("git-fixture", owner, default) } }
        }, null, true, default);
        await tools.RefreshDiscoveryExactAsync(owner, "git-fixture", default, default);
        var discovered = (await tools.ListVisibleToolsAsync(owner, default)).Single(value => value.Value.Definition.ExternalId == "git.repository");
        await tools.SetToolEnabledAsync(discovered.Value.Name, true, (await tools.GetToolOperationalStateAsync(discovered.Value, default))?.ETag, default);
        var source = (await tools.ListVisibleToolsAsync(owner, default)).Single(value => value.Value.Definition.ExternalId == "git.repository").Value;
        var secrets = host.Services.GetRequiredService<SecretManagementService>();
        await secrets.CreateVaultAsync(new VaultResource { ApiVersion = ResourceApiVersions.CoreV1, Kind = SecretResourceKinds.Vault, Metadata = new() { Name = "git-vault" }, ScopeRef = owner,
            Definition = new() { DisplayName = "Git vault", ProviderType = "local" } }, default);
        await secrets.InitializeVaultExactAsync(owner, "git-vault", default);
        var secret = await secrets.CreateSecretAsync(new SecretResource { ApiVersion = ResourceApiVersions.CoreV1, Kind = SecretResourceKinds.Secret, Metadata = new() { Name = "git-credential" }, ScopeRef = owner,
            Definition = new() { DisplayName = "Git fixture credential", Vault = new("git-vault", owner), Key = "credential" } }, default);
        using (var value = new SecretValue(Encoding.UTF8.GetBytes(AuthenticatedGitFixture.Credential))) await secrets.SetValueExactAsync(owner, secret.Value.Name, value, default);
        const string capability = "agentstration.git.repository";
        var definitions = host.Services.GetRequiredService<ToolDefinitionService>();
        await definitions.PutAsync(new ToolDefinitionResource
        {
            ApiVersion = ResourceApiVersions.CoreV1, Kind = ToolResourceKinds.ToolDefinition, Metadata = new() { Name = "git-connection" }, ScopeRef = owner,
            Definition = new RelayToolDefinitionProperties
            {
                DisplayName = "Git connection", Version = "1.0.0", Publish = true,
                Target = new() { Name = source.Name, Namespace = source.Namespace, ScopeRef = owner, Uid = source.Uid, Generation = source.Generation },
                Metadata = new Dictionary<string, JsonElement> { [capability] = source.Definition.Metadata[capability] },
                Bindings = [new SecretToolArgumentBinding { Path = "/credential", Secret = new(secret.Value.Address, owner) },
                    Literal("/repository", native.Repository), Literal("/authentication", "Basic"), Literal("/username", "fixture"),
                    Literal("/allowedRefPrefixes", new[] { "refs/heads/main" }), Literal("/allowedPathPrefixes", new[] { "shared" })]
            }
        }, null, true, default);
        var publication = (await definitions.ListVersionsAsync("git-connection", default, default)).Single();
        Assert.IsFalse(publication.Definition.InputSchema.GetProperty("properties").TryGetProperty("credential", out _));
        Assert.IsFalse(ResourceManifestSerializer.ToJson(publication).Contains(AuthenticatedGitFixture.Credential, StringComparison.Ordinal));
        var arguments = JsonSerializer.SerializeToElement(new { operation = "publish", mode = "execute", operationKey = new string('a', 64), intentDigest = new string('b', 64),
            request = new { refName = "refs/heads/main", baseRevision = native.InitialRevision, expectedRefRevision = native.InitialRevision,
                changes = new[] { new { path = "shared/plan.json", contentBase64 = Convert.ToBase64String("{\"revision\":2}"u8) } }, message = "Publish governed fixture" } });
        const string route = "/api/tools/agentstration.git-connection.v1.0.0/run";
        using var run = await http.PostAsJsonAsync(route, new RunToolRequest(arguments, ToolRunMode.Execute));
        Assert.AreEqual(HttpStatusCode.OK, run.StatusCode, await run.Content.ReadAsStringAsync());
        var result = (await run.Content.ReadFromJsonAsync<RunToolResponse>())!;
        Assert.AreEqual("Succeeded", result.Output!.Value.GetProperty("structuredContent").GetProperty("status").GetString());
        var revision = (await native.GitAsync(native.RepositoryPath, "rev-parse", "main")).Trim();
        var overridden = System.Text.Json.Nodes.JsonNode.Parse(arguments.GetRawText())!; overridden["credential"] = "override";
        using var rejected = await http.PostAsJsonAsync(route, new RunToolRequest(JsonSerializer.SerializeToElement(overridden), ToolRunMode.Execute));
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, rejected.StatusCode);
        http.DefaultRequestHeaders.Add("X-Agentstration-Context", ResourceScopeRef.Workspace(Guid.NewGuid()).Value);
        using var foreign = await http.PostAsJsonAsync(route, new RunToolRequest(arguments, ToolRunMode.Execute));
        Assert.IsTrue(foreign.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound);
        Assert.AreEqual(revision, (await native.GitAsync(native.RepositoryPath, "rev-parse", "main")).Trim());
        await host.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }
    private static LiteralToolArgumentBinding Literal<T>(string path, T value) => new() { Path = path, Value = JsonSerializer.SerializeToElement(value) };
    private sealed class HandlerFactory(WebApplicationFactory<GitExtension::Program> extension) : IHttpMessageHandlerFactory
    { public HttpMessageHandler CreateHandler(string name) => extension.Server.CreateHandler(); }
}
