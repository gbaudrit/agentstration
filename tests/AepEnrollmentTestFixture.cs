extern alias crawl4ai;
extern alias openapi;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agentstration.Aep.Abstractions;
using Agentstration.ConnectedServices;
using Agentstration.ConnectedServices.Contracts;
using Agentstration.DataSources.Contracts;
using Agentstration.Extensions;
using Agentstration.Extensions.Aep;
using Agentstration.Extensions.Contracts;
using Agentstration.Flows;
using Agentstration.Flows.Application;
using Agentstration.Identity.Contracts;
using Agentstration.Infrastructure.Bootstrap;
using Agentstration.Infrastructure.ConnectedServices;
using Agentstration.Infrastructure.Declarative;
using Agentstration.Infrastructure.Tools;
using Agentstration.ModelProviders;
using Agentstration.ResourceManagement;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;
using Agentstration.Security.Contracts;
using Agentstration.Tools;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Crawl4AiDataSourceProfileBundle = crawl4ai::Agentstration.Extensions.Crawl4AI.Crawl4AiDataSourceProfileBundle;

namespace Agentstration.Management.Tests;

[TestClass]
public sealed partial class AepEnrollmentTests
{
    private static WebApplicationFactory<Program> EnrollmentFactory(bool observeStartup = false) => new AepEnrollmentTestFactory(observeStartup);

    private static Task<RequestContext> GetBootstrapContextAsync(WebApplicationFactory<Program> factory) =>
        factory.Services
            .GetRequiredService<ILocalEnvironmentBootstrapper>()
            .EnsureInitializedAsync(default);

