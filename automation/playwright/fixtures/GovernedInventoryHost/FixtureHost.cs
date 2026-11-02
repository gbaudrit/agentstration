using System.Net;
using System.Text.Json;
using Agentstration.Aep.Abstractions;
using Agentstration.Extensions.Aep;
using Agentstration.Extensions.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.Infrastructure.Bootstrap;
using Agentstration.ModelProviders;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Tools;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http;

namespace Agentstration.Playwright.GovernedInventory;

internal static class FixtureHost
{
    public static async Task Main()
    {
        await using var factory = new InventoryFactory();
        var address = new Uri(Environment.GetEnvironmentVariable("ASPNETCORE_URLS")
            ?? throw new InvalidOperationException("The browser fixture requires ASPNETCORE_URLS."));
        factory.UseKestrel(address.Port);
        factory.StartServer();
        var shutdown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lifetime = factory.Services.GetRequiredService<IHostApplicationLifetime>();
        using var registration = lifetime.ApplicationStopping.Register(() => shutdown.TrySetResult());
        await factory.Services.GetRequiredService<InventorySeeder>().SeedAsync(lifetime.ApplicationStopping);
        var readyFile = Environment.GetEnvironmentVariable("GOVERNED_INVENTORY_READY_FILE")
            ?? throw new InvalidOperationException("The browser fixture requires a readiness file.");
        await File.WriteAllTextAsync(readyFile, "ready", lifetime.ApplicationStopping);
        await shutdown.Task;
    }
}

internal sealed class InventoryFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseContentRoot(Path.Combine(Directory.GetCurrentDirectory(), "src", "Agentstration.Web"));
        builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddCommandLine(Environment.GetCommandLineArgs().Skip(1).ToArray()));
        builder.UseEnvironment("Testing");
        builder.UseStaticWebAssets();
        // Public, disposable local browser fixture; satisfies the regular password policy.
        builder.UseSetting("Agentstration:Bootstrap:Secrets:AdminPassword", "Fixture753!Admin");
        builder.UseSetting("Logging:LogLevel:Default", "Warning");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IExtensionInspector>();
            services.AddSingleton<IExtensionInspector, InventoryInspector>();
            services.AddSingleton<IHttpMessageHandlerBuilderFilter, InventoryTransportFilter>();
            services.AddSingleton<InventorySeeder>();
        });
    }
}

internal sealed class InventoryTransportFilter : IHttpMessageHandlerBuilderFilter
{
    public Action<HttpMessageHandlerBuilder> Configure(Action<HttpMessageHandlerBuilder> next) => builder =>
    {
        next(builder);
        builder.AdditionalHandlers.Insert(0, new InventoryTransport());
    };
}

internal sealed class InventoryTransport : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        request.RequestUri?.Host == "inventory.example.test"
            ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { RequestMessage = request })
            : base.SendAsync(request, cancellationToken);
}

