using System.Net;
using System.Net.Http.Json;
using Agentstration.Aep.Abstractions;
using Agentstration.Extensions.Aep;
using Agentstration.Extensions.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.Infrastructure;
using Agentstration.Parameters;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Tools;
using Agentstration.Work;
using Agentstration.Work.Storage.Abstractions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Agentstration.Management.Tests;

public sealed partial class AepEnrollmentTests
{
    private const string PhasedExtensionId = "extension.delayed-bootstrap";
    private const string PhasedKey = "phased-bootstrap-public-disposable-test-key-material";

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task PhasedBootstrapWaitsForAnExternalExtensionAndItsContributions(bool failFirstProvisioning)
    {
        var root = await CreatePhasedCatalogAsync();
        try
        {
            var failure = new FailFirstBootstrapProvisioner(failFirstProvisioning);
            await using var factory = PhasedFactory(root, failure);
            using var client = factory.CreateClient();
            var startup = factory.Services.GetRequiredService<BootstrapStartupState>();
            Assert.IsFalse(startup.IsReady);
            Assert.AreEqual(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/health/ready")).StatusCode);
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/api/tools")).StatusCode);
            await WaitForPhasedTopologyAsync(factory);
            using (factory.Services.GetRequiredService<IRequestContextScopeFactory>().PushSystem())
            {
                var store = factory.Services.GetRequiredService<IResourceStore>();
                Assert.IsNull(await store.GetExactAsync<ToolSetResource>(ScopedResourceAddress.Create(
                    ResourceScopeRef.Instance, ResourceNamespace.Default, ToolResourceKinds.ToolSet, "bootstrap-consumer"), default));
            }
            var announcement = new AepEnrollmentAnnouncement(Guid.NewGuid(), new(PhasedExtensionId, "Delayed extension", "1.0.0"),
                new Uri("https://delayed-bootstrap.example/"), null, AepEnrollmentMethod.SharedKeyFile);
            await AnnouncePhasedAsync(client, announcement);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            try { await startup.WaitUntilReadyAsync(deadline.Token); }
            catch (OperationCanceledException) { Assert.Fail($"Phased Bootstrap did not become ready: {startup.Failure}"); }
            Assert.AreEqual(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);
            using var system = factory.Services.GetRequiredService<IRequestContextScopeFactory>().PushSystem();
            var resources = factory.Services.GetRequiredService<IResourceStore>();
            var consumer = await resources.GetExactAsync<ToolSetResource>(ScopedResourceAddress.Create(
                ResourceScopeRef.Instance, ResourceNamespace.Default, ToolResourceKinds.ToolSet, "bootstrap-consumer"), default);
            Assert.IsNotNull(consumer);
            Assert.AreEqual(ResourceScopeRef.Instance, consumer.Value.Definition.Members.Single().Tool.ScopeRef);
            var tools = await resources.ListExactAsync<ToolResource>(ResourceScopeRef.Instance, ToolResourceKinds.Tool, 0, 100, default);
            var selectedTool = tools.Single(value => value.Value.Name == consumer.Value.Definition.Members.Single().Tool.Name);
            Assert.AreEqual("crawl4ai.web.crawl", selectedTool.Value.Definition.ExternalId);
            Assert.IsTrue(selectedTool.Value.Definition.Enabled);
            Assert.AreEqual(failFirstProvisioning ? 2 : 1, failure.Calls);
            var tenant = await factory.Services.GetRequiredService<IIdentityStore>().FindTenantByNameAsync("bootstrap", default);
            var parameter = await resources.GetExactAsync<ParameterResource>(ScopedResourceAddress.Create(
                ResourceScopeRef.Tenant(tenant!.Id), ResourceNamespace.Default, ParameterResourceKinds.Parameter, "tenant-bootstrap-ready"), default);
            Assert.AreEqual("ready", parameter!.Value.Definition.Value.GetString());
            var workspace = await factory.Services.GetRequiredService<IIdentityStore>().FindWorkspaceByNameAsync(tenant.Id, "default", default);
            var entry = await factory.Services.GetRequiredService<IWorkplaceRepository>().GetEntryAsync(
                new(workspace!.Id), new("bootstrap-entry"), default);
            Assert.IsNotNull(entry);
            await AnnouncePhasedAsync(client, announcement);
            var again = await resources.GetExactAsync<ToolSetResource>(ScopedResourceAddress.Create(
                ResourceScopeRef.Instance, ResourceNamespace.Default, ToolResourceKinds.ToolSet, "bootstrap-consumer"), default);
            Assert.AreEqual(consumer.Value.Uid, again!.Value.Uid);
            Assert.AreEqual(consumer.ETag, again.ETag);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task PhasedBootstrapNamesTheMissingContributionAtItsDeadline()
    {
        var root = await CreatePhasedCatalogAsync();
        try
        {
            await using var factory = PhasedFactory(root, new(false), "00:00:01");
            using var client = factory.CreateClient();
            var startup = factory.Services.GetRequiredService<BootstrapStartupState>();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            try { await startup.WaitUntilReadyAsync(deadline.Token); Assert.Fail("Bootstrap unexpectedly became ready."); }
            catch (OperationCanceledException) when (startup.Failure is not null) { }
            Assert.IsFalse(startup.IsReady);
            StringAssert.Contains(startup.Failure, "bootstrap_dependency_timeout");
            StringAssert.Contains(startup.Failure, "bootstrap-consumer");
            StringAssert.Contains(startup.Failure, "crawl4ai.web.crawl");
            StringAssert.Contains(startup.Failure, PhasedExtensionId);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task PhasedBootstrapRejectsCatalogChangesWhileWaiting()
    {
        var root = await CreatePhasedCatalogAsync();
        try
        {
            await using var factory = PhasedFactory(root, new(false));
            using var client = factory.CreateClient();
            var startup = factory.Services.GetRequiredService<BootstrapStartupState>();
            await WaitForPhasedTopologyAsync(factory);
            await File.AppendAllTextAsync(Path.Combine(root, "base", "20-consumer.yaml"), "\n# changed while waiting\n");
            await AnnouncePhasedAsync(client, new(Guid.NewGuid(), new(PhasedExtensionId, "Delayed extension", "1.0.0"),
                new Uri("https://delayed-bootstrap.example/"), null, AepEnrollmentMethod.SharedKeyFile));
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            try { await startup.WaitUntilReadyAsync(deadline.Token); Assert.Fail("Changed catalog was applied."); }
            catch (OperationCanceledException) when (startup.Failure is not null) { }
            StringAssert.Contains(startup.Failure, "catalog changed");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static WebApplicationFactory<Program> PhasedFactory(string root, FailFirstBootstrapProvisioner provisioner, string timeout = "00:01:30") =>
        EnrollmentFactory(observeStartup: true).WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Agentstration:Authentication:Mode", "Local");
            builder.UseSetting("Agentstration:Extensions:DiscoverOnStartup", "false");
            builder.UseSetting("Agentstration:Bootstrap:Path", root);
            builder.UseSetting("Agentstration:Bootstrap:InitialBootstrapEnabled", "true");
            builder.UseSetting("Agentstration:Bootstrap:InitialProfiles:0", "base");
            builder.UseSetting("Agentstration:Bootstrap:InitialApplications:0:Profiles:0", "tenant-resources");
            builder.UseSetting("Agentstration:Bootstrap:InitialApplications:0:TargetLevel", "/tenants/bootstrap");
            builder.UseSetting("Agentstration:Bootstrap:InitialApplications:1:Profiles:0", "workspace-resources");
            builder.UseSetting("Agentstration:Bootstrap:InitialApplications:1:TargetLevel", "/tenants/bootstrap/workspaces/default");
            builder.UseSetting("Agentstration:Bootstrap:ExtensionDependencyTimeout", timeout);
            builder.UseSetting("Agentstration:Bootstrap:Secrets:AdminPassword", "Bootstrap123!Fixture");
            builder.UseSetting($"Agentstration:Extensions:{PhasedExtensionId}:EnrollmentMode", "SharedKeyFile");
            builder.UseSetting($"Agentstration:Extensions:{PhasedExtensionId}:SharedKeyFile:Path", Path.Combine(root, "test.key"));
            // This configured, unannounced extension is deliberately unrelated to the consumer.
            builder.UseSetting("Agentstration:Extensions:extension.optional:EnrollmentMode", "SharedKeyFile");
            builder.UseSetting("Agentstration:Extensions:extension.optional:SharedKeyFile:Path", Path.Combine(root, "optional.key"));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IHttpClientFactory>();
                services.AddSingleton<IHttpClientFactory>(new EnrollmentHttpClientFactory(
                    new EnrollmentLifecycleHandler(PhasedExtensionId, advertiseBundle: true)));
                services.RemoveAll<IToolProviderDiscovery>();
                services.AddSingleton<IToolProviderDiscovery, CrawlToolDiscovery>();
                services.AddSingleton<IAepEnrollmentResourceProvisioner>(provisioner);
            });
        });

    private static async Task<string> CreatePhasedCatalogAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"agentstration-phased-bootstrap-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "base"));
        await File.WriteAllTextAsync(Path.Combine(root, "test.key"), PhasedKey);
        await File.WriteAllTextAsync(Path.Combine(root, "optional.key"), "optional-bootstrap-extension-disposable-distinct-key");
        Directory.CreateDirectory(Path.Combine(root, "tenant-resources"));
        await File.WriteAllTextAsync(Path.Combine(root, "tenant-resources", "profile.yaml"), """
            apiVersion: agentstration.io/v1
            kind: BootstrapProfile
            metadata: { name: tenant-resources }
            definition: { targetScope: tenant }
            """);
        await File.WriteAllTextAsync(Path.Combine(root, "tenant-resources", "10-parameter.yaml"), """
            apiVersion: agentstration.io/v1
            kind: Parameter
            metadata: { name: tenant-bootstrap-ready }
            definition: { displayName: Tenant ready, valueType: string, value: ready }
            """);
        Directory.CreateDirectory(Path.Combine(root, "workspace-resources"));
        await File.WriteAllTextAsync(Path.Combine(root, "workspace-resources", "profile.yaml"), """
            apiVersion: agentstration.io/v1
            kind: BootstrapProfile
            metadata: { name: workspace-resources }
            definition: { targetScope: workspace }
            """);
        await File.WriteAllTextAsync(Path.Combine(root, "workspace-resources", "10-flow.yaml"), """
            apiVersion: agentstration.io/v1
            kind: Flow
            metadata: { name: bootstrap-workspace-flow }
            definition:
              displayName: Workspace bootstrap Flow
              version: 1.0.0
              spec:
                flowKind: workflow
                entryNodeId: input
                nodes: [{ id: input, kind: input }]
                edges: []
              graph:
                entryStep: input
                steps:
                  - { type: input, name: input }
                  - { type: output, name: completed, outcome: success, outputMapping: "${input}" }
                  - { type: output, name: error, outcome: error, code: BOOTSTRAP_FAILED, message: Bootstrap flow failed. }
                transitions:
                  - { id: input-completed, fromStep: input, event: completed, toStep: completed }
              publish: true
              activate: true
            """);
        await File.WriteAllTextAsync(Path.Combine(root, "workspace-resources", "20-entry.yaml"), """
            apiVersion: agentstration.io/v1
            kind: Entry
            metadata: { name: bootstrap-entry }
            definition:
              displayName: Bootstrap Entry
              presentation:
                kind: prompt
                fields:
                  - { name: request, type: prompt, label: Request, required: true, order: 0, role: primaryInput }
              binding: { kind: flow, resourceId: bootstrap-workspace-flow }
              behavior: { taskCreationMode: automatic, allowConversation: true, streamResponse: true }
              publish: true
            """);
        await File.WriteAllTextAsync(Path.Combine(root, "base", "profile.yaml"), """
            apiVersion: agentstration.io/v1
            kind: BootstrapProfile
            metadata: { name: base }
            definition: { targetScope: instance }
            """);
        await File.WriteAllTextAsync(Path.Combine(root, "base", "00-topology.yaml"), """
            apiVersion: agentstration.io/v1
            kind: PlatformAdministrator
            metadata: { name: bootstrap-admin }
            definition:
              displayName: Bootstrap administrator
              passwordFrom: { configuration: "Agentstration:Bootstrap:Secrets:AdminPassword" }
            ---
            apiVersion: agentstration.io/v1
            kind: Tenant
            metadata: { name: bootstrap }
            definition: { displayName: Bootstrap }
            ---
            apiVersion: agentstration.io/v1
            kind: Workspace
            metadata: { name: default }
            definition: { displayName: Default, tenantRef: { name: bootstrap } }
            """);
        await File.WriteAllTextAsync(Path.Combine(root, "base", "10-policy.yaml"), $$"""
            apiVersion: agentstration.io/v1
            kind: ExtensionEnrollmentPolicy
            metadata: { name: required-extension }
            definition:
              extensionId: {{PhasedExtensionId}}
              autoEnrollmentLevel: /instance
            """);
        await File.WriteAllTextAsync(Path.Combine(root, "base", "20-consumer.yaml"), $$"""
            apiVersion: agentstration.io/v1
            kind: ToolSet
            metadata: { name: bootstrap-consumer }
            definition:
              displayName: Bootstrap consumer
              members:
                - capability: web.crawl
                  route: default
                  tool:
                    extensionContribution:
                      extensionId: {{PhasedExtensionId}}
                      kind: Tool
                      contributionId: crawl4ai.web.crawl
            """);
        return root;
    }

    private static async Task WaitForPhasedTopologyAsync(WebApplicationFactory<Program> factory)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var identities = factory.Services.GetRequiredService<IIdentityStore>();
        while (await identities.FindTenantByNameAsync("bootstrap", deadline.Token) is null)
            await Task.Delay(TimeSpan.FromMilliseconds(50), deadline.Token);
    }

    private static async Task AnnouncePhasedAsync(HttpClient client, AepEnrollmentAnnouncement announcement)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/aep/enrollments/announce")
        {
            Content = JsonContent.Create(announcement, options: AepProtocol.JsonOptions)
        };
        request.Headers.Add("X-AEP-Enrollment-Timestamp", timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Headers.Add("X-AEP-Enrollment-Signature", AepEnrollmentProofs.Sign(announcement, timestamp, PhasedKey));
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
    }

    private sealed class FailFirstBootstrapProvisioner(bool failFirst) : IAepEnrollmentResourceProvisioner
    {
        public int Calls { get; private set; }
        public Task ProvisionAsync(ExtensionRegistrationResource registration, AepManifest? authenticatedManifest, CancellationToken cancellationToken)
        {
            Calls++;
            return failFirst && Calls == 1 ? Task.FromException(new InvalidOperationException("Deterministic first-attempt failure.")) : Task.CompletedTask;
        }
    }
}
