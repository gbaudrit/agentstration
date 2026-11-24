using System.Text.Json;
using Agentstration.Testing;
using Agentstration.Aep.Client;
using Agentstration.Extensions.Git;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;

namespace Agentstration.SourceProviders.Git.Tests;

[TestClass]
public sealed class GitOperationsMcpTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    [TestMethod]
    public async Task ExistingGitAepExtensionAdvertisesSeparateOperationsAndInvokesAuthenticatedNativeGitThroughMcp()
    {
        await using var repository = await AuthenticatedGitFixture.CreateAsync();
        await using var extension = new WebApplicationFactory<global::Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("GitOperations:Enabled", "true"); builder.UseSetting("GitOperations:AllowedRepositories:0", repository.Repository);
            builder.UseSetting("GitOperations:AllowHttpLoopback", "true"); builder.UseSetting("GitOperations:StateDirectory", Path.Combine(repository.Root, "mcp-journal"));
            builder.UseSetting("GitSourceProvider:TemporaryDirectory", Path.Combine(repository.Root, "mcp-work"));
        });
        using var http = extension.CreateClient();
        var manifest = await new AepClient(http).GetManifestAsync();
        Assert.AreEqual("git", manifest.Contributions.SourceProviders!.Single().Id);
        var contribution = manifest.Contributions.Tools!.Single(); Assert.AreEqual("git.repository", contribution.Id);
        Assert.AreEqual(GitOperationProtocol.Version, contribution.Metadata![GitOperationProtocol.MetadataKey].GetProperty("version").GetString());
        var transport = new HttpClientTransport(new() { Endpoint = new Uri(http.BaseAddress!, "mcp") }, http, NullLoggerFactory.Instance, ownsHttpClient: false);
        await using var mcp = await McpClient.CreateAsync(transport, loggerFactory: NullLoggerFactory.Instance);
        var tools = await mcp.ListToolsAsync(); Assert.AreEqual("git_repository_operation", tools.Single().Name);
        var result = await mcp.CallToolAsync("git_repository_operation", new Dictionary<string, object?>
        {
            ["operation"] = "read", ["mode"] = "execute", ["operationKey"] = new string('a', 64), ["intentDigest"] = new string('b', 64),
            ["request"] = new GitRepositoryInput("refs/heads/main", repository.InitialRevision, Paths: ["shared/plan.json"]),
            ["repository"] = repository.Repository, ["authentication"] = "Basic", ["username"] = "fixture", ["credential"] = AuthenticatedGitFixture.Credential,
            ["allowedRefPrefixes"] = new[] { "refs/heads/main" }, ["allowedPathPrefixes"] = new[] { "shared" }
        });
        Assert.IsFalse(result.IsError == true); Assert.IsNotNull(result.StructuredContent);
        var response = JsonSerializer.SerializeToElement(result.StructuredContent).Deserialize<GitRepositoryResponse>(Json)!;
        Assert.AreEqual("Succeeded", response.Status, response.ErrorCode); Assert.AreEqual(repository.InitialRevision, response.Revision);
        Assert.AreEqual("shared/plan.json", response.Files!.Single().Path);
    }
    [TestMethod]
    public async Task EnabledGitOperationsRejectInvalidProtectedRepositoryConfigurationAtStartup()
    {
        await using var extension = new WebApplicationFactory<global::Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("GitOperations:Enabled", "true");
            builder.UseSetting("GitOperations:AllowedRepositories:0", "https://fixture:disposable@example.invalid/repository.git");
        });
        await Assert.ThrowsAsync<Microsoft.Extensions.Options.OptionsValidationException>(async () =>
        {
            using var http = extension.CreateClient(); await http.GetAsync("/.well-known/aep");
        });
    }

}