// Test-host data only: the browser exercises the real Console/API/services/SQLite,
// while this inspector supplies deterministic discovery without a remote extension.
internal sealed class InventoryInspector : IExtensionInspector
{
    public bool CanHandle(string providerType) => false;
    public bool CanInspectEndpoint(Uri endpoint) => endpoint.Host == "inventory.example.test";
    public ValueTask<ExtensionInspection> InspectAsync(ModelProviderConfiguration provider, CancellationToken cancellationToken = default) =>
        InspectAsync(provider.Name, provider.Endpoint, cancellationToken);
    public ValueTask<ExtensionInspection> InspectAsync(string registrationName, Uri endpoint, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(new ExtensionInspection(registrationName, endpoint, "available",
            new("example.governed-inventory", registrationName == "qa-degraded" ? "Degraded inventory fixture" : "Governed inventory fixture", "9.0.0", null),
            [new("data-source-profile-bundle", registrationName), new("tool", "fixture.ready"),
                new("tool", "fixture.disabled"), new("tool", "fixture.missing"), new("tool", "fixture.conflict")], []));
}

internal sealed class InventorySeeder(
    IServiceScopeFactory scopes,
    IResourceStore store,
    IRequestContextScopeFactory requestScopes,
    IResourceGovernanceContext governance,
    TimeProvider clock)
{
    private static readonly ResourceNamespace Namespace = ResourceNamespace.Parse("qa-inventory");
    private static readonly ResourceScopeRef Scope = ResourceScopeRef.Instance;
    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        using var request = requestScopes.PushSystem();
        using var services = scopes.CreateScope();
        foreach (var (name, degraded) in new[] { ("qa-managed", false), ("qa-degraded", true) })
        {
            var registration = new ExtensionRegistrationResource
            {
                ApiVersion = ResourceApiVersions.CoreV1,
                Kind = ExtensionKinds.ExtensionRegistration,
                Metadata = new() { Name = name, Namespace = Namespace },
                ScopeRef = Scope,
                Definition = new()
                {
                    DisplayName = degraded ? "Degraded inventory fixture" : "Governed inventory fixture",
                    Endpoint = new($"https://inventory.example.test/{name}"),
                    ExpectedExtensionId = "example.governed-inventory",
                    EnrollmentMode = AepEnrollmentMode.PairingCode
                }
            };
            _ = await store.PutExactAsync(Scope, registration, null, true, cancellationToken);
            using (governance.Push(ResourceGovernance.Extension(
                $"{Scope.Value}/{registration.Kind}/{Namespace}/{name}", "example.governed-inventory", "9.0.0")))
            {
                _ = await store.PutExactAsync(Scope, new ToolProviderResource
                {
                    ApiVersion = ResourceApiVersions.CoreV1,
                    Kind = ToolResourceKinds.ToolProvider,
                    Metadata = new() { Name = name, Namespace = Namespace },
                    ScopeRef = Scope,
                    Definition = new()
                    {
                        DisplayName = registration.Definition.DisplayName,
                        ProviderType = ToolProviderType.Aep,
                        Aep = new() { Extension = new(name, Scope, Namespace) }
                    }
                }, null, true, cancellationToken);
                foreach (var id in new[] { "ready", "disabled", "conflict", "conflict-copy" })
                    _ = await store.PutExactAsync(Scope, new ToolResource
                    {
                        ApiVersion = ResourceApiVersions.CoreV1,
                        Kind = ToolResourceKinds.Tool,
                        Metadata = new() { Name = $"{name}.{id}", Namespace = Namespace },
                        ScopeRef = Scope,
                        Definition = new()
                        {
                            DisplayName = $"Fixture {id}",
                            ExternalId = $"fixture.{(id == "conflict-copy" ? "conflict" : id)}",
                            Provider = new(name, Scope, Namespace),
                            Enabled = id != "disabled",
                            Discovery = new() { Available = true, FirstSeenAt = clock.GetUtcNow(), LastSeenAt = clock.GetUtcNow() },
                            Schema = new() { Input = JsonSerializer.SerializeToElement(new { type = "object" }, JsonSerializerOptions.Web) }
                        }
                    }, null, true, cancellationToken);
            }
            var bundle = Bundle(name);
            var manifest = new AepManifest(AepProtocol.Version,
                new("example.governed-inventory", registration.Definition.DisplayName, "9.0.0"),
                new Dictionary<string, AepCapabilityDescriptor>(),
                new AepContributions([], [], DataSourceProfileBundles: [bundle]));
            await services.ServiceProvider.GetServices<IAepEnrollmentResourceProvisioner>()
                .OfType<AepEnrollmentBundleProvisioner>().Single().ProvisionAsync(registration, manifest, cancellationToken);
            if (degraded)
            {
                var record = (await store.ListExactAsync<ExtensionBundleInstallationResource>(Scope,
                    ExtensionKinds.ExtensionBundleInstallation, 0, 100, cancellationToken))
                    .Single(value => value.Value.Definition.Registration.Name == name);
                using var authority = governance.Push(record.Value.Governance);
                _ = await store.PutExactAsync(Scope, record.Value with
                {
                    Definition = record.Value.Definition with
                    { State = ExtensionBundleInstallationState.Degraded, Diagnostic = "fixture: recorded definition drift" }
                }, record.ETag, false, cancellationToken);
            }
            var instanceId = Guid.Parse(degraded ? "75300000-0000-0000-0000-000000000002" : "75300000-0000-0000-0000-000000000001");
            _ = await store.PutExactAsync(Scope, new AepEnrollmentRequestResource
            {
                ApiVersion = ResourceApiVersions.CoreV1,
                Kind = ExtensionKinds.AepEnrollmentRequest,
                Metadata = new() { Name = instanceId.ToString("N") },
                ScopeRef = Scope,
                Definition = new()
                {
                    InstanceId = instanceId,
                    TargetScopeRef = Scope,
                    ExtensionId = manifest.Extension.Id,
                    ExtensionName = registration.Definition.DisplayName,
                    ExtensionVersion = "9.0.0",
                    Endpoint = registration.Definition.Endpoint,
                    RegistrationName = name,
                    State = AepEnrollmentState.Available,
                    AnnouncedAt = clock.GetUtcNow()
                }
            }, null, true, cancellationToken);
        }
        await services.ServiceProvider.GetRequiredService<AepDataSourceProfileBundleInstaller>()
            .InstallAsync(Bundle("qa-user"), new(), new Dictionary<string, ResourceReference>
            { ["fixture.ready"] = new("qa-managed.ready", Scope, Namespace) }, cancellationToken);
    }

