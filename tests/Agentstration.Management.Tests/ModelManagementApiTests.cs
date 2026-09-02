using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Agentstration.Management.Abstractions;
using Agentstration.Management.Contracts;
using Agentstration.Management.Core;
using Agentstration.ModelProviders;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Agentstration.Management.Tests;

[TestClass]
public sealed partial class ModelManagementApiTests
{
    private static WebApplicationFactory<Program> Factory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:ollama-extension", "Endpoint=http://127.0.0.1:1");
            builder.UseSetting("Logging:LogLevel:Default", "Warning");
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
        });

    private static WebApplicationFactory<Program> DiagnosticFactory() => Factory().WithWebHostBuilder(builder =>
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IModelProviderDiscovery>();
            services.RemoveAll<IModelProviderCapabilitiesResolver>();
            services.AddSingleton<IModelProviderDiscovery, DiagnosticModelProvider>();
            services.AddSingleton<IModelProviderCapabilitiesResolver, DiagnosticModelProvider>();
        }));

    private static Task<RequestContext> GetBootstrapContextAsync(WebApplicationFactory<Program> factory) =>
        factory.Services
            .GetRequiredService<ILocalEnvironmentBootstrapper>()
            .EnsureInitializedAsync(default);

    private sealed class DiagnosticModelProvider : IModelProviderDiscovery, IModelProviderCapabilitiesResolver
    {
        public string ProviderType => "diagnostic";
        public bool CanHandle(string providerType) => true;

        public ValueTask<ModelProviderHealth> GetHealthAsync(ModelProviderConfiguration provider, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new ModelProviderHealth("available"));

        public ValueTask<IReadOnlyList<DiscoveredModel>> ListModelsAsync(ModelProviderConfiguration provider, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<DiscoveredModel>>([
                new("local-model", "Local model", "available", ["chat", "streaming", "structuredOutput"], new Dictionary<string, string>())
            ]);

        public ValueTask<ResolvedModelProviderCapabilities> ResolveCapabilitiesAsync(
            ModelProviderConfiguration provider,
            ModelDeploymentConfiguration deployment,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(new ResolvedModelProviderCapabilities(
                Capabilities(streaming: true, tools: true, structuredOutput: true, reasoning: true),
                Capabilities(streaming: true, tools: false, structuredOutput: true, reasoning: false),
                Capabilities(streaming: true, tools: true, structuredOutput: true, reasoning: true)));

        private static AgentRuntimeCapabilities Capabilities(bool streaming, bool tools, bool structuredOutput, bool reasoning) => new()
        {
            Streaming = new(streaming ? CapabilitySupport.Native : CapabilitySupport.Unsupported),
            Tools = new(tools ? CapabilitySupport.Native : CapabilitySupport.Unsupported),
            StructuredOutput = new(structuredOutput ? CapabilitySupport.Native : CapabilitySupport.Unsupported),
            Reasoning = new ReasoningCapability { Support = reasoning ? CapabilitySupport.Native : CapabilitySupport.Unsupported }
        };
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

    private sealed class ConfiguredEndpointInspector : IExtensionInspector
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
            ValueTask.FromResult(new ExtensionInspection(
                registrationName,
                endpoint,
                "available",
                new ExtensionIdentity(registrationName == "extension-discovered" ? "extension.discovered" : registrationName, "Discovered extension", "1.0.0", null),
                [new ExtensionContribution("model-provider", "discovered")],
                []));
    }

    private sealed class MigrationExtensionAdapter : IExtensionInspector, IExtensionOptionsMigrator
    {
        private static readonly JsonElement SourceSchema = JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new { legacyName = new { type = "string" } },
            required = new[] { "legacyName" },
            additionalProperties = false
        });
        private static readonly JsonElement TargetSchema = JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new { name = new { type = "string" } },
            required = new[] { "name" },
            additionalProperties = false
        });
        public static ExtensionOptionSet OptionSet { get; } = new(
            "io.agentstration.test/model-profile",
            "model-provider",
            "ollama",
            ExtensionOptionScopes.ModelProfile,
            "2.0.0",
            [
                new("1.0.0", ExtensionOptionSchemaDigest.Compute(SourceSchema), SourceSchema, false),
                new("2.0.0", ExtensionOptionSchemaDigest.Compute(TargetSchema), TargetSchema, false)
            ],
            [new("1.0.0", "2.0.0")]);

        public bool CanHandle(string providerType) => string.Equals(providerType, AepModelProvider.AdapterType, StringComparison.OrdinalIgnoreCase);
        public bool CanInspectEndpoint(Uri endpoint) => true;
        public ValueTask<ExtensionInspection> InspectAsync(ModelProviderConfiguration provider, CancellationToken cancellationToken = default) =>
            InspectAsync(provider.Name, provider.Endpoint, cancellationToken);
        public ValueTask<ExtensionInspection> InspectAsync(string registrationName, Uri endpoint, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new ExtensionInspection(
                registrationName,
                endpoint,
                "available",
                new ExtensionIdentity("migration.test", "Migration test", "2.0.0", null),
                [new ExtensionContribution("model-provider", "ollama")],
                [OptionSet]));
        public ValueTask<VersionedExtensionOptions> MigrateAsync(
            ModelProviderConfiguration provider,
            VersionedExtensionOptions source,
            string targetVersion,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = OptionSet.Versions.Single(value => value.Version == targetVersion);
            return ValueTask.FromResult(new VersionedExtensionOptions
            {
                OptionSet = source.OptionSet,
                Version = target.Version,
                SchemaDigest = target.SchemaDigest,
                Values = JsonSerializer.SerializeToElement(new { name = source.Values.GetProperty("legacyName").GetString() })
            });
        }
    }

    private static CreateModelProfileRequest Request(string name, string model) => new(
        name,
        new ModelProfileProperties
        {
            DisplayName = name,
            Description = "API test profile",
            Provider = new ResourceReference(ModelProviderManagementService.ModelProviderId("ollama-local")),
            Model = new ModelSelection { Name = model },
            Generation = new ModelGenerationOptions { Temperature = 0.3, MaxOutputTokens = 512 }
        });
}
