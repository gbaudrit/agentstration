using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Agentstration.Aep.Abstractions;
using Agentstration.Identity.Contracts;
using Agentstration.ModelProviders;
using Agentstration.Models;
using Agentstration.Models.Contracts;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Agentstration.Management.Tests;

public abstract class ResourceManagementApiTestBase
{
    protected static WebApplicationFactory<Program> Factory() => new ResourceApiTestFactory();

    protected static Task<RequestContext> GetBootstrapContextAsync(WebApplicationFactory<Program> factory) =>
        factory.Services
            .GetRequiredService<ILocalEnvironmentBootstrapper>()
            .EnsureInitializedAsync(default);

    protected static void SelectConsoleContext(HttpClient client, ResourceScopeRef scopeRef)
    {
        client.DefaultRequestHeaders.Remove("X-Agentstration-Context");
        client.DefaultRequestHeaders.Add("X-Agentstration-Context", scopeRef.Value);
    }

    private sealed class UnavailableModelProviderAdapter : IModelProviderDiscovery, IModelProviderCapabilitiesResolver, IExtensionInspector
    {
        private const string UnavailableDetails = "The test extension is intentionally unavailable.";

        public string ProviderType => AepModelProvider.AdapterType;
        public bool CanHandle(string providerType) => true;
        public bool CanInspectEndpoint(Uri endpoint) => true;

        public ValueTask<ModelProviderHealth> GetHealthAsync(
            ModelProviderConfiguration provider,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new ModelProviderHealth("unavailable", UnavailableDetails));

        public ValueTask<IReadOnlyList<DiscoveredModel>> ListModelsAsync(
            ModelProviderConfiguration provider,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<DiscoveredModel>>([]);

        public ValueTask<ResolvedModelProviderCapabilities> ResolveCapabilitiesAsync(
            ModelProviderConfiguration provider,
            ModelDeploymentConfiguration deployment,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new ResolvedModelProviderCapabilities(new(), new(), new()));

        public ValueTask<ExtensionInspection> InspectAsync(
            ModelProviderConfiguration provider,
            CancellationToken cancellationToken = default) =>
            InspectAsync(provider.Name, provider.Endpoint, cancellationToken);

        public ValueTask<ExtensionInspection> InspectAsync(
            string registrationName,
            Uri endpoint,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new ExtensionInspection(
                registrationName,
                endpoint,
                "unavailable",
                null,
                [],
                [],
                UnavailableDetails));
    }

    private sealed class ResourceApiTestFactory : ResourceApiOnlyWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("Agentstration:Authentication:DevelopmentGrantPlatformAdministrator", "true");
            builder.UseSetting("ConnectionStrings:ollama-extension", "Endpoint=http://127.0.0.1:1");
            AwpTestWorker.Configure(builder);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IModelProviderDiscovery>();
                services.RemoveAll<IModelProviderCapabilitiesResolver>();
                services.RemoveAll<IExtensionInspector>();
                services.AddSingleton<UnavailableModelProviderAdapter>();
                services.AddSingleton<IModelProviderDiscovery>(provider => provider.GetRequiredService<UnavailableModelProviderAdapter>());
                services.AddSingleton<IModelProviderCapabilitiesResolver>(provider => provider.GetRequiredService<UnavailableModelProviderAdapter>());
                services.AddSingleton<IExtensionInspector>(provider => provider.GetRequiredService<UnavailableModelProviderAdapter>());
            });
        }

    }
}

internal class ResourceApiOnlyWebApplicationFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Agentstration:Testing:ApiOnly", "true");
        builder.UseSetting("Logging:LogLevel:Default", "Warning");
    }
}