    private static AepDataSourceProfileBundleContribution Bundle(string name) => new(
        name, "Governed fixture bundle", "2.4.0",
        [$$"""
        apiVersion: agentstration.io/v1
        kind: ToolSet
        metadata: { name: {{name}}, namespace: qa-inventory }
        definition:
          displayName: Fixture tools
          version: 2.4.0
          publish: true
          members: [{ tool: { binding: fixture.ready }, capability: fixture.read, route: default }]
        """, $$"""
        apiVersion: agentstration.io/v1
        kind: Flow
        metadata: { name: {{name}}, namespace: qa-inventory }
        definition:
          displayName: Fixture acquisition
          version: 2.4.0
          enabled: true
          metadata: { flow.contract: datasource.acquisition/v1 }
          spec:
            flowKind: workflow
            entryNodeId: input
            nodes: [{ id: input, kind: input }]
            edges: []
          graph:
            entryStep: input
            inputSchema:
              type: object
              properties:
                dataSourceId: { type: string }
                dataSourceUid: { type: string }
                dataSourceGeneration: { type: integer }
                profile: { type: object }
                sourceConfiguration: { type: object }
                parameters: { type: object }
                caller: { type: object }
                correlationId: { type: string }
                acquisitionId: { type: string }
              required: [dataSourceId, dataSourceUid, dataSourceGeneration, profile, sourceConfiguration, parameters, caller, correlationId, acquisitionId]
            outputSchema:
              type: object
              properties: { artifacts: { type: array } }
              required: [artifacts]
            steps:
              - { type: input, name: input, displayName: Request }
              - { type: output, name: output, displayName: Result, outputMapping: { artifacts: [] } }
            transitions: [{ id: complete, fromStep: input, event: completed, toStep: output }]
        """, $$"""
        apiVersion: agentstration.io/v1
        kind: DataSourceProfile
        metadata: { name: {{name}}, namespace: qa-inventory }
        definition:
          displayName: Fixture profile
          version: 2.4.0
          configurationSchema: { type: object }
          acquisitionFlow: { name: {{name}}, namespace: qa-inventory, version: 2.4.0, useActiveVersion: false }
          toolBindings: [{ name: read, capability: fixture.read, tool: { binding: fixture.ready } }]
        """], RequiredTools: ["fixture.ready"]);
}
