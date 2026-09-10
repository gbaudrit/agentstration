using Agentstration.Infrastructure.Sources;
using Agentstration.Management.Abstractions;
using Agentstration.Management.Contracts;
using Agentstration.Management.Core;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Infrastructure;

internal static class SourceServiceCollectionExtensions
{
    internal static IServiceCollection AddAgentstrationSources(
        this IServiceCollection services,
        AgentstrationServiceRegistrationContext context)
    {
        var verificationOptions = context.SourceVerificationIndexOptions;
        if (verificationOptions.TimeoutSeconds is < 1 or > 60)
            throw new InvalidOperationException("Source verification index timeout must be between 1 and 60 seconds.");
        if (verificationOptions.MaximumBytes is < 1024 or > SourceVerificationIndexReader.MaximumIndexBytes)
            throw new InvalidOperationException(
                $"Source verification index maximum bytes must be between 1024 and {SourceVerificationIndexReader.MaximumIndexBytes}.");

        services.AddSingleton<SourceManifestValidator>();
        services.AddSingleton<ISourceManifestReader, SourceManifestReader>();
        services.AddSingleton<ISourceVerificationIndexReader, SourceVerificationIndexReader>();
        services.AddHttpClient<ISourceManifestRetriever, HttpSourceManifestRetriever>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(15);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Agentstration-Source-Importer/1.0");
        }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5
        });
        services.AddSingleton(verificationOptions);
        services.AddHttpClient<ISourceVerificationIndexProvider, HttpSourceVerificationIndexProvider>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(verificationOptions.TimeoutSeconds);
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
        services.AddSingleton<ISourceSnapshotArtifactStore>(_ =>
            new FileSystemSourceSnapshotArtifactStore(Path.Combine(context.DataDirectory, "source-snapshots")));
        services.AddSingleton<ISourceSnapshotContentReader, ZipSourceSnapshotContentReader>();
        services.AddSingleton<ISourceCatalogManifestReader, SourceCatalogManifestReader>();
        services.AddSingleton(new SourceMaterializationLimits());
        services.AddSingleton<SourceChannelSnapshotService>();
        services.AddSingleton<SourceCatalogService>();
        return services;
    }
}
