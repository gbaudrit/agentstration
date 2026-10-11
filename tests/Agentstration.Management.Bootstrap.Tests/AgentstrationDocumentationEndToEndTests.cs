extern alias crawl4ai;
extern alias awpworker;

using System.Text;
using System.Text.Json;
using Agentstration.Agents;
using Agentstration.Awp.Abstractions;
using Agentstration.DataSources;
using Agentstration.DataSources.Contracts;
using Agentstration.Extensions;
using Agentstration.Flows;
using Agentstration.Flows.Application;
using Agentstration.Identity.Contracts;
using Agentstration.Identity;
using Agentstration.Infrastructure.Assistant;
using Agentstration.Infrastructure.Bootstrap;
using Agentstration.Knowledge;
using Agentstration.Knowledge.Contracts;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;
using Agentstration.Security.AspNetCoreIdentity;
using Agentstration.Secrets.Abstractions;
using Agentstration.Tools;
using Agentstration.Web.Hosting;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Crawl4AiDataSourceProfileBundle = crawl4ai::Agentstration.Extensions.Crawl4AI.Crawl4AiDataSourceProfileBundle;
using AwpWorker = awpworker::Agentstration.Runtime.Worker.MicrosoftAgentFramework;

namespace Agentstration.Management.Tests;

[TestClass]
public sealed class AgentstrationDocumentationEndToEndTests
{
    private const string Password = "Initial123!Password";
    private static readonly byte[] FixtureCorpus = Encoding.UTF8.GetBytes("""
        # Agentstration documentation

        Source: https://docs.agentstration.io/concepts/workspaces

        A Workspace is the governed ownership and execution boundary for Agents, Flows, Tools, and Knowledge Sources.
        """);

