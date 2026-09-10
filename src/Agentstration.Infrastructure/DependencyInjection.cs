using Agentstration.Application.Work;
using Agentstration.Flow.Application;
using Agentstration.Flow.Storage.PostgreSql;
using Agentstration.Flow.Storage.Sqlite;
using Agentstration.Infrastructure.Agents;
using Agentstration.Infrastructure.Artifacts;
using Agentstration.Infrastructure.Bootstrap;
using Agentstration.Infrastructure.Events;
using Agentstration.Infrastructure.Flows;
using Agentstration.Infrastructure.Packs;
using Agentstration.Infrastructure.Runtime;
using Agentstration.Infrastructure.Sources;
using Agentstration.Infrastructure.Triggers;
using Agentstration.Infrastructure.Work;
using Agentstration.Management.Abstractions;
using Agentstration.Management.Contracts;
using Agentstration.Management.Core;
using Agentstration.Management.Storage.PostgreSql;
using Agentstration.Management.Storage.Sqlite;
using Agentstration.ModelProviders;
using Agentstration.Runtime.Abstractions;
using Agentstration.Runtime.AgentFramework;
using Agentstration.Runtime.Core;
using Agentstration.Runtime.Local;
using Agentstration.Runtime.Storage.PostgreSql;
using Agentstration.Runtime.Storage.Sqlite;
using Agentstration.Secrets.Abstractions;
using Agentstration.Secrets.Local;
using Agentstration.Tools.Mcp;
using Agentstration.Work;
using Agentstration.Work.Storage.Abstractions;
using Agentstration.Work.Storage.PostgreSql;
using Agentstration.Work.Storage.Sqlite;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Quartz;

