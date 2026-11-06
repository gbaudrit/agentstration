using Agentstration.Infrastructure.Knowledge;
using Agentstration.Infrastructure.Sources;
using Agentstration.Sources;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Infrastructure;

internal static class SourcesServiceCollectionExtensions
{
    internal static IServiceCollection AddAgentstrationSources(this IServiceCollection services, AgentstrationCompositionContext context)
    {
        var dataDirectory = context.Options.DataDirectory;
        var sourceVerificationIndexOptions = context.Options.SourceVerificationIndex;
        var sourceRegistryTransportOptions = context.Options.SourceRegistryTransport;
        services.AddSingleton<ISourceScopeResolver, SourceScopeResolver>();
        services.AddSingleton<Agentstration.Sources.SourceManifestValidator>();
        services.AddSingleton<ISourceManifestReader, Agentstration.Sources.SourceManifestReader>();
        services.AddSingleton<ISourceVerificationIndexReader, Agentstration.Sources.SourceVerificationIndexReader>();
        services.AddHttpClient<ISourceManifestRetriever, HttpSourceManifestRetriever>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(15);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Agentstration-Source-Importer/1.0");
        }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5
        });
        services.AddSingleton(sourceVerificationIndexOptions);
        services.AddHttpClient<ISourceVerificationIndexProvider, HttpSourceVerificationIndexProvider>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(sourceVerificationIndexOptions.TimeoutSeconds);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Agentstration-Source-Verification/1.0");
        }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5
        });
        services.AddSingleton<SourceManagementService>();
        services.AddSingleton<SourceVerificationService>();
        services.AddSingleton<SourceBindingManagementService>();
        services.AddSingleton<IAgentstrationVersionProvider, AssemblyAgentstrationVersionProvider>();
        services.AddSingleton<SourceChannelCompatibilityEvaluator>();
        services.AddSingleton<ISourceSnapshotArtifactStore>(_ => new FileSystemSourceSnapshotArtifactStore(Path.Combine(dataDirectory, "source-snapshots")));
        services.AddSingleton<ISourceSnapshotContentReader, ZipSourceSnapshotContentReader>();
        services.AddSingleton<ISourceCatalogManifestReader, Agentstration.Sources.SourceCatalogManifestReader>();
        services.AddSingleton(new SourceMaterializationLimits());
        services.AddSingleton<SourceChannelSnapshotService>();
        services.AddSingleton<SourceRefreshScheduler>();
        services.AddSingleton<ISourceCatalogContentResolver, SourceCatalogContentResolver>();
        services.AddSingleton<SourceCatalogService>();
        services.AddSingleton<ISourceRegistryIndexReader, SourceRegistryIndexReader>();
        services.AddSingleton<ISourceRegistryReader, SourceRegistryReader>();
        services.AddSingleton<ISourceRegistryReferenceResolver, SourceRegistryRuntimeReferenceResolver>();
        services.AddSingleton(sourceRegistryTransportOptions);
        services.AddHttpClient<ISourceRegistryDocumentRetriever, HttpSourceRegistryDocumentRetriever>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(sourceRegistryTransportOptions.TimeoutSeconds);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Agentstration-Source-Registry/1.0");
        }).ConfigurePrimaryHttpMessageHandler(() => HttpSourceRegistryDocumentRetriever.CreatePrimaryHandler(sourceRegistryTransportOptions));
        services.AddSingleton<ISourceRegistryCacheStore>(_ => new FileSystemSourceRegistryCacheStore(Path.Combine(dataDirectory, "source-registry-cache")));
        services.AddSingleton<SourceRegistryManagementService>();
        services.AddSingleton<SourceRegistryTrustEvaluationService>();
        services.AddSingleton<SourceRegistryDiscoveryService>();
        services.AddSingleton<ISourceVerificationEvidenceProvider>(services => services.GetRequiredService<SourceRegistryTrustEvaluationService>());
        return services;
    }
}