    [TestMethod]
    public async Task ProfileAcquiresStoresPublishesAndRetrievesTheOfflineFixture()
    {
        using var catalog = new TemporaryCatalog();
        await catalog.WriteInitialProfileAsync();
        await catalog.WriteSupportProfileAsync();
        catalog.CopyDocumentationProfile(FindRepositoryRoot());
        await using var factory = Factory(catalog.Root);
        using var client = factory.CreateClient();
        using var health = await client.GetAsync("/health");
        health.EnsureSuccessStatusCode();

        await using var scope = factory.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var account = await services.GetRequiredService<UserManager<LocalIdentityUser>>()
            .FindByNameAsync("bootstrap-admin");
        Assert.IsNotNull(account);
        var principal = await services.GetRequiredService<IPrincipalResolver>().ResolveLocalAsync(account.Id, default);
        Assert.IsNotNull(principal);
        var identities = services.GetRequiredService<IIdentityStore>();
        var tenant = await identities.FindTenantByNameAsync("dev", default);
        Assert.IsNotNull(tenant);
        var workspace = await identities.FindWorkspaceByNameAsync(tenant.Id, "default", default);
        Assert.IsNotNull(workspace);
        var context = new RequestContext(principal.Id, tenant.Id, workspace.Id);
        using var requestScope = services.GetRequiredService<IRequestContextScopeFactory>().Push(context);
        _ = await services.GetRequiredService<ExtensionSourceDiscoveryService>().DiscoverAsync(default);

        var target = new BootstrapApplicationTarget(tenant.Id, workspace.Id);
        var management = services.GetRequiredService<BootstrapProfileManagementService>();
        var toolBindings = new Dictionary<string, ResourceReference>
        {
            ["web.crawl"] = ToolReference(FixtureCrawlTool.Name, workspace.Id),
            ["content.read"] = ToolReference(FixtureReadTool.Name, workspace.Id),
            ["content.delete"] = ToolReference(FixtureDeleteTool.Name, workspace.Id)
        };
        _ = await services.GetRequiredService<AepDataSourceProfileBundleInstaller>().InstallAsync(
            Crawl4AiDataSourceProfileBundle.Create(), target, toolBindings, default);

        await ApplyAsync(management, new(["agentstration-documentation"], target), principal.Id);

        var acquisitions = services.GetRequiredService<DataSourceAcquisitionService>();
        var sourceScope = ResourceScopeRef.Workspace(workspace.Id);
        var started = await acquisitions.StartAsync(ResourceNamespace.Default, "agentstration-documentation", sourceScope,
            JsonSerializer.SerializeToElement(new { }), "documentation-fixture", "documentation-fixture", default);
        using var worker = await AwpTestWorker.CreateAsync(factory, services);
        await worker.ExecuteAsync(started.Value.FlowRunId);
        var completed = await acquisitions.GetAsync(ResourceNamespace.Default, started.Value.Name, default);
        Assert.AreEqual(DataSourceAcquisitionState.Succeeded, completed.Value.State, completed.Value.ErrorMessage);
        var artifact = completed.Value.Manifest?.Artifacts.Single();
        Assert.IsNotNull(artifact);
        Assert.AreEqual(DataSourceArtifactDisposition.Publishable, artifact.Disposition);

        var projectionTask = services.GetRequiredService<KnowledgeProjectionService>().StartAsync(
            new("agentstration-documentation"),
            new StartKnowledgeProjectionRequest
            {
                AcquisitionIds = new Dictionary<string, string> { ["documentation"] = completed.Value.Name },
                CorrelationId = "documentation-fixture-projection"
            }, default);
        var projection = await worker.AwaitAsync(projectionTask);
        Assert.AreEqual(KnowledgeProjectionState.Succeeded, projection.Value.State, projection.Value.ErrorMessage);
        Assert.IsNotNull(projection.Value.SnapshotName);
        _ = await services.GetRequiredService<KnowledgeSourceToolExposureService>().PublishAsync(
            new("agentstration-documentation"), "1.0.0", false, default);

        var toolExecution = services.GetRequiredService<IToolDefinitionExecutor>().ExecuteAsync(
            new ToolDefinitionInvocation(
                tenant.Id,
                new WorkspaceId(workspace.Id),
                principal.Id,
                ResourceNamespace.Default,
                "agentstration-documentation.search",
                "documentation-search-call",
                "documentation-search-correlation",
                JsonSerializer.SerializeToElement(new { request = new { query = "Workspace", limit = 5 } }),
                ToolDefinitionCallerKind.Agent,
                "agentstration-documentation-assistant"), default);
        var toolResult = await worker.AwaitAsync(toolExecution);
        StringAssert.Contains(toolResult.Output!.Value.GetRawText(), "governed ownership");

        var legacyExecution = services.GetRequiredService<AssistantDocumentationMcpTool>().ExecuteAsync(
            new InternalMcpToolInvocation(
                tenant.Id,
                new WorkspaceId(workspace.Id),
                principal.Id,
                "legacy-documentation-search",
                "legacy-documentation-correlation",
                JsonSerializer.SerializeToElement(new { query = "Workspace", maximumResults = 5 }),
                ToolDefinitionCallerKind.Agent,
                "assistant-help"), default);
        var legacy = await worker.AwaitAsync(legacyExecution);
        Assert.AreEqual("available", legacy!.Value.GetProperty("availability").GetString());
        StringAssert.Contains(legacy.Value.GetRawText(), "governed ownership");
    }

    private sealed class AwpTestWorker : IDisposable
    {
        private readonly AwpWorker::AwpClientCredential credential;
        private readonly AwpWorker::AwpClient client;
        private readonly AwpWorker::AwpAssignmentRunner runner;
        private readonly AwpWorker::RuntimeWorkerOptions options = new() { LeaseSafetyMarginSeconds = 1 };

        private AwpTestWorker(AwpWorker::AwpClientCredential credential, AwpWorker::AwpClient client)
        {
            this.credential = credential;
            this.client = client;
            runner = new(
                new AwpWorker::AwpAssignmentExecutor(NullLoggerFactory.Instance,
                    NullLogger<AwpWorker::AwpAssignmentExecutor>.Instance),
                TimeProvider.System,
                NullLogger<AwpWorker::AwpRuntimeWorkerService>.Instance);
        }

