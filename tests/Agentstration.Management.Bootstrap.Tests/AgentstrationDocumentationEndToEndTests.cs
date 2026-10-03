using System.Text;
using System.Text.Json;
using Agentstration.Agents;
using Agentstration.Extensions;
using Agentstration.Flows;
using Agentstration.Flows.Application;
using Agentstration.Identity.Contracts;
using Agentstration.Infrastructure.Assistant;
using Agentstration.Knowledge;
using Agentstration.Knowledge.Contracts;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;
using Agentstration.Security.AspNetCoreIdentity;
using Agentstration.Tools;
using Agentstration.Web.Hosting;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

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
        await ApplyAsync(management, new(["documentation-support"], target), principal.Id);

        var bindings = new List<BootstrapBindingSelection>
        {
            Selection("crawl4ai-crawl", AgentstrationToolProvider.ToolResourceName(FixtureCrawlTool.Name), workspace.Id),
            Selection("crawl4ai-read", AgentstrationToolProvider.ToolResourceName(FixtureReadTool.Name), workspace.Id),
            Selection("crawl4ai-delete", AgentstrationToolProvider.ToolResourceName(FixtureDeleteTool.Name), workspace.Id)
        };
        bindings.Add(await ExistingSelectionAsync(management, target, "assistant-model",
            BootstrapBindingTargetKind.ModelProfile, "bootstrap-model", principal.Id));
        bindings.Add(await ExistingSelectionAsync(management, target, "assistant-runtime",
            BootstrapBindingTargetKind.RuntimeProfile, "bootstrap-runtime", principal.Id));
        await ApplyAsync(management,
            new(["agentstration-documentation"], target, bindings), principal.Id);

        var assignedAgent = await services.GetRequiredService<AgentManagementService>()
            .GetAgentAsync("agentstration-documentation-assistant", default);
        Assert.IsNotNull(assignedAgent);
        Assert.IsEmpty(assignedAgent.Value.Definition.Tools);
        Assert.HasCount(1, assignedAgent.Value.Definition.ToolSets);
        Assert.AreEqual("agentstration-documentation", assignedAgent.Value.Definition.ToolSets[0].ToolSet.Name);

        var acquisitions = services.GetRequiredService<KnowledgeAcquisitionService>();
        var started = await acquisitions.StartAsync(new("agentstration-documentation"),
            JsonSerializer.SerializeToElement(new { }), "documentation-fixture", "documentation-fixture", default);
        var flowScope = new FlowRunScope(tenant.Id, new WorkspaceId(workspace.Id), principal.Id);
        await DrainAsync(services.GetRequiredService<FlowRunService>(), started.Value.FlowRunId, flowScope);
        var completed = await acquisitions.GetAsync(started.Value.Name, ResourceNamespace.Default, default);
        Assert.AreEqual(KnowledgeAcquisitionState.Succeeded, completed.Value.State, completed.Value.ErrorMessage);
        var artifact = completed.Value.Manifest?.Artifacts.Single();
        Assert.IsNotNull(artifact);
        Assert.AreEqual(KnowledgeArtifactDisposition.Publishable, artifact.Disposition);

        var snapshot = await services.GetRequiredService<KnowledgeSnapshotService>().PublishAsync(
            started.Value.Name,
            ResourceNamespace.Default,
            new PublishKnowledgeSnapshotRequest { ArtifactIds = [artifact.ArtifactId] },
            "documentation-fixture-snapshot",
            default);
        Assert.AreEqual(started.Value.FlowRunId, snapshot.Value.IngestionFlowRunId);

        var toolResult = await services.GetRequiredService<IToolDefinitionExecutor>().ExecuteAsync(
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
        StringAssert.Contains(toolResult.Output!.Value.GetRawText(), "governed ownership");

        var legacy = await services.GetRequiredService<AssistantDocumentationMcpTool>().ExecuteAsync(
            new InternalMcpToolInvocation(
                tenant.Id,
                new WorkspaceId(workspace.Id),
                principal.Id,
                "legacy-documentation-search",
                "legacy-documentation-correlation",
                JsonSerializer.SerializeToElement(new { query = "Workspace", maximumResults = 5 }),
                ToolDefinitionCallerKind.Agent,
                "assistant-help"), default);
        Assert.AreEqual("available", legacy!.Value.GetProperty("availability").GetString());
        StringAssert.Contains(legacy.Value.GetRawText(), "governed ownership");
    }

    private static async Task DrainAsync(FlowRunService runs, string runId, FlowRunScope scope)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var current = await runs.GetAsync(scope.WorkspaceId, runId, default)
                ?? throw new AssertFailedException($"FlowRun '{runId}' was not found.");
            if (current.Value.Status.IsTerminal()) return;
            if (current.Value.Status == FlowRunStatus.WaitingForChild)
            {
                foreach (var childId in current.Value.Steps
                             .Select(step => step.ChildFlowRunId)
                             .Where(value => value is not null)
                             .Cast<string>()
                             .Distinct(StringComparer.Ordinal))
                    await DrainAsync(runs, childId, scope);
            }
            await runs.ExecuteAsync(new(runId, scope), default);
        }
        Assert.Fail($"FlowRun '{runId}' did not reach a terminal state.");
    }

    private static BootstrapBindingSelection Selection(string bindingName, string toolName, Guid workspaceId) =>
        new("agentstration-documentation", bindingName,
            new ResourceReference(toolName, ResourceScopeRef.Workspace(workspaceId)));

    private static async Task<BootstrapBindingSelection> ExistingSelectionAsync(
        BootstrapProfileManagementService management,
        BootstrapApplicationTarget target,
        string bindingName,
        BootstrapBindingTargetKind kind,
        string resourceName,
        Guid principalId)
    {
        var option = (await management.GetBindingTargetsAsync(target, kind,
            ["agentstration-documentation"], principalId, default)).Single(value => value.Name == resourceName);
        return new("agentstration-documentation", bindingName,
            new ResourceReference(option.Name, option.ScopeRef, ResourceNamespace.Parse(option.Namespace)));
    }

    private static async Task ApplyAsync(
        BootstrapProfileManagementService management,
        BootstrapProfileSelection selection,
        Guid principalId)
    {
        var preview = await management.PreviewAsync(selection, principalId, default);
        Assert.IsTrue(preview.CanApply,
            string.Join(Environment.NewLine, preview.Resources.Select(value => $"{value.Kind}/{value.Name}: {value.Message}")));
        var application = await management.ApplyAsync(selection, preview.Digest, principalId, default);
        Assert.AreEqual(BootstrapApplicationStatus.Succeeded, application.Definition.Status);
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
            builder.UseSetting("Agentstration:Extensions:Agentstration.Extensions.Ollama:Endpoint", "http://127.0.0.1:1");
            builder.ConfigureServices(services =>
            {
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
