using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentstration.Aep.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Agentstration.Aep.AspNetCore;

public interface IAepModelProvider
{
    AepModelProviderDescriptor Descriptor { get; }
    Task<AepChatResponse> ChatAsync(AepChatRequest request, CancellationToken cancellationToken);
    IAsyncEnumerable<AepChatUpdate> ChatStreamingAsync(AepChatRequest request, CancellationToken cancellationToken);
    Task<IReadOnlyList<AepModelDescriptor>> ListModelsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<AepModelDescriptor>>(Descriptor.Models ?? []);
    Task<AepProviderHealth> GetHealthAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new AepProviderHealth("available"));
}

public interface IAepSourceProvider
{
    AepSourceProviderDescriptor Descriptor { get; }
    Task<AepSourceResolveResponse> ResolveAsync(AepSourceResolveRequest request, CancellationToken cancellationToken);
    Task<AepSourceMaterializeResponse> MaterializeAsync(AepSourceMaterializeRequest request, CancellationToken cancellationToken);
}

public sealed class AepExtensionOptions
{
    public AepExtensionIdentity Extension { get; set; } = new("agentstration.extension", "Agentstration extension", "1.0.0");
    public IDictionary<string, AepCapabilityDescriptor> Capabilities { get; } = new Dictionary<string, AepCapabilityDescriptor>(StringComparer.Ordinal);
    public IList<AepMcpServerDescriptor> McpServers { get; } = [];
    public IList<AepToolContribution> Tools { get; } = [];
    public IList<AepOptionSetDescriptor> OptionSets { get; } = [];
    public AepSourceMaterializationLimits SourceMaterializationLimits { get; set; } = new(
        MaxArchiveBytes: 64 * 1024 * 1024,
        MaxEntries: 10_000,
        MaxExpandedBytes: 256 * 1024 * 1024,
        TimeoutSeconds: 60);
}

public static class AepServerExtensions
{
    public static IServiceCollection AddAep(this IServiceCollection services, Action<AepExtensionOptions>? configure = null) =>
        services.AddAgentstrationAep(configure);