        public static async Task<AwpTestWorker> CreateAsync(
            WebApplicationFactory<Program> factory,
            IServiceProvider services)
        {
            var workerId = Guid.NewGuid();
            var identities = services.GetRequiredService<AwpWorkerIdentityService>();
            _ = await identities.AnnounceAsync(new(workerId, "Documentation test Worker",
                AwpWorkerAuthentication.ProtocolVersion), default);
            var pairing = await identities.IssuePairingCodeAsync(workerId, default);
            var issued = await identities.ClaimAsync(new(workerId, pairing.Code), "bootstrap-test", default);
            var credential = new AwpWorker::AwpClientCredential(issued.WorkerId, issued.CredentialId,
                issued.InstanceId, Encoding.UTF8.GetBytes(issued.Secret));
            var client = new AwpWorker::AwpClient(new Uri("https://localhost"), credential,
                new(Guid.NewGuid()), TimeProvider.System, 0, factory.Server.CreateHandler());
            var worker = new AwpTestWorker(credential, client);
            await client.ActivateSessionAsync(default);
            _ = await client.RegisterAsync(new([AwpProtocol.Version], new(client.WorkerId, client.SessionId,
                "bootstrap-test", 1,
                [new(AwpRuntimeKinds.MicrosoftAgentFramework, "1.0", ["1.0"])])), default);
            return worker;
        }

        public async Task ExecuteAsync(string expectedRunId)
        {
            var claim = await client.ClaimAsync(new(client.WorkerId, client.SessionId, 1, 1), default);
            Assert.IsNotNull(claim.Assignment, $"No AWP assignment was available for FlowRun '{expectedRunId}'.");
            Assert.IsInstanceOfType<AwpRootFlowRunTarget>(claim.Assignment.Target);
            Assert.AreEqual(expectedRunId, ((AwpRootFlowRunTarget)claim.Assignment.Target).FlowRunId);
            await RunAsync(claim);
        }

        public async Task<T> AwaitAsync<T>(Task<T> operation)
        {
            while (!operation.IsCompleted)
            {
                var claim = await client.ClaimAsync(new(client.WorkerId, client.SessionId, 1, 1), default);
                if (claim.Assignment is not null) await RunAsync(claim);
            }
            return await operation;
        }

        private async Task RunAsync(AwpClaimResponse claim)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await runner.RunAsync(client, claim.Assignment!, claim.ServerTime, options, timeout.Token);
        }