namespace Agentstration.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddAgentstration(
        this IServiceCollection services,
        string dataDirectory,
        AiProviderOptions? aiOptions = null,
        string? controlPlaneConnectionString = null,
        string? workPlaneConnectionString = null,
        string? flowConnectionString = null,
        string? runtimeConnectionString = null,
        AgentstrationStorageOptions? storageOptions = null,
        bool enableHostedServices = true,
        SourceVerificationIndexOptions? sourceVerificationIndexOptions = null,
        SourceRegistryTransportOptions? sourceRegistryTransportOptions = null)
    {
        services.AddSingleton(TimeProvider.System);
        services.TryAddSingleton<LocalBootstrapOptions>();
        services.TryAddSingleton<CurrentRequestContext>();
        services.TryAddSingleton<ICurrentRequestContext>(provider => provider.GetRequiredService<CurrentRequestContext>());
        services.TryAddSingleton<IRequestContextScopeFactory>(provider => provider.GetRequiredService<CurrentRequestContext>());
        services.TryAddSingleton(new GenAiObservabilityOptions());
        services.TryAddTransient<GenAiHttpPayloadCaptureHandler>();
        services.AddSingleton<IManagementEventPublisher, InProcessManagementEventPublisher>();
        aiOptions ??= new AiProviderOptions("Deterministic", new Uri("http://localhost/"), "deterministic", null);
        services.AddSingleton(aiOptions);
        var useManagedProfileResolver = string.Equals(aiOptions.Provider, "Managed", StringComparison.OrdinalIgnoreCase);
        if (string.Equals(aiOptions.Provider, "Deterministic", StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<IChatClient, DeterministicChatClient>();
        }
        else if (!useManagedProfileResolver)
        {
            services.AddHttpClient<OpenAiCompatibleChatClient>(client => client.Timeout = TimeSpan.FromSeconds(90))
                .AddHttpMessageHandler<GenAiHttpPayloadCaptureHandler>();
            services.AddSingleton<IChatClient>(provider => provider.GetRequiredService<OpenAiCompatibleChatClient>());
        }
        storageOptions ??= new AgentstrationStorageOptions();
        var storageProvider = storageOptions.GetProvider();
        services.AddSingleton(storageOptions);
        if (storageProvider == AgentstrationStorageProvider.PostgreSql)
            services.AddSingleton<IAgentstrationStorageInitializer, PostgreSqlStorageInitializer>();
        else
            services.AddSingleton<IAgentstrationStorageInitializer, SqliteStorageInitializer>();
        if (storageProvider == AgentstrationStorageProvider.PostgreSql)
            services.AddPostgreSqlControlPlane(storageOptions.ConnectionString!);
        else
        {
            controlPlaneConnectionString ??= $"Data Source={Path.Combine(dataDirectory, "control-plane.db")}";
            services.AddSqliteControlPlane(controlPlaneConnectionString);
        }
        var secretPath = Path.Combine(dataDirectory, "secrets");
        services.AddSingleton(_ => new EnvironmentMasterKeyProvider(Path.Combine(secretPath, "master.key")));
        services.AddSingleton<IMasterKeyProvider>(provider => provider.GetRequiredService<EnvironmentMasterKeyProvider>());
        services.AddSingleton<ISecretVaultProvider>(provider => new LocalSecretVaultProvider(
            secretPath,
            provider.GetRequiredService<IMasterKeyProvider>()));
        services.AddSingleton<ISecretVaultProvider, SharedKeyFileSecretVaultProvider>();
        services.AddSingleton<SecretManagementService>();
        services.AddSingleton<ISecretResolver>(provider => provider.GetRequiredService<SecretManagementService>());
        services.AddSingleton<IPrincipalResolver, ExternalIdentityPrincipalResolver>();
        services.AddSingleton<IInitialPrincipalProvisioner, InitialPrincipalProvisioner>();
        services.AddSingleton<IInitialTopologyProvisioner, InitialTopologyProvisioner>();
        services.AddSingleton<ILocalPrincipalProvisioner, LocalPrincipalProvisioner>();
        services.AddSingleton<IPlatformAuthorizationService, PlatformAuthorizationService>();
        services.AddSingleton<PlatformAdministratorLifecycleLock>();
        services.AddSingleton<PlatformAdministratorAdministrationService>();
        services.AddSingleton<IPlatformAdministratorPolicy>(provider => provider.GetRequiredService<PlatformAdministratorAdministrationService>());
        services.AddSingleton<ExternalIdentityLifecycleLock>();
        services.AddSingleton<ExternalIdentityAdministrationService>();
        services.AddSingleton<IAuthorizationService, PermissionAuthorizationService>();
        services.AddSingleton<SecurityAuditService>();
        services.AddSingleton<ISecurityAuditWriter>(provider => provider.GetRequiredService<SecurityAuditService>());
        services.AddSingleton<ILocalEnvironmentBootstrapper, LocalEnvironmentBootstrapper>();
        services.AddSingleton<IdentityAdministrationService>();
        services.AddScoped<IBootstrapResourceHandler, TenantBootstrapResourceHandler>();
        services.AddScoped<IBootstrapResourceHandler, WorkspaceBootstrapResourceHandler>();
        services.AddScoped<IBootstrapResourceHandler, PrincipalDefaultContextBootstrapResourceHandler>();
        services.AddScoped<IBootstrapResourceHandler, PackInstallationBootstrapResourceHandler>();
        services.AddScoped<IBootstrapResourceHandler, ModelProviderBootstrapResourceHandler>();
        services.AddScoped<IBootstrapResourceHandler, RuntimeProfileBootstrapResourceHandler>();
        services.AddScoped<IBootstrapResourceHandler, ModelProfileBootstrapResourceHandler>();
        services.AddScoped<IBootstrapResourceHandler, AgentBootstrapResourceHandler>();
        services.AddScoped<IBootstrapResourceHandler, FlowBootstrapResourceHandler>();
        services.AddScoped<IBootstrapResourceHandler, EntryBootstrapResourceHandler>();
        services.AddSingleton<WorkspaceMembershipAdministrationService>();
        services.AddSingleton<IdentityExperienceService>();
        services.AddSingleton<PrincipalPreferencesService>();
        services.AddSingleton<PersonalAccessTokenService>();
        services.AddSingleton<IAgentDefinitionCompiler, AgentDefinitionCompiler>();
        services.AddSingleton<IRuntimeAgentResolver, ControlPlaneRuntimeAgentResolver>();
        services.AddSingleton<IModelProfileReferenceValidator, DeferredModelProfileReferenceValidator>();
        if (!useManagedProfileResolver)
            services.AddSingleton<IChatClientResolver, SingleChatClientResolver>();
        services.AddAgentstrationMcpTools();
        services.AddSingleton<AgentRuntimeContext>();
        services.AddSingleton<AgentFrameworkRuntimeFactory>();
        services.AddSingleton<Agentstration.Runtime.Abstractions.IAgentRuntimeFactory>(services =>
            services.GetRequiredService<AgentFrameworkRuntimeFactory>());
        services.AddSingleton<IRuntimeRegistry, RuntimeRegistry>();
        services.AddSingleton<IRuntimeRunQueue, LocalRuntimeRunQueue>();
        services.AddSingleton<IRuntimeRunCancellationRegistry, LocalRuntimeRunCancellationRegistry>();
        services.AddSingleton<IRuntimeRunExecutionScope, WorkspaceRuntimeRunExecutionScope>();
        services.AddSingleton<IAgentDeploymentProvisioner, InProcessAgentProvisioner>();
        services.AddSingleton<IAgentDeploymentProvisioner, SharedHostAgentProvisioner>();
        services.AddSingleton<IAgentDeploymentReconciler, LocalAgentDeploymentReconciler>();
        services.AddSingleton<IAgentRouter, AgentFrameworkAgentRouter>();
        services.AddSingleton(new AgentRevisionRetentionOptions());
        services.AddSingleton<AgentManagementService>();
        services.AddSingleton<AgentExecutionCoordinator>();
        services.AddSingleton<IPackArchiveReader, ZipPackArchiveReader>();
        services.AddSingleton<IPackArtifactStore>(_ => new FileSystemPackArtifactStore(Path.Combine(dataDirectory, "pack-artifacts")));
        services.AddSingleton<IPackResourceHandler, ModelProviderPackResourceHandler>();
        services.AddSingleton<IPackResourceHandler, RuntimeProfilePackResourceHandler>();
        services.AddSingleton<IPackResourceHandler, ModelProfilePackResourceHandler>();
        services.AddSingleton<IPackResourceHandler, AgentPackResourceHandler>();
        services.AddSingleton<IPackResourceHandler, FlowPackResourceHandler>();
        services.AddSingleton<IPackResourceHandler, EntryPackResourceHandler>();
        services.AddSingleton<IPackWorkspaceResourceCatalog, WorkspacePackResourceCatalog>();
        services.AddSingleton<PackManagementService>();
        services.AddSingleton<PackAuthoringService>();
        services.AddSingleton<PackCompositionService>();
        services.AddSingleton<Agentstration.Management.Contracts.SourceManifestValidator>();
        services.AddSingleton<ISourceManifestReader, Agentstration.Management.Contracts.SourceManifestReader>();
        services.AddSingleton<ISourceVerificationIndexReader, Agentstration.Management.Contracts.SourceVerificationIndexReader>();
        services.AddHttpClient<ISourceManifestRetriever, HttpSourceManifestRetriever>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(15);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Agentstration-Source-Importer/1.0");
        }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5
        });
        sourceVerificationIndexOptions ??= new SourceVerificationIndexOptions();
        if (sourceVerificationIndexOptions.TimeoutSeconds is < 1 or > 60)
            throw new InvalidOperationException("Source verification index timeout must be between 1 and 60 seconds.");
        if (sourceVerificationIndexOptions.MaximumBytes is < 1024 or > Agentstration.Management.Contracts.SourceVerificationIndexReader.MaximumIndexBytes)
            throw new InvalidOperationException($"Source verification index maximum bytes must be between 1024 and {Agentstration.Management.Contracts.SourceVerificationIndexReader.MaximumIndexBytes}.");
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
        services.AddSingleton<ISourceCatalogManifestReader, Agentstration.Management.Contracts.SourceCatalogManifestReader>();
        services.AddSingleton(new SourceMaterializationLimits());
        services.AddSingleton<SourceChannelSnapshotService>();
        services.AddSingleton<SourceCatalogService>();
        services.AddSingleton<SourcePackInstallationService>();
        services.AddSingleton<ISourceRegistryIndexReader, SourceRegistryIndexReader>();
        services.AddSingleton<ISourceRegistryReader, SourceRegistryReader>();
        services.AddSingleton<ISourceRegistryReferenceResolver, SourceRegistryRuntimeReferenceResolver>();
        sourceRegistryTransportOptions ??= new();
        sourceRegistryTransportOptions.Validate();
        services.AddSingleton(sourceRegistryTransportOptions);
        services.AddHttpClient<ISourceRegistryDocumentRetriever, HttpSourceRegistryDocumentRetriever>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(sourceRegistryTransportOptions.TimeoutSeconds);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Agentstration-Source-Registry/1.0");
        }).ConfigurePrimaryHttpMessageHandler(() => HttpSourceRegistryDocumentRetriever.CreatePrimaryHandler(sourceRegistryTransportOptions));
        services.AddSingleton<ISourceRegistryCacheStore>(_ => new FileSystemSourceRegistryCacheStore(Path.Combine(dataDirectory, "source-registry-cache")));
        services.AddSingleton<SourceRegistryManagementService>();
        services.AddSingleton<ToolManagementService>();
        services.AddSingleton<ToolExecutionHookManagementService>();
        services.AddSingleton<RuntimeProfileManagementService>();
        services.AddSingleton<ITriggerScheduleCalculator, QuartzTriggerScheduleCalculator>();
        services.AddSingleton<ITriggerTargetValidator, FlowTriggerTargetValidator>();
        services.AddSingleton<ITriggerExecutionAuthorizer, WorkspaceTriggerExecutionAuthorizer>();
        services.AddSingleton<ITriggerWorkSubmitter, TriggerWorkSubmitter>();
        services.AddSingleton<ITriggerSchedulerProjection, QuartzTriggerScheduler>();
        services.AddSingleton<TriggerManagementService>();
        services.AddSingleton<TriggerFiringService>();
        // Quartz owns a short-lived, local scheduler database. Disabling ADO.NET pooling
        // ensures its file handles are released when the hosted scheduler shuts down.
        var schedulerConnectionString = storageProvider == AgentstrationStorageProvider.PostgreSql
            ? storageOptions.ConnectionString!
            : $"Data Source={Path.Combine(dataDirectory, "scheduler.db")};Pooling=False";
        services.AddQuartz(configuration =>
        {
            configuration.SchedulerId = "AUTO";
            configuration.SchedulerName = "Agentstration.TriggerScheduler";
            configuration.UsePersistentStore(options =>
            {
                options.UseProperties = true;
                if (storageProvider == AgentstrationStorageProvider.PostgreSql)
                {
                    options.UsePostgres(postgres =>
                    {
                        postgres.ConnectionString = schedulerConnectionString;
                        postgres.TablePrefix = "scheduler.qrtz_";
                    });
                }
                else
                    options.UseMicrosoftSQLite(sqlite => sqlite.ConnectionString = schedulerConnectionString);
                options.UseSystemTextJsonSerializer();
            });
        });
        if (enableHostedServices)
        {
            services.AddSingleton<IHostedService, QuartzLoggingInitializer>();
            if (storageProvider == AgentstrationStorageProvider.Sqlite)
                services.AddSingleton<IHostedService>(_ => new QuartzSqliteSchemaInitializer(schedulerConnectionString));
            services.AddQuartzHostedService(options => options.WaitForJobsToComplete = true);
            services.AddHostedService<TriggerSchedulerReconciler>();
        }
        if (storageProvider == AgentstrationStorageProvider.PostgreSql)
            services.AddPostgreSqlRuntimeRuns(storageOptions.ConnectionString!);
        else
        {
            runtimeConnectionString ??= $"Data Source={Path.Combine(dataDirectory, "runtime-plane.db")}";
            services.AddSqliteRuntimeRuns(runtimeConnectionString);
        }
        services.AddSingleton<RuntimeRunStateManager>();
        services.AddSingleton<RuntimeRunService>();
        services.TryAddSingleton(new ToolExecutionCaptureOptions());
        services.AddSingleton<IToolExecutionEventSink, RuntimeToolExecutionEventSink>();
        services.AddSingleton<IToolExecutionEventSink, FlowToolExecutionEventSink>();
        services.AddSingleton<IToolExecutionHookResolver, ManagementToolExecutionHookResolver>();
        services.AddSingleton<IToolGovernanceAuditReader, ToolGovernanceAuditReader>();
        services.AddSingleton<IToolExecutionPipeline, ToolExecutionPipeline>();
        services.AddSingleton<IRuntimeRunExecutionScope, WorkspaceRuntimeRunExecutionScope>();
        if (storageProvider == AgentstrationStorageProvider.PostgreSql)
            services.AddPostgreSqlWorkPlane(storageOptions.ConnectionString!);
        else
        {
            workPlaneConnectionString ??= $"Data Source={Path.Combine(dataDirectory, "work-plane.db")}";
            services.AddSqliteWorkPlane(workPlaneConnectionString);
        }
        services.AddSingleton<IArtifactStore>(_ => new FileSystemArtifactStore(Path.Combine(dataDirectory, "artifacts")));
        services.AddSingleton<LocalWorkExecutionGateway>();
        services.AddSingleton<IWorkExecutionGateway>(provider => provider.GetRequiredService<LocalWorkExecutionGateway>());
        services.AddSingleton<ILocalWorkExecutionQueue>(provider => provider.GetRequiredService<LocalWorkExecutionGateway>());
        services.AddSingleton<WorkItemService>();
        services.AddSingleton<WorkplaceService>();
        services.AddSingleton<WorkTaskDeletionService>();
        services.AddSingleton<IWorkTaskEventSink, WorkplaceProjectionSink>();
        if (storageProvider == AgentstrationStorageProvider.PostgreSql)
            services.AddPostgreSqlFlowStorage(storageOptions.ConnectionString!);
        else
        {
            flowConnectionString ??= $"Data Source={Path.Combine(dataDirectory, "flow-plane.db")}";
            services.AddSqliteFlowStorage(flowConnectionString);
        }
        services.AddSingleton<FlowService>();
        services.AddSingleton<IEntryTargetResolver, EntryTargetResolver>();
        services.AddSingleton<EntryResourceDeletionGuard>();
        services.AddSingleton<IManagementResourceDeletionGuard>(provider => provider.GetRequiredService<EntryResourceDeletionGuard>());
        services.AddSingleton<IFlowDeletionGuard>(provider => provider.GetRequiredService<EntryResourceDeletionGuard>());
        services.AddSingleton<EntryAdministrationService>();
        services.AddSingleton<IWorkplaceContext, CurrentWorkplaceContext>();
        services.AddSingleton<DashboardAdministrationService>();
        services.AddSingleton<IFlowRunQueue, LocalFlowRunQueue>();
        services.AddSingleton<IFlowRunCancellationRegistry, LocalFlowRunCancellationRegistry>();
        services.AddSingleton<IFlowRunExecutionScope, WorkspaceFlowRunExecutionScope>();
        services.AddSingleton<IWorkExecutionScopeAccessor, CurrentWorkExecutionScopeAccessor>();
        services.TryAddSingleton<IFlowRunEventSink, NullFlowRunEventSink>();
        services.AddSingleton<IFlowInputRequestSink, WorkplaceFlowInputProjectionSink>();
        services.AddSingleton<IWorkplaceExternalInputResponder, WorkplaceFlowInputResponder>();
        services.AddSingleton<FlowRevisionRetentionService>();
        services.AddSingleton<IAgentRevisionRunRetention, AgentRevisionRunRetention>();
        services.AddSingleton<IFlowAgentExecutor, ManagedFlowAgentExecutor>();
        services.AddSingleton<AgentFrameworkFlowOrchestrationEngine>();
        services.AddSingleton<IFlowOrchestrationEngine, ManagedFlowOrchestrationEngine>();
        services.AddSingleton<IFlowResourceReferenceResolver, ManagementFlowResourceReferenceResolver>();
        services.AddSingleton<FlowExpressionParser>();
        services.AddSingleton<IExpressionParser>(provider => provider.GetRequiredService<FlowExpressionParser>());
        services.AddSingleton<IExpressionValidator>(provider => provider.GetRequiredService<FlowExpressionParser>());
        services.AddSingleton<IExpressionEvaluator>(provider => provider.GetRequiredService<FlowExpressionParser>());
        services.AddSingleton<IFlowDefinitionValidator, FlowGraphValidator>();
        services.AddSingleton<FlowDraftService>();
        services.AddSingleton<FlowRunService>();
        return services;
    }
}
