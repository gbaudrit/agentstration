using Agentstration.Application;
using Agentstration.Application.Ingestion;
using Agentstration.Application.Memory;
using Agentstration.Application.Missions;
using Agentstration.Application.Routing;
using Agentstration.Application.Work;
using Agentstration.Application.Workflows;
using Agentstration.Application.Workspaces;
using Agentstration.Flow.Application;
using Agentstration.Flow.Storage.Sqlite;
using Agentstration.Infrastructure.Agents;
using Agentstration.Infrastructure.Artifacts;
using Agentstration.Infrastructure.Events;
using Agentstration.Infrastructure.Flows;
using Agentstration.Infrastructure.Ingestion;
using Agentstration.Infrastructure.Missions;
using Agentstration.Infrastructure.Packs;
using Agentstration.Infrastructure.Persistence;
using Agentstration.Infrastructure.Runtime;
using Agentstration.Infrastructure.Work;
using Agentstration.Infrastructure.Workflows;
using Agentstration.Management.Abstractions;
using Agentstration.Management.Core;
using Agentstration.Management.Storage.Sqlite;
using Agentstration.ModelProviders;
using Agentstration.Runtime.Abstractions;
using Agentstration.Runtime.AgentFramework;
using Agentstration.Runtime.Core;
using Agentstration.Runtime.Local;
using Agentstration.Runtime.Storage.Sqlite;
using Agentstration.Secrets.Abstractions;
using Agentstration.Secrets.Local;
using Agentstration.Tools.Mcp;
using Agentstration.Work;
using Agentstration.Work.Storage.Abstractions;
using Agentstration.Work.Storage.Sqlite;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Agentstration.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddAgentstration(
        this IServiceCollection services,
        string dataPath,
        bool inMemory = false,
        AiProviderOptions? aiOptions = null,
        string? controlPlaneConnectionString = null,
        string? workPlaneConnectionString = null,
        string? flowConnectionString = null,
        string? runtimeConnectionString = null)
    {
        services.AddSingleton(TimeProvider.System);
        services.TryAddSingleton<LocalBootstrapOptions>();
        services.TryAddSingleton<CurrentRequestContext>();
        services.TryAddSingleton<ICurrentRequestContext>(provider => provider.GetRequiredService<CurrentRequestContext>());
        services.TryAddSingleton<IRequestContextInitializer>(provider => provider.GetRequiredService<CurrentRequestContext>());
        services.TryAddSingleton<IRequestContextScopeFactory>(provider => provider.GetRequiredService<CurrentRequestContext>());
        services.TryAddSingleton(new GenAiObservabilityOptions());
        services.TryAddTransient<GenAiHttpPayloadCaptureHandler>();
        if (inMemory) services.AddSingleton<IPlatformStore, InMemoryPlatformStore>();
        else services.AddSingleton<IPlatformStore>(_ => new JsonFilePlatformStore(dataPath));
        services.AddSingleton<IEventBus, InProcessEventBus>();
        services.AddSingleton<IManagementEventPublisher, InProcessManagementEventPublisher>();
        services.AddSingleton<IItemProcessingQueue, ItemProcessingQueue>();
        services.AddSingleton<IIntentRouter, DeterministicIntentRouter>();
        services.AddSingleton<MemoryService>();
        services.AddSingleton<IMemoryStore>(provider => provider.GetRequiredService<MemoryService>());
        services.AddSingleton<IMemorySearch>(provider => provider.GetRequiredService<MemoryService>());
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
        services.AddSingleton<MicrosoftExtensionsAiAgentRuntime>();
        services.AddSingleton<Agentstration.Application.IAgentRuntime>(provider => provider.GetRequiredService<MicrosoftExtensionsAiAgentRuntime>());
        services.AddSingleton<IObservationTool, DemoObservationTool>();
        services.AddHttpClient("ingestion", client => client.Timeout = TimeSpan.FromSeconds(15));
        services.AddSingleton<IContentSourceReader, SafeHttpContentSourceReader>();
        services.AddSingleton<WorkspaceService>();
        services.AddSingleton<IngestionService>();
        services.AddSingleton<ContentProcessingWorkflow>();
        services.AddSingleton<MissionService>();
        services.AddSingleton<IEventHandler<Domain.ItemReceived>, ItemReceivedHandler>();
        controlPlaneConnectionString ??= $"Data Source={Path.Combine(Path.GetDirectoryName(dataPath) ?? ".", "control-plane.db")}";
        services.AddSqliteControlPlane(controlPlaneConnectionString);
        var secretPath = Path.Combine(Path.GetDirectoryName(dataPath) ?? ".", "secrets");
        services.AddSingleton(_ => new EnvironmentMasterKeyProvider(Path.Combine(secretPath, "master.key")));
        services.AddSingleton<IMasterKeyProvider>(provider => provider.GetRequiredService<EnvironmentMasterKeyProvider>());
        services.AddSingleton<ISecretVaultProvider>(provider => new LocalSecretVaultProvider(
            secretPath,
            provider.GetRequiredService<IMasterKeyProvider>()));
        services.AddSingleton<SecretManagementService>();
        services.AddSingleton<ISecretResolver>(provider => provider.GetRequiredService<SecretManagementService>());
        services.AddSingleton<IPrincipalResolver, ExternalIdentityPrincipalResolver>();
        services.AddSingleton<IInitialPrincipalProvisioner, InitialPrincipalProvisioner>();
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
        services.AddSingleton<WorkspaceMembershipAdministrationService>();
        services.AddSingleton<IdentityExperienceService>();
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
        services.AddSingleton<IPackArtifactStore>(_ => new FileSystemPackArtifactStore(Path.Combine(Path.GetDirectoryName(dataPath) ?? ".", "pack-artifacts")));
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
        services.AddSingleton<ToolManagementService>();
        services.AddSingleton<ToolExecutionHookManagementService>();
        services.AddSingleton<RuntimeProfileManagementService>();
        runtimeConnectionString ??= $"Data Source={Path.Combine(Path.GetDirectoryName(dataPath) ?? ".", "runtime-plane.db")}";
        services.AddSqliteRuntimeRuns(runtimeConnectionString);
        services.AddSingleton<RuntimeRunStateManager>();
        services.AddSingleton<RuntimeRunService>();
        services.TryAddSingleton(new ToolExecutionCaptureOptions());
        services.AddSingleton<IToolExecutionEventSink, RuntimeToolExecutionEventSink>();
        services.AddSingleton<IToolExecutionEventSink, FlowToolExecutionEventSink>();
        services.AddSingleton<IToolExecutionHookResolver, ManagementToolExecutionHookResolver>();
        services.AddSingleton<IToolGovernanceAuditReader, ToolGovernanceAuditReader>();
        services.AddSingleton<IToolExecutionPipeline, ToolExecutionPipeline>();
        services.AddSingleton<IRuntimeRunExecutionScope, WorkspaceRuntimeRunExecutionScope>();
        workPlaneConnectionString ??= $"Data Source={Path.Combine(Path.GetDirectoryName(dataPath) ?? ".", "work-plane.db")}";
        services.AddSqliteWorkPlane(workPlaneConnectionString);
        services.AddSingleton<IArtifactStore>(_ => new FileSystemArtifactStore(Path.Combine(Path.GetDirectoryName(dataPath) ?? ".", "artifacts")));
        services.AddSingleton<LocalWorkExecutionGateway>();
        services.AddSingleton<IWorkExecutionGateway>(provider => provider.GetRequiredService<LocalWorkExecutionGateway>());
        services.AddSingleton<ILocalWorkExecutionQueue>(provider => provider.GetRequiredService<LocalWorkExecutionGateway>());
        services.AddSingleton<WorkItemService>();
        services.AddSingleton<WorkplaceService>();
        services.AddSingleton<IWorkTaskEventSink, WorkplaceProjectionSink>();
        flowConnectionString ??= $"Data Source={Path.Combine(Path.GetDirectoryName(dataPath) ?? ".", "flow-plane.db")}";
        services.AddSqliteFlowStorage(flowConnectionString);
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