        public void Dispose()
        {
            client.Dispose();
            credential.Dispose();
        }
    }

    private static ResourceReference ToolReference(string toolName, Guid workspaceId) =>
        new(AgentstrationToolProvider.ToolResourceName(toolName), ResourceScopeRef.Workspace(workspaceId));

    private static async Task ApplyAsync(
        BootstrapProfileManagementService management,
        BootstrapProfileSelection selection,
        Guid principalId)
    {
        var preview = await management.PreviewAsync(selection, principalId, default);
        Assert.IsTrue(preview.CanApply,
            string.Join(Environment.NewLine, preview.Resources.Select(value => $"{value.Kind}/{value.Name}: {value.Message}")));
        var application = await management.ApplyAsync(selection, preview.Digest, principalId, default);
        Assert.AreEqual(BootstrapApplicationStatus.Succeeded, application.Definition.Status,
            string.Join(Environment.NewLine, application.Definition.Resources.Select(value =>
                $"{value.Kind}/{value.Name}: {value.Disposition} {value.Message}")));
    }

    private static WebApplicationFactory<Program> Factory(string catalogRoot) =>
        new BootstrapApiOnlyWebApplicationFactory().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Agentstration:Authentication:Mode", "Local");
            builder.UseSetting("Agentstration:Bootstrap:Path", catalogRoot);
            builder.UseSetting("Agentstration:Bootstrap:InitialBootstrapEnabled", "true");
            builder.UseSetting("Agentstration:Bootstrap:InitialProfiles:0", "initial");
            builder.UseSetting("Agentstration:Bootstrap:Secrets:AdminPassword", Password);
            builder.UseSetting("Agentstration:AwpWorkerTrust:Enabled", "true");
            builder.UseSetting("Agentstration:AwpWorkerTrust:InstanceId", "bootstrap-test");
            builder.UseSetting("Agentstration:Extensions:Agentstration.Extensions.Ollama:Endpoint", "http://127.0.0.1:1");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IMasterKeyProvider>();
                services.AddSingleton<IMasterKeyProvider, DocumentationTestMasterKeyProvider>();
                AddFixtureTool<FixtureCrawlTool>(services);
                AddFixtureTool<FixtureReadTool>(services);
                AddFixtureTool<FixtureDeleteTool>(services);
            });
        });

    private static void AddFixtureTool<T>(IServiceCollection services) where T : class, IInternalMcpToolHandler
    {
        services.AddSingleton<T>();
        services.AddSingleton<IInternalMcpToolDefinitionProvider>(provider => provider.GetRequiredService<T>());
        services.AddSingleton<IInternalMcpToolHandler>(provider => provider.GetRequiredService<T>());
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Agentstration.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("The Agentstration repository root could not be located.");
    }

    private sealed class FixtureCrawlTool : IInternalMcpToolHandler
    {
        public const string Name = "fixture.web_crawl";
        public InternalMcpToolDefinition Definition { get; } = new(Name, "Fixture web crawl", null,
            Schema(new
            {
                type = "object",
                properties = new { startUrl = new { type = "string" }, maximumDepth = new { type = "integer" }, maximumPages = new { type = "integer" }, correlationId = new { type = "string" } },
                required = new[] { "startUrl" },
                additionalProperties = false
            }));

        public Task<JsonElement?> ExecuteAsync(InternalMcpToolInvocation invocation, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = invocation.Arguments.GetProperty("startUrl").GetString()!;
            return Task.FromResult<JsonElement?>(JsonSerializer.SerializeToElement(new
            {
                structuredContent = new
                {
                    correlationId = invocation.CorrelationId,
                    startUrl = source,
                    maximumDepth = 3,
                    maximumPages = 25,
                    contents = Array.Empty<object>(),
                    corpus = new
                    {
                        reference = "fixture-corpus",
                        sourceUrl = source,
                        mediaType = "text/markdown; charset=utf-8",
                        length = FixtureCorpus.Length,
                        sha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(FixtureCorpus)),
                        links = Array.Empty<string>(),
                        metadata = new Dictionary<string, string> { ["kind"] = "crawl-corpus", ["pageCount"] = "1" }
                    },
                    truncated = false
                }
            }));
        }
    }

    private sealed class FixtureReadTool : IInternalMcpToolHandler
    {
        public const string Name = "fixture.content_read";
        public InternalMcpToolDefinition Definition { get; } = new(Name, "Fixture content read", null,
            Schema(new
            {
                type = "object",
                properties = new { contentReference = new { type = "string" }, offset = new { type = "integer" }, maximumBytes = new { type = "integer" } },
                required = new[] { "contentReference", "offset" },
                additionalProperties = false
            }));

        public Task<JsonElement?> ExecuteAsync(InternalMcpToolInvocation invocation, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.AreEqual("fixture-corpus", invocation.Arguments.GetProperty("contentReference").GetString());
            var offset = invocation.Arguments.GetProperty("offset").GetInt32();
            var length = Math.Min(48, FixtureCorpus.Length - offset);
            var nextOffset = offset + length;
            return Task.FromResult<JsonElement?>(JsonSerializer.SerializeToElement(new
            {
                structuredContent = new
                {
                    reference = "fixture-corpus",
                    offset,
                    nextOffset,
                    contentBase64 = Convert.ToBase64String(FixtureCorpus, offset, length),
                    endOfContent = nextOffset >= FixtureCorpus.Length
                }
            }));
        }
    }

    private sealed class FixtureDeleteTool : IInternalMcpToolHandler
    {
        public const string Name = "fixture.content_delete";
        public InternalMcpToolDefinition Definition { get; } = new(Name, "Fixture content delete", null,
            Schema(new
            {
                type = "object",
                properties = new { contentReference = new { type = "string" } },
                required = new[] { "contentReference" },
                additionalProperties = false
            }));

        public Task<JsonElement?> ExecuteAsync(InternalMcpToolInvocation invocation, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.AreEqual("fixture-corpus", invocation.Arguments.GetProperty("contentReference").GetString());
            return Task.FromResult<JsonElement?>(JsonSerializer.SerializeToElement(true));
        }
    }

    private static JsonElement Schema(object value) => JsonSerializer.SerializeToElement(value);

    private sealed class DocumentationTestMasterKeyProvider : IMasterKeyProvider
    {
        public ValueTask<byte[]> GetKeyAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Enumerable.Range(1, 32).Select(value => (byte)value).ToArray());
    }

    private sealed class TemporaryCatalog : IDisposable
    {
        public TemporaryCatalog()
        {
            Root = Path.Combine(Path.GetTempPath(), $"agentstration-documentation-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public async Task WriteInitialProfileAsync()
        {
            var path = Directory.CreateDirectory(Path.Combine(Root, "initial")).FullName;
            await File.WriteAllTextAsync(Path.Combine(path, "profile.yaml"), Profile("initial", "instance"));
            await File.WriteAllTextAsync(Path.Combine(path, "00-platform-admin.yaml"), """
                apiVersion: agentstration.io/v1
                kind: PlatformAdministrator
                metadata: { name: bootstrap-admin }
                definition:
                  displayName: Bootstrap administrator
                  email: bootstrap@example.test
                  passwordFrom: { configuration: Agentstration:Bootstrap:Secrets:AdminPassword }
                """);
            await File.WriteAllTextAsync(Path.Combine(path, "10-tenant.yaml"), """
                apiVersion: agentstration.io/v1
                kind: Tenant
                metadata: { name: dev }
                definition: { displayName: Development }
                """);
            await File.WriteAllTextAsync(Path.Combine(path, "20-workspace.yaml"), """
                apiVersion: agentstration.io/v1
                kind: Workspace
                metadata: { name: default }
                definition:
                  displayName: Default workspace
                  tenantRef: { name: dev }
                """);
            await File.WriteAllTextAsync(Path.Combine(path, "30-context.yaml"), """
                apiVersion: agentstration.io/v1
                kind: PrincipalDefaultContext
                metadata: { name: bootstrap-admin }
                definition:
                  principalRef: { localAccount: bootstrap-admin }
                  tenantRef: { name: dev }
                  workspaceRef: { name: default }
                """);
        }

        public async Task WriteSupportProfileAsync()
        {
            var path = Directory.CreateDirectory(Path.Combine(Root, "documentation-support")).FullName;
            await File.WriteAllTextAsync(Path.Combine(path, "profile.yaml"), Profile("documentation-support", "workspace"));
            await File.WriteAllTextAsync(Path.Combine(path, "10-provider.yaml"), """
                apiVersion: agentstration.io/v1
                kind: ModelProvider
                metadata: { name: bootstrap-provider }
                definition:
                  displayName: Bootstrap provider
                  extension: { name: ollama-extension }
                  contributionId: ollama
                """);
            await File.WriteAllTextAsync(Path.Combine(path, "20-runtime.yaml"), """
                apiVersion: agentstration.io/v1
                kind: RuntimeProfile
                metadata: { name: bootstrap-runtime }
                definition:
                  displayName: Bootstrap runtime
                  runtimeType: microsoft-agent-framework
                  execution: { sessionMode: transient, toolInvocation: automatic, streaming: automatic }
                """);
            await File.WriteAllTextAsync(Path.Combine(path, "30-model.yaml"), """
                apiVersion: agentstration.io/v1
                kind: ModelProfile
                metadata: { name: bootstrap-model }
                definition:
                  displayName: Bootstrap model
                  provider: { name: bootstrap-provider }
                  model: { name: deterministic }
                  generation: { temperature: 0.2 }
                """);
        }

        public void CopyDocumentationProfile(string repositoryRoot)
        {
            var source = Path.Combine(repositoryRoot, "deploy", "bootstrap", "profiles", "agentstration-documentation");
            var destination = Directory.CreateDirectory(Path.Combine(Root, "agentstration-documentation")).FullName;
            foreach (var file in Directory.EnumerateFiles(source))
                File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);

        private static string Profile(string name, string scope) => $$"""
            apiVersion: agentstration.io/v1
            kind: BootstrapProfile
            metadata: { name: {{name}} }
            definition:
              displayName: {{name}}
              targetScope: {{scope}}
            """;
    }
}
