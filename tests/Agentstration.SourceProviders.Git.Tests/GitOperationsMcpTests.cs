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
            ["operation"] = "read",
            ["mode"] = "execute",
            ["operationKey"] = new string('a', 64),
            ["intentDigest"] = new string('b', 64),
            ["request"] = new GitRepositoryInput("refs/heads/main", repository.InitialRevision, Paths: ["shared/plan.json"]),
            ["repository"] = repository.Repository,
            ["authentication"] = "Basic",
            ["username"] = "fixture",
            ["credential"] = AuthenticatedGitFixture.Credential,
            ["allowedRefPrefixes"] = new[] { "refs/heads/main" },
            ["allowedPathPrefixes"] = new[] { "shared" }
        });
        Assert.IsFalse(result.IsError == true); Assert.IsNotNull(result.StructuredContent);
        var response = JsonSerializer.SerializeToElement(result.StructuredContent).Deserialize<GitRepositoryResponse>(Json)!;
        Assert.AreEqual("Succeeded", response.Status, response.ErrorCode); Assert.AreEqual(repository.InitialRevision, response.Revision);
        Assert.AreEqual("shared/plan.json", response.Files!.Single().Path);
    }
    [TestMethod]
    public async Task GitConnectedServiceConfiguresNativeMcpWithoutBusinessArgumentsAndRejectsOverrides()
    {
        await using var repository = await AuthenticatedGitFixture.CreateAsync();
        await using var extension = new WebApplicationFactory<global::Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("GitOperations:Enabled", "true"); builder.UseSetting("GitOperations:AllowedRepositories:0", repository.Repository);
            builder.UseSetting("GitOperations:AllowHttpLoopback", "true"); builder.UseSetting("GitOperations:StateDirectory", Path.Combine(repository.Root, "service-journal"));
            builder.UseSetting("GitSourceProvider:TemporaryDirectory", Path.Combine(repository.Root, "service-work"));
        });
        using var http = extension.CreateClient();
        var manifest = await new AepClient(http).GetManifestAsync();
        Assert.AreEqual(GitServiceContract.TypeId, manifest.Contributions.ConnectedServiceTypes!.Single().Id);
        var envelope = new GitServiceEnvelope(GitServiceContract.TypeId, GitServiceContract.Version, GitServiceContract.TypeVersion.SchemaDigest,
            JsonSerializer.SerializeToElement(new GitServiceOptions(repository.Repository, "Basic", "fixture", ["refs/heads/main"], ["shared"], "refs/heads/main"), Json),
            new Dictionary<string, string> { ["credential"] = AuthenticatedGitFixture.Credential });
        http.DefaultRequestHeaders.Add(GitServiceContract.Header, Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(envelope, Json)));
        var transport = new HttpClientTransport(new() { Endpoint = new Uri(http.BaseAddress!, "mcp") }, http, NullLoggerFactory.Instance, ownsHttpClient: false);
        await using var mcp = await McpClient.CreateAsync(transport, loggerFactory: NullLoggerFactory.Instance);
        var arguments = new Dictionary<string, object?>
        {
            ["operation"] = "read",
            ["mode"] = "execute",
            ["operationKey"] = new string('a', 64),
            ["intentDigest"] = new string('b', 64),
            ["request"] = new GitRepositoryInput("refs/heads/main", repository.InitialRevision, Paths: ["shared/plan.json"])
        };
        var result = await mcp.CallToolAsync("git_repository_operation", arguments);
        Assert.IsFalse(result.IsError == true, JsonSerializer.Serialize(result, Json));
        var response = JsonSerializer.SerializeToElement(result.StructuredContent).Deserialize<GitRepositoryResponse>(Json)!;
        Assert.AreEqual("Succeeded", response.Status, response.ErrorCode); Assert.AreEqual(repository.InitialRevision, response.Revision);
        arguments["repository"] = repository.Repository;
        Assert.IsTrue((await mcp.CallToolAsync("git_repository_operation", arguments)).IsError == true);
        arguments.Remove("repository"); arguments["request"] = new GitRepositoryInput("refs/heads/other", repository.InitialRevision, Paths: ["shared/plan.json"]);
        Assert.IsTrue((await mcp.CallToolAsync("git_repository_operation", arguments)).IsError == true);
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