    private sealed class AepEnrollmentTestFactory(bool observeStartup) : WebApplicationFactory<Program>
    {
        protected override IHost CreateHost(IHostBuilder builder)
        {
            if (!observeStartup) return base.CreateHost(builder);
            var host = builder.Build();
            var starting = host.StartAsync();
            var startup = host.Services.GetRequiredService<Agentstration.Infrastructure.BootstrapStartupState>();
            if (!SpinWait.SpinUntil(() => startup.AdministrationReady || starting.IsCompleted, TimeSpan.FromSeconds(45)))
            {
                host.Dispose();
                throw new TimeoutException("The enrollment test host did not reach Bootstrap administration.");
            }
            if (starting.IsCompleted) starting.GetAwaiter().GetResult();
            return host;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Agentstration:Testing:ApiOnly", "true");
            builder.UseSetting("Logging:LogLevel:Default", "Warning");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IPlatformAuthorizationService>();
                services.RemoveAll<IExtensionInspector>();
                services.AddSingleton<IPlatformAuthorizationService, AllowEnrollmentPlatformAdministrator>();
                services.AddSingleton<IExtensionInspector, UnavailableExtensionInspector>();
            });
        }
    }

    private sealed class UnavailableExtensionInspector : IExtensionInspector
    {
        public bool CanHandle(string providerType) => true;
        public bool CanInspectEndpoint(Uri endpoint) => true;

        public ValueTask<ExtensionInspection> InspectAsync(
            ModelProviderConfiguration provider,
            CancellationToken cancellationToken = default) =>
            InspectAsync(provider.Name, provider.Endpoint, cancellationToken);

        public ValueTask<ExtensionInspection> InspectAsync(
            string registrationName,
            Uri endpoint,
            CancellationToken cancellationToken = default) =>
            endpoint.Host == "instance-inventory.extension.example"
                ? ValueTask.FromResult(new ExtensionInspection(
                    registrationName,
                    endpoint,
                    "available",
                    new ExtensionIdentity("extension.instance-inventory", "Instance inventory", "1.0.0", null),
                    [new ExtensionContribution(AepContributionKinds.Tool, "lookup")],
                    []))
                : ValueTask.FromResult(new ExtensionInspection(
                    registrationName,
                    endpoint,
                    "unavailable",
                    null,
                    [],
                    [],
                    "The test extension is intentionally unavailable."));
    }

    private static async Task AssertBundleDriftRejectedAsync(WebApplicationFactory<Program> factory,
        ResourceScopeRef scope, ExtensionRegistrationResource registration, AepManifest manifest,
        ExtensionBundleInstallationResource installation, StoredResource<ToolSetResource> original, string drift)
    {
        var store = factory.Services.GetRequiredService<IResourceStore>();
        var address = ScopedResourceAddress.Create(scope, original.Value.Namespace, original.Value.Kind, original.Value.Name);
        var authority = factory.Services.GetRequiredService<IResourceGovernanceContext>();
        using (authority.Push(installation.Governance))
        {
            if (drift == "unrecorded")
            {
                var record = (await store.ListExactAsync<ExtensionBundleInstallationResource>(scope,
                    ExtensionKinds.ExtensionBundleInstallation, 0, 10, default)).Single();
                installation = record.Value with
                {
                    Definition = record.Value.Definition with
                    { State = ExtensionBundleInstallationState.Installing, ManagedResources = [] }
                };
                _ = await store.PutExactAsync(scope, installation, record.ETag, false, default);
            }
            if (drift is "missing" or "owner") await store.DeleteExactAsync(address, original.ETag, default);
            else if (drift == "published")
            {
                var published = (await store.ListExactAsync<ToolSetVersionResource>(scope,
                    ToolResourceKinds.ToolSetVersion, 0, 10, default)).Single();
                await store.DeleteExactAsync(ScopedResourceAddress.Create(scope, published.Value.Namespace,
                    published.Value.Kind, published.Value.Name), published.ETag, default);
                _ = await store.CreateImmutableAsync(published.Value with { DefinitionHash = "changed" }, default);
            }
            else if (drift == "flow")
            {
                var flows = factory.Services.GetRequiredService<FlowService>();
                var flow = (await flows.GetExactAsync(scope, new("crawl4ai-web-acquisition"), default))!;
                _ = await flows.UpdateExactAsync(scope, flow.Value.Id, new(flow.Value.Description, flow.Value.Version,
                    !flow.Value.Enabled, flow.Value.Definition, flow.Value.Metadata, flow.Value.Graph, flow.Value.DisplayName),
                    flow.ETag, default);
            }
            else if (drift != "manifest")
            {
                var changed = drift switch
                {
                    "definition" or "unrecorded" => original.Value with { Definition = original.Value.Definition with { DisplayName = "Changed" } },
                    "version" => original.Value with { Definition = original.Value.Definition with { Version = "2.0.0" } },
                    _ => original.Value with { Generation = original.Value.Generation + 1 }
                };
                _ = await store.PutExactAsync(scope, changed, original.ETag, false, default);
            }
        }
        if (drift == "owner")
        {
            using var other = authority.Push(ResourceGovernance.Extension("other-installation", "other.extension", "1.0.0"));
            _ = await store.PutExactAsync(scope, original.Value with { Governance = ResourceGovernance.User }, null, true, default);
        }
        if (drift == "manifest")
        {
            var bundle = manifest.Contributions.DataSourceProfileBundles!.Single();
            manifest = manifest with
            {
                Contributions = manifest.Contributions with
                {
                    DataSourceProfileBundles = [bundle with { ResourceManifests = bundle.ResourceManifests
                    .Select(value => value.Replace("Crawl4AI Web acquisition", "Changed acquisition", StringComparison.Ordinal)).ToArray() }]
                }
            };
        }
        var conflict = await Assert.ThrowsExactlyAsync<AepBundleConflictException>(() =>
            factory.Services.GetServices<IAepEnrollmentResourceProvisioner>().OfType<AepEnrollmentBundleProvisioner>()
                .Single().ProvisionAsync(registration, manifest, default));
        Assert.AreEqual(drift switch
        {
            "owner" => "extension_bundle_owner_conflict",
            "manifest" => "extension_bundle_installation_conflict",
            _ => "extension_bundle_resource_drift"
        }, conflict.Code);
        var retained = (await store.ListExactAsync<ExtensionBundleInstallationResource>(scope,
            ExtensionKinds.ExtensionBundleInstallation, 0, 10, default)).Single().Value;
        Assert.AreEqual(ExtensionBundleInstallationState.Degraded, retained.Definition.State);
        CollectionAssert.AreEqual(installation.Definition.ManagedResources.ToArray(), retained.Definition.ManagedResources.ToArray());
    }

    private sealed class EnrollmentHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory, IHttpMessageHandlerFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
        public HttpMessageHandler CreateHandler(string name) => handler;
    }

    private static IDisposable PushOwnerScope(
        WebApplicationFactory<Program> factory,
        RequestContext context,
        ResourceScopeRef target)
    {
        var scopes = factory.Services.GetRequiredService<IRequestContextScopeFactory>();
        return target.Kind switch
        {
            ResourceScopeKind.Instance => scopes.PushInstance(context),
            ResourceScopeKind.Tenant => scopes.PushTenant(context.PrincipalId, context.TenantId),
            ResourceScopeKind.Workspace => scopes.Push(context),
            _ => throw new InvalidOperationException($"Unsupported resource scope '{target}'.")
        };
    }

    private sealed class AllowEnrollmentPlatformAdministrator : IPlatformAuthorizationService
    {
        public Task<bool> IsPlatformAdministratorAsync(Guid principalId, CancellationToken cancellationToken) =>
            Task.FromResult(true);
    }

    private sealed class DenyEnrollmentPlatformAdministrator : IPlatformAuthorizationService
    {
        public Task<bool> IsPlatformAdministratorAsync(Guid principalId, CancellationToken cancellationToken) =>
            Task.FromResult(false);
    }

    private sealed class RecordingResourceProvisioner : IAepEnrollmentResourceProvisioner
    {
        public ExtensionRegistrationResource? Registration { get; private set; }
        public AepManifest? Manifest { get; private set; }
        public Exception? Failure { get; init; }

        public Task ProvisionAsync(
            ExtensionRegistrationResource registration,
            AepManifest? authenticatedManifest,
            CancellationToken cancellationToken)
        {
            Registration = registration;
            Manifest = authenticatedManifest;
            return Failure is null ? Task.CompletedTask : Task.FromException(Failure);
        }
    }

    private sealed class StaticToolDiscovery : IToolProviderDiscovery
    {
        public bool Supports(ToolProviderType providerType) => providerType == ToolProviderType.Aep;

        public Task<ToolProviderDiscoveryResult> DiscoverAsync(
            ToolProviderResource provider,
            CancellationToken cancellationToken) => Task.FromResult(new ToolProviderDiscoveryResult(
                [new DiscoveredToolDescriptor(
                    "lookup",
                    "Lookup",
                    "Looks up a value.",
                    JsonSerializer.SerializeToElement(new { type = "object" }),
                    null,
                    new Dictionary<string, JsonElement>())],
                new Dictionary<string, bool> { ["tools"] = true },
                new Dictionary<string, string>()));
    }

    private sealed class CrawlToolDiscovery : IToolProviderDiscovery
    {
        public bool Supports(ToolProviderType providerType) => providerType == ToolProviderType.Aep;

        public Task<ToolProviderDiscoveryResult> DiscoverAsync(
            ToolProviderResource provider,
            CancellationToken cancellationToken) => Task.FromResult(new ToolProviderDiscoveryResult(
                new[] { "crawl4ai.web.fetch", "crawl4ai.web.crawl", "crawl4ai.content.extract", "crawl4ai.content.read", "crawl4ai.content.delete" }
                    .Select(id => new DiscoveredToolDescriptor(
                        id,
                        id,
                        id,
                        JsonSerializer.SerializeToElement(new { type = "object" }),
                        null,
                        new Dictionary<string, JsonElement>()))
                    .ToArray(),
                new Dictionary<string, bool> { ["tools"] = true },
                new Dictionary<string, string>()));
    }

    private sealed class OpenApiToolDiscovery : IToolProviderDiscovery
    {
        public bool Supports(ToolProviderType providerType) => providerType == ToolProviderType.Aep;

        public Task<ToolProviderDiscoveryResult> DiscoverAsync(ToolProviderResource provider, CancellationToken cancellationToken) =>
            Task.FromResult(new ToolProviderDiscoveryResult(
                new[] { "openapi.contract.discover", "openapi.operation.invoke" }.Select(id =>
                    new DiscoveredToolDescriptor(id, id, id, JsonSerializer.SerializeToElement(new { type = "object" }),
                        null, new Dictionary<string, JsonElement>())).ToArray(),
                new Dictionary<string, bool> { ["tools"] = true }, new Dictionary<string, string>()));
    }

    private sealed class BundleApplyConflict
    {
        public bool Pending { get; set; }
        public bool InterruptPublication { get; init; }
    }

    private sealed class ConflictingFlowHandler(
        FlowBootstrapResourceHandler inner,
        FlowService flows,
        BundleApplyConflict conflict) : IBootstrapResourceHandler
    {
        public string Kind => inner.Kind;
        public BootstrapProfileScope Scope => inner.Scope;
        public bool SupportsProfileScope(BootstrapProfileScope profileScope) => inner.SupportsProfileScope(profileScope);

        public Task<BootstrapResourcePlanResult> PlanAsync(
            BootstrapResourceDocument resource,
            BootstrapResourceOperationContext operation,
            BootstrapPlanningContext planning,
            CancellationToken cancellationToken) => inner.PlanAsync(resource, operation, planning, cancellationToken);

        public async Task<BootstrapResourceApplyResult> ApplyAsync(
            BootstrapResourceDocument resource,
            BootstrapResourceOperationContext operation,
            CancellationToken cancellationToken)
        {
            if (!conflict.Pending) return await inner.ApplyAsync(resource, operation, cancellationToken);
            conflict.Pending = false;
            if (conflict.InterruptPublication)
            {
                var value = ResourceManifestSerializer.FromJson<DeclarativeResourceEnvelope<DeclarativeFlowDefinition>>(
                    JsonSerializer.Serialize(resource, JsonSerializerOptions.Web));
                var definition = value.Definition;
                _ = await flows.CreateExactAsync(ResourceScopeRef.Workspace(operation.Target!.WorkspaceId!.Value),
                    new(value.Metadata.Name, definition.Description, definition.Version, definition.Enabled,
                        definition.Spec, definition.Metadata, definition.Graph, definition.DisplayName),
                    value.Metadata.Namespace, cancellationToken);
            }
            return BootstrapResourceApplyResult.Conflict;
        }
    }

    private sealed class EnrollmentLifecycleHandler(
        string extensionId,
        bool advertiseTool = false,
        bool advertiseBundle = false,
        AepDataSourceProfileBundleContribution? bundleOverride = null) : HttpMessageHandler
    {
        public AepCredentialRotation? Rotation { get; private set; }
        public int PreviousRevocationCount { get; private set; }
        public int UnenrollmentCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsolutePath == AepEnrollmentProtocol.CredentialRotationPath)
                Rotation = await request.Content!.ReadFromJsonAsync<AepCredentialRotation>(AepProtocol.JsonOptions, cancellationToken);
            else if (request.RequestUri?.AbsolutePath == AepEnrollmentProtocol.PreviousCredentialRevocationPath)
                PreviousRevocationCount++;
            else if (request.RequestUri?.AbsolutePath == AepEnrollmentProtocol.UnenrollmentPath)
                UnenrollmentCount++;

            if (request.RequestUri?.AbsolutePath != AepProtocol.DiscoveryPath)
                return new(HttpStatusCode.OK);
            var manifest = Manifest();
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(manifest, options: AepProtocol.JsonOptions) };
        }

        public AepManifest Manifest()
        {
            var tools = bundleOverride is not null
                ? bundleOverride.RequiredTools!.Select(id => new AepToolContribution(id, id, new AepMcpToolMapping("main", id))).ToArray()
                : advertiseBundle
                ? new[]
                {
                    new AepToolContribution("crawl4ai.web.fetch", "Fetch", new AepMcpToolMapping("main", "web_fetch")),
                    new AepToolContribution("crawl4ai.web.crawl", "Crawl", new AepMcpToolMapping("main", "web_crawl")),
                    new AepToolContribution("crawl4ai.content.extract", "Extract", new AepMcpToolMapping("main", "content_extract")),
                    new AepToolContribution("crawl4ai.content.read", "Read", new AepMcpToolMapping("main", "content_read")),
                    new AepToolContribution("crawl4ai.content.delete", "Delete", new AepMcpToolMapping("main", "content_delete"))
                }
                : advertiseTool
                ? new[] { new AepToolContribution("lookup", "Lookup", new AepMcpToolMapping("main", "lookup")) }
                : [];
            return new AepManifest(
                AepProtocol.Version,
                new(extensionId, "Pairing test", "1.0.0"),
                new Dictionary<string, AepCapabilityDescriptor>(),
                new AepContributions([], tools, DataSourceProfileBundles: bundleOverride is not null ? [bundleOverride] : advertiseBundle ? [Crawl4AiDataSourceProfileBundle.Create()] : []),
                advertiseTool || advertiseBundle || bundleOverride is not null ? new AepMcpDescriptor([new AepMcpServerDescriptor("main", "/mcp")]) : null);
        }
    }
}