    public static IServiceCollection AddAgentstrationAep(this IServiceCollection services, Action<AepExtensionOptions>? configure = null)
    {
        services.AddOptions<AepExtensionOptions>();
        services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));
        if (configure is not null) services.Configure(configure);
        services.AddHealthChecks();
        return services;
    }

    public static IServiceCollection AddModelProvider<TProvider>(this IServiceCollection services)
        where TProvider : class, IAepModelProvider => services.AddSingleton<IAepModelProvider, TProvider>();

    public static IServiceCollection AddSourceProvider<TProvider>(this IServiceCollection services)
        where TProvider : class, IAepSourceProvider => services.AddSingleton<IAepSourceProvider, TProvider>();

    public static IServiceCollection AddOptionMigrator<TMigrator>(this IServiceCollection services)
        where TMigrator : class, IAepOptionMigrator => services.AddSingleton<IAepOptionMigrator, TMigrator>();

    public static IEndpointRouteBuilder MapAgentstrationAep(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(AepProtocol.DiscoveryPath, (IOptions<AepExtensionOptions> options, IEnumerable<IAepModelProvider> providers, IEnumerable<IAepSourceProvider> sourceProviders, IEnumerable<IAepOptionMigrator> migrators) =>
            Results.Json(CreateManifest(options.Value, providers, sourceProviders, migrators), AepProtocol.JsonOptions));
        endpoints.MapGet(AepProtocol.LegacyDiscoveryPath, (IOptions<AepExtensionOptions> options, IEnumerable<IAepModelProvider> providers, IEnumerable<IAepSourceProvider> sourceProviders, IEnumerable<IAepOptionMigrator> migrators) =>
            Results.Json(CreateManifest(options.Value, providers, sourceProviders, migrators), AepProtocol.JsonOptions));
        endpoints.MapGet(AepProtocol.HealthPath, () => Results.Json(new AepHealth("available"), AepProtocol.JsonOptions));
        endpoints.MapGet(AepProtocol.ModelProvidersPath, (IEnumerable<IAepModelProvider> providers) =>
            Results.Json(providers.Select(value => value.Descriptor).ToArray(), AepProtocol.JsonOptions));
        endpoints.MapGet(AepProtocol.SourceProvidersPath, (IEnumerable<IAepSourceProvider> providers) =>
            Results.Json(providers.Select(value => value.Descriptor).ToArray(), AepProtocol.JsonOptions));
        endpoints.MapGet(AepProtocol.ConfigurationPath, (IOptions<AepExtensionOptions> options, IEnumerable<IAepOptionMigrator> migrators) =>
            Results.Json(CreateConfigurationCatalog(options.Value.OptionSets, migrators), AepProtocol.JsonOptions));
        endpoints.MapPost(AepProtocol.ConfigurationMigrationPath, MigrateOptionsAsync);
        endpoints.MapPost($"{AepProtocol.ModelProvidersPath}/{{providerId}}/chat", ChatAsync);
        endpoints.MapPost($"{AepProtocol.ModelProvidersPath}/{{providerId}}/chat/stream", StreamAsync);
        endpoints.MapGet($"{AepProtocol.ModelProvidersPath}/{{providerId}}/models", ListModelsAsync);
        endpoints.MapGet($"{AepProtocol.ModelProvidersPath}/{{providerId}}/health", ProviderHealthAsync);
        endpoints.MapPost($"{AepProtocol.SourceProvidersPath}/{{providerId}}/resolve", ResolveSourceAsync);
        endpoints.MapPost($"{AepProtocol.SourceProvidersPath}/{{providerId}}/materialize", MaterializeSourceAsync);
        endpoints.MapHealthChecks("/health");
        return endpoints;
    }

    public static IEndpointRouteBuilder MapAep(this IEndpointRouteBuilder endpoints) => endpoints.MapAgentstrationAep();

    private static AepManifest CreateManifest(
        AepExtensionOptions options,
        IEnumerable<IAepModelProvider> providers,
        IEnumerable<IAepSourceProvider> sourceProviders,
        IEnumerable<IAepOptionMigrator> migrators)
    {
        var modelProviders = providers.Select(value => value.Descriptor).ToArray();
        var sources = sourceProviders.Select(value => value.Descriptor).ToArray();
        ValidateOptionSets(options.OptionSets, modelProviders, sources, migrators);
        var capabilities = new Dictionary<string, AepCapabilityDescriptor>(options.Capabilities, StringComparer.Ordinal)
        {
            [AepCapabilityNames.Health] = new("1.0", AepProtocol.HealthPath)
        };
        if (modelProviders.Length > 0) capabilities[AepCapabilityNames.ModelProvider] = new("1.0", AepProtocol.ModelProvidersPath);
        if (sources.Length > 0) capabilities[AepCapabilityNames.SourceProvider] = new("1.0", AepProtocol.SourceProvidersPath);
        if (options.Tools.Count > 0) capabilities[AepCapabilityNames.Tools] = new("1.0");
        if (options.OptionSets.Count > 0) capabilities[AepCapabilityNames.Configuration] = new("1.0", AepProtocol.ConfigurationPath);
        var descriptor = new AepManifest(
            AepProtocol.Version,
            options.Extension,
            capabilities,
            new AepContributions(modelProviders, options.Tools.ToArray(), sources),
            options.McpServers.Count == 0 ? null : new AepMcpDescriptor(options.McpServers.ToArray()));
        var errors = AepDescriptorValidator.Validate(descriptor);
        if (errors.Count > 0) throw new InvalidOperationException($"The AEP extension descriptor is invalid: {string.Join(" ", errors)}");
        return descriptor;
    }

    private static void ValidateOptionSets(
        ICollection<AepOptionSetDescriptor> optionSets,
        IReadOnlyCollection<AepModelProviderDescriptor> modelProviders,
        IReadOnlyCollection<AepSourceProviderDescriptor> sourceProviders,
        IEnumerable<IAepOptionMigrator> migrators)
    {
        var duplicate = optionSets.GroupBy(value => value.Id, StringComparer.Ordinal).FirstOrDefault(value => value.Count() > 1);
        if (duplicate is not null) throw new InvalidOperationException($"AEP option set '{duplicate.Key}' is declared more than once.");
        foreach (var optionSet in optionSets)
        {
            if (string.IsNullOrWhiteSpace(optionSet.Id)) throw new InvalidOperationException("An AEP option set id is required.");
            var knownContribution = string.Equals(optionSet.ContributionKind, AepContributionKinds.ModelProvider, StringComparison.Ordinal)
                && modelProviders.Any(value => string.Equals(value.Id, optionSet.ContributionId, StringComparison.OrdinalIgnoreCase))
                || string.Equals(optionSet.ContributionKind, AepContributionKinds.SourceProvider, StringComparison.Ordinal)
                && sourceProviders.Any(value => string.Equals(value.Id, optionSet.ContributionId, StringComparison.OrdinalIgnoreCase));
            var expectedScope = string.Equals(optionSet.ContributionKind, AepContributionKinds.ModelProvider, StringComparison.Ordinal)
                ? AepOptionScopes.ModelProfile
                : AepOptionScopes.SourceChannel;
            if (!knownContribution)
                throw new InvalidOperationException($"AEP option set '{optionSet.Id}' targets an unknown contribution.");
            if (!string.Equals(optionSet.Scope, expectedScope, StringComparison.Ordinal))
                throw new InvalidOperationException($"AEP option set '{optionSet.Id}' has an invalid scope for contribution kind '{optionSet.ContributionKind}'.");
            if (optionSet.Versions.Count == 0
                || !optionSet.Versions.Any(value => string.Equals(value.Version, optionSet.PreferredVersion, StringComparison.Ordinal)))
                throw new InvalidOperationException($"AEP option set '{optionSet.Id}' must contain its preferred version.");
            if (optionSet.Versions.Select(value => value.Version).Distinct(StringComparer.Ordinal).Count() != optionSet.Versions.Count)
                throw new InvalidOperationException($"AEP option set '{optionSet.Id}' contains duplicate versions.");
            foreach (var version in optionSet.Versions)
            {
                var actual = AepSchemaDigest.Compute(version.Schema);
                if (!string.Equals(actual, version.SchemaDigest, StringComparison.Ordinal))
                    throw new InvalidOperationException($"AEP option set '{optionSet.Id}' version '{version.Version}' has an invalid schema digest.");
            }
        }
        var migrationKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var migration in migrators)
        {
            var optionSet = optionSets.SingleOrDefault(value => string.Equals(value.Id, migration.OptionSet, StringComparison.Ordinal))
                ?? throw new InvalidOperationException($"AEP option migration targets unknown option set '{migration.OptionSet}'.");
            if (!optionSet.Versions.Any(value => string.Equals(value.Version, migration.FromVersion, StringComparison.Ordinal))
                || !optionSet.Versions.Any(value => string.Equals(value.Version, migration.ToVersion, StringComparison.Ordinal)))
                throw new InvalidOperationException($"AEP option migration '{migration.OptionSet}' {migration.FromVersion} -> {migration.ToVersion} targets an unknown version.");
            if (string.Equals(migration.FromVersion, migration.ToVersion, StringComparison.Ordinal)
                || !migrationKeys.Add($"{migration.OptionSet}\n{migration.FromVersion}\n{migration.ToVersion}"))
                throw new InvalidOperationException($"AEP option migration '{migration.OptionSet}' {migration.FromVersion} -> {migration.ToVersion} is invalid or duplicated.");
        }
    }

    private static AepConfigurationCatalog CreateConfigurationCatalog(
        IEnumerable<AepOptionSetDescriptor> optionSets,
        IEnumerable<IAepOptionMigrator> migrators)
    {
        var migrations = migrators.ToArray();
        return new(optionSets.Select(optionSet => optionSet with
        {
            Migrations = migrations
                .Where(value => string.Equals(value.OptionSet, optionSet.Id, StringComparison.Ordinal))
                .Select(value => new AepOptionMigrationDescriptor(value.FromVersion, value.ToVersion))
                .OrderBy(value => value.FromVersion, StringComparer.Ordinal)
                .ThenBy(value => value.ToVersion, StringComparer.Ordinal)
                .ToArray()
        }).ToArray());
    }

    private static async Task<IResult> MigrateOptionsAsync(
        AepOptionMigrationRequest request,
        IOptions<AepExtensionOptions> options,
        IEnumerable<IAepOptionMigrator> migrators,
        CancellationToken cancellationToken)
    {
        try
        {
            var optionSet = options.Value.OptionSets.SingleOrDefault(value => string.Equals(value.Id, request.OptionSet, StringComparison.Ordinal))
                ?? throw new AepServerException("option_set_unsupported", $"Option set '{request.OptionSet}' is not supported.", StatusCodes.Status422UnprocessableEntity);
            var source = optionSet.Versions.SingleOrDefault(value => string.Equals(value.Version, request.FromVersion, StringComparison.Ordinal))
                ?? throw new AepServerException("option_version_unsupported", $"Source version '{request.FromVersion}' is not supported.", StatusCodes.Status422UnprocessableEntity);
            var target = optionSet.Versions.SingleOrDefault(value => string.Equals(value.Version, request.ToVersion, StringComparison.Ordinal))
                ?? throw new AepServerException("option_version_unsupported", $"Target version '{request.ToVersion}' is not supported.", StatusCodes.Status422UnprocessableEntity);
            if (!string.Equals(source.SchemaDigest, request.FromSchemaDigest, StringComparison.Ordinal))
                throw new AepServerException("option_schema_mismatch", "The source schema digest does not match the extension contract.", StatusCodes.Status422UnprocessableEntity);
            ValidateMigrationValues(request.Values, source, "source");
            var path = FindMigrationPath(request.OptionSet, request.FromVersion, request.ToVersion, migrators);
            if (path.Length == 0 && !string.Equals(request.FromVersion, request.ToVersion, StringComparison.Ordinal))
                throw new AepServerException("option_migration_unsupported", $"No migration path exists from '{request.FromVersion}' to '{request.ToVersion}'.", StatusCodes.Status422UnprocessableEntity);
            var values = request.Values.Clone();
            foreach (var migration in path)
            {
                try { values = (await migration.MigrateAsync(values, cancellationToken)).Clone(); }
                catch (AepServerException) { throw; }
                catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    throw new AepServerException("option_migration_failed", $"Migration from '{migration.FromVersion}' to '{migration.ToVersion}' failed.", StatusCodes.Status422UnprocessableEntity, exception);
                }
                var version = optionSet.Versions.Single(value => string.Equals(value.Version, migration.ToVersion, StringComparison.Ordinal));
                ValidateMigrationValues(values, version, "result");
            }
            return Results.Json(new AepOptionMigrationResponse(
                new AepVersionedOptions(optionSet.Id, target.Version, target.SchemaDigest, values)), AepProtocol.JsonOptions);
        }
        catch (AepServerException exception) { return Error(exception.StatusCode, exception.Code, exception.Message); }
    }

    private static void ValidateMigrationValues(JsonElement values, AepOptionSetVersionDescriptor version, string role)
    {
        if (values.ValueKind != JsonValueKind.Object)
            throw new AepServerException("invalid_options", $"The migration {role} values must be a JSON object.", StatusCodes.Status422UnprocessableEntity);
        var issues = AepOptionSchemaValidator.Validate(values, version.Schema);
        if (issues.Count > 0)
            throw new AepServerException("invalid_options", string.Join(" ", issues.Select(value => value.Message)), StatusCodes.Status422UnprocessableEntity);
    }

    private static IAepOptionMigrator[] FindMigrationPath(
        string optionSet,
        string fromVersion,
        string toVersion,
        IEnumerable<IAepOptionMigrator> migrators)
    {
        if (string.Equals(fromVersion, toVersion, StringComparison.Ordinal)) return [];
        var edges = migrators.Where(value => string.Equals(value.OptionSet, optionSet, StringComparison.Ordinal)).ToArray();
        var queue = new Queue<(string Version, IAepOptionMigrator[] Path)>();
        var visited = new HashSet<string>(StringComparer.Ordinal) { fromVersion };
        queue.Enqueue((fromVersion, []));
        while (queue.TryDequeue(out var current))
        {
            foreach (var edge in edges.Where(value => string.Equals(value.FromVersion, current.Version, StringComparison.Ordinal)))
            {
                var path = current.Path.Append(edge).ToArray();
                if (string.Equals(edge.ToVersion, toVersion, StringComparison.Ordinal)) return path;
                if (visited.Add(edge.ToVersion)) queue.Enqueue((edge.ToVersion, path));
            }
        }
        return [];
    }

    private static async Task<IResult> ResolveSourceAsync(
        string providerId,
        AepSourceResolveRequest request,
        IEnumerable<IAepSourceProvider> providers,
        IOptions<AepExtensionOptions> options,
        CancellationToken cancellationToken)
    {
        var provider = FindSource(providers, providerId);
        if (provider is null) return Error(StatusCodes.Status404NotFound, "source_provider_unavailable", $"Source provider '{providerId}' is not registered.");
        try
        {
            ValidateSourceOptions(providerId, request.Configuration, options.Value.OptionSets);
            var response = await provider.ResolveAsync(request, cancellationToken);
            ValidateResolvedSource(response);
            return Results.Json(response, AepProtocol.JsonOptions);
        }
        catch (AepServerException exception) { return Error(exception.StatusCode, exception.Code, exception.Message); }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return Error(StatusCodes.Status502BadGateway, "source_provider_failed", $"Source provider '{providerId}' failed to resolve the channel.");
        }
    }

    private static async Task<IResult> MaterializeSourceAsync(
        string providerId,
        AepSourceMaterializeRequest request,
        IEnumerable<IAepSourceProvider> providers,
        IOptions<AepExtensionOptions> options,
        CancellationToken cancellationToken)
    {
        var provider = FindSource(providers, providerId);
        if (provider is null) return Error(StatusCodes.Status404NotFound, "source_provider_unavailable", $"Source provider '{providerId}' is not registered.");
        try
        {
            ValidateSourceOptions(providerId, request.Configuration, options.Value.OptionSets);
            ArgumentException.ThrowIfNullOrWhiteSpace(request.Revision);
            var limits = RestrictLimits(request.Limits, options.Value.SourceMaterializationLimits);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(limits.TimeoutSeconds));
            AepSourceMaterializeResponse response;
            try
            {
                response = await provider.MaterializeAsync(request with { Limits = limits }, timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
            {
                throw new AepServerException("source_provider_timeout", "Source materialization exceeded its time limit.", StatusCodes.Status504GatewayTimeout);
            }
            ValidateMaterialization(request.Revision, response, limits);
            return Results.Json(response, AepProtocol.JsonOptions);
        }
        catch (AepServerException exception) { return Error(exception.StatusCode, exception.Code, exception.Message); }
        catch (ArgumentException exception) { return Error(StatusCodes.Status422UnprocessableEntity, "invalid_request", exception.Message); }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return Error(StatusCodes.Status502BadGateway, "source_provider_failed", $"Source provider '{providerId}' failed to materialize the channel.");
        }
    }

    private static AepSourceMaterializationLimits RestrictLimits(
        AepSourceMaterializationLimits requested,
        AepSourceMaterializationLimits server)
    {
        if (requested.MaxArchiveBytes <= 0 || requested.MaxEntries <= 0 || requested.MaxExpandedBytes <= 0 || requested.TimeoutSeconds <= 0)
            throw new AepServerException("invalid_limits", "All source materialization limits must be positive.", StatusCodes.Status422UnprocessableEntity);
        if (server.MaxArchiveBytes <= 0 || server.MaxEntries <= 0 || server.MaxExpandedBytes <= 0 || server.TimeoutSeconds <= 0)
            throw new InvalidOperationException("AEP source materialization server limits must be positive.");
        return new(
            Math.Min(requested.MaxArchiveBytes, server.MaxArchiveBytes),
            Math.Min(requested.MaxEntries, server.MaxEntries),
            Math.Min(requested.MaxExpandedBytes, server.MaxExpandedBytes),
            Math.Min(requested.TimeoutSeconds, server.TimeoutSeconds));
    }

    private static void ValidateResolvedSource(AepSourceResolveResponse response)
    {
        if (response is null || string.IsNullOrWhiteSpace(response.Revision))
            throw new AepServerException("invalid_source_response", "The source provider returned an empty immutable revision.");
        ValidateIntegrity(response.Integrity, null, requireSha256: false);
    }

    private static void ValidateMaterialization(
        string requestedRevision,
        AepSourceMaterializeResponse response,
        AepSourceMaterializationLimits limits)
    {
        if (response is null || response.Archive is null)
            throw new AepServerException("invalid_source_response", "The source provider returned an empty materialization.");
        if (!string.Equals(requestedRevision, response.Revision, StringComparison.Ordinal))
            throw new AepServerException("source_revision_mismatch", "The materialized revision does not match the exact requested revision.");
        if (string.IsNullOrWhiteSpace(response.Archive.MediaType))
            throw new AepServerException("invalid_source_response", "The source archive media type is required.");
        if (response.Archive.Content.LongLength > limits.MaxArchiveBytes)
            throw new AepServerException("source_archive_too_large", "The source archive exceeds the negotiated compressed size limit.", StatusCodes.Status413PayloadTooLarge);
        if (response.Archive.EntryCount < 0 || response.Archive.EntryCount > limits.MaxEntries)
            throw new AepServerException("source_archive_too_many_entries", "The source archive exceeds the negotiated entry limit.", StatusCodes.Status413PayloadTooLarge);
        if (response.Archive.ExpandedBytes < 0 || response.Archive.ExpandedBytes > limits.MaxExpandedBytes)
            throw new AepServerException("source_archive_expanded_too_large", "The source archive exceeds the negotiated expanded size limit.", StatusCodes.Status413PayloadTooLarge);
        ValidateIntegrity(response.Archive.Integrity, response.Archive.Content, requireSha256: true);
    }

    private static void ValidateIntegrity(AepContentIntegrity integrity, byte[]? content, bool requireSha256)
    {
        if (integrity is null || string.IsNullOrWhiteSpace(integrity.Algorithm) || string.IsNullOrWhiteSpace(integrity.Digest))
            throw new AepServerException("invalid_source_integrity", "Source integrity algorithm and digest are required.");
        if (requireSha256
            && (!string.Equals(integrity.Algorithm, "sha256", StringComparison.Ordinal)
                || integrity.Digest.Length != 64
                || !integrity.Digest.All(Uri.IsHexDigit)))
            throw new AepServerException("invalid_source_integrity", "Source archive integrity must contain a SHA-256 digest.");
        if (content is not null
            && !string.Equals(AepContentIntegrity.Sha256(content).Digest, integrity.Digest, StringComparison.OrdinalIgnoreCase))
            throw new AepServerException("source_integrity_mismatch", "The source archive digest does not match its content.");
    }

    private static void ValidateSourceOptions(
        string providerId,
        AepVersionedOptions options,
        IEnumerable<AepOptionSetDescriptor> optionSets) =>
        ValidateVersionedOptions(providerId, options, optionSets, AepContributionKinds.SourceProvider, AepOptionScopes.SourceChannel);

    private static async Task<IResult> ProviderHealthAsync(string providerId, IEnumerable<IAepModelProvider> providers, CancellationToken cancellationToken)
    {
        var provider = Find(providers, providerId);
        if (provider is null) return Error(StatusCodes.Status404NotFound, "provider_unavailable", $"Model provider '{providerId}' is not registered.");
        return Results.Json(await provider.GetHealthAsync(cancellationToken), AepProtocol.JsonOptions);
    }

    private static async Task<IResult> ListModelsAsync(string providerId, IEnumerable<IAepModelProvider> providers, CancellationToken cancellationToken)
    {
        var provider = Find(providers, providerId);
        if (provider is null) return Error(StatusCodes.Status404NotFound, "provider_unavailable", $"Model provider '{providerId}' is not registered.");
        try { return Results.Json(await provider.ListModelsAsync(cancellationToken), AepProtocol.JsonOptions); }
        catch (AepServerException exception) { return Error(exception.StatusCode, exception.Code, exception.Message); }
    }

    private static async Task<IResult> ChatAsync(
        string providerId,
        AepChatRequest request,
        IEnumerable<IAepModelProvider> providers,
        IOptions<AepExtensionOptions> options,
        CancellationToken cancellationToken)
    {
        var provider = Find(providers, providerId);
        if (provider is null) return Error(StatusCodes.Status404NotFound, "provider_unavailable", $"Model provider '{providerId}' is not registered.");
        try
        {
            ValidateNativeOptions(providerId, request.Options?.NativeOptions, options.Value.OptionSets);
            return Results.Json(await provider.ChatAsync(request, cancellationToken), AepProtocol.JsonOptions);
        }
        catch (AepServerException exception) { return Error(exception.StatusCode, exception.Code, exception.Message); }
    }

    private static async Task StreamAsync(
        string providerId,
        AepChatRequest request,
        IEnumerable<IAepModelProvider> providers,
        IOptions<AepExtensionOptions> options,
        HttpResponse response,
        CancellationToken cancellationToken)
    {
        var provider = Find(providers, providerId);
        if (provider is null)
        {
            response.StatusCode = StatusCodes.Status404NotFound;
            await response.WriteAsJsonAsync(new AepErrorResponse(new AepError("provider_unavailable", $"Model provider '{providerId}' is not registered.")), AepProtocol.JsonOptions, cancellationToken);
            return;
        }
        try { ValidateNativeOptions(providerId, request.Options?.NativeOptions, options.Value.OptionSets); }
        catch (AepServerException exception)
        {
            response.StatusCode = exception.StatusCode;
            await response.WriteAsJsonAsync(new AepErrorResponse(new AepError(exception.Code, exception.Message)), AepProtocol.JsonOptions, cancellationToken);
            return;
        }
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache";
        try
        {
            await foreach (var update in provider.ChatStreamingAsync(request, cancellationToken).WithCancellation(cancellationToken))
            {
                await response.WriteAsync("data: ", cancellationToken);
                await JsonSerializer.SerializeAsync(response.Body, update, AepProtocol.JsonOptions, cancellationToken);
                await response.WriteAsync("\n\n", cancellationToken);
                await response.Body.FlushAsync(cancellationToken);
                if (update.FinishReason is not null) break;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private static IAepModelProvider? Find(IEnumerable<IAepModelProvider> providers, string id) =>
        providers.FirstOrDefault(value => string.Equals(value.Descriptor.Id, id, StringComparison.OrdinalIgnoreCase));

    private static IAepSourceProvider? FindSource(IEnumerable<IAepSourceProvider> providers, string id) =>
        providers.FirstOrDefault(value => string.Equals(value.Descriptor.Id, id, StringComparison.OrdinalIgnoreCase));

    private static void ValidateNativeOptions(
        string providerId,
        AepVersionedOptions? nativeOptions,
        IEnumerable<AepOptionSetDescriptor> optionSets)
    {
        if (nativeOptions is null) return;
        ValidateVersionedOptions(providerId, nativeOptions, optionSets, AepContributionKinds.ModelProvider, AepOptionScopes.ModelProfile);
    }

    private static void ValidateVersionedOptions(
        string providerId,
        AepVersionedOptions nativeOptions,
        IEnumerable<AepOptionSetDescriptor> optionSets,
        string contributionKind,
        string scope)
    {
        var optionSet = optionSets.SingleOrDefault(value =>
            string.Equals(value.Id, nativeOptions.OptionSet, StringComparison.Ordinal)
            && string.Equals(value.ContributionKind, contributionKind, StringComparison.Ordinal)
            && string.Equals(value.ContributionId, providerId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(value.Scope, scope, StringComparison.Ordinal));
        if (optionSet is null)
            throw new AepServerException("option_set_unsupported", $"Option set '{nativeOptions.OptionSet}' is not supported by model provider '{providerId}'.", StatusCodes.Status422UnprocessableEntity);
        var version = optionSet.Versions.SingleOrDefault(value => string.Equals(value.Version, nativeOptions.Version, StringComparison.Ordinal));
        if (version is null)
            throw new AepServerException("option_version_unsupported", $"Option set '{nativeOptions.OptionSet}' version '{nativeOptions.Version}' is not supported.", StatusCodes.Status422UnprocessableEntity);
        if (!string.Equals(version.SchemaDigest, nativeOptions.SchemaDigest, StringComparison.Ordinal))
            throw new AepServerException("option_schema_mismatch", $"Option set '{nativeOptions.OptionSet}' version '{nativeOptions.Version}' has an unexpected schema digest.", StatusCodes.Status422UnprocessableEntity);
        if (nativeOptions.Values.ValueKind != JsonValueKind.Object)
            throw new AepServerException("invalid_options", "Native option values must be a JSON object.", StatusCodes.Status422UnprocessableEntity);
        var issues = AepOptionSchemaValidator.Validate(nativeOptions.Values, version.Schema);
        if (issues.Count > 0)
            throw new AepServerException(
                "invalid_options",
                string.Join(" ", issues.Select(value => value.Message)),
                StatusCodes.Status422UnprocessableEntity);
    }

    private static IResult Error(int status, string code, string message) =>
        Results.Json(new AepErrorResponse(new AepError(code, message)), AepProtocol.JsonOptions, statusCode: status);
}

public sealed class AepServerException(string code, string message, int statusCode = StatusCodes.Status502BadGateway, Exception? innerException = null)
    : Exception(message, innerException)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
}
