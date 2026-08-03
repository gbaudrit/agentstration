using Agentstration.Application;
using Agentstration.Application.Ingestion;
using Agentstration.Application.Memory;
using Agentstration.Application.Missions;
using Agentstration.Management.Abstractions;
using Agentstration.Management.Core;
using Agentstration.Application.Routing;
using Agentstration.Application.Workflows;
using Agentstration.Application.Workspaces;
using Agentstration.Application.Work;
using Agentstration.Infrastructure.Agents;
using Agentstration.Infrastructure.Events;
using Agentstration.Infrastructure.Ingestion;
using Agentstration.Infrastructure.Missions;
using Agentstration.Infrastructure.Persistence;
using Agentstration.Infrastructure.Workflows;
using Agentstration.Flow.Application;
using Agentstration.Flow.Storage.Sqlite;
using Agentstration.Management.Storage.Sqlite;
using Agentstration.ModelProviders;
using Agentstration.Runtime.Abstractions;
using Agentstration.Runtime.AgentFramework;
using Agentstration.Runtime.Core;
using Agentstration.Runtime.Local;
using Agentstration.Runtime.Storage.Sqlite;
using Agentstration.Work;
using Agentstration.Work.Storage.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.AI;

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
        if (string.Equals(aiOptions.Provider, "Deterministic", StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<IChatClient, DeterministicChatClient>();
        }
        else if (!string.Equals(aiOptions.Provider, "Ollama", StringComparison.OrdinalIgnoreCase))
        {
            services.AddHttpClient<OpenAiCompatibleChatClient>(client => client.Timeout = TimeSpan.FromSeconds(90));
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
        services.AddSingleton<IAgentDefinitionCompiler, AgentDefinitionCompiler>();
        services.AddSingleton<IModelProfileReferenceValidator, DeferredModelProfileReferenceValidator>();
        if (!string.Equals(aiOptions.Provider, "Ollama", StringComparison.OrdinalIgnoreCase))
            services.AddSingleton<IChatClientResolver, SingleChatClientResolver>();
        services.AddSingleton<IToolCatalog, EmptyToolCatalog>();
        services.AddSingleton<AgentRuntimeContext>();
        services.AddSingleton<Agentstration.Runtime.Abstractions.IAgentRuntimeFactory, AgentFrameworkRuntimeFactory>();
        services.AddSingleton<IRuntimeRegistry, RuntimeRegistry>();
        services.AddSingleton<IRuntimeRunQueue, LocalRuntimeRunQueue>();
        services.AddSingleton<IRuntimeRunCancellationRegistry, LocalRuntimeRunCancellationRegistry>();
        services.AddSingleton<IAgentDeploymentProvisioner, InProcessAgentProvisioner>();
        services.AddSingleton<IAgentDeploymentProvisioner, SharedHostAgentProvisioner>();
        services.AddSingleton<IAgentDeploymentReconciler, LocalAgentDeploymentReconciler>();
        services.AddSingleton<IAgentRouter, AgentFrameworkAgentRouter>();
        services.AddSingleton<AgentManagementService>();
        services.AddSingleton<RuntimeProfileManagementService>();
        runtimeConnectionString ??= $"Data Source={Path.Combine(Path.GetDirectoryName(dataPath) ?? ".", "runtime-plane.db")}";
        services.AddSqliteRuntimeRuns(runtimeConnectionString);
        services.AddSingleton<RuntimeRunService>();
        workPlaneConnectionString ??= $"Data Source={Path.Combine(Path.GetDirectoryName(dataPath) ?? ".", "work-plane.db")}";
        services.AddSqliteWorkPlane(workPlaneConnectionString);
        services.AddSingleton<LocalWorkExecutionGateway>();
        services.AddSingleton<IWorkExecutionGateway>(provider => provider.GetRequiredService<LocalWorkExecutionGateway>());
        services.AddSingleton<ILocalWorkExecutionQueue>(provider => provider.GetRequiredService<LocalWorkExecutionGateway>());
        services.AddSingleton<WorkItemService>();
        flowConnectionString ??= $"Data Source={Path.Combine(Path.GetDirectoryName(dataPath) ?? ".", "flow-plane.db")}";
        services.AddSqliteFlowStorage(flowConnectionString);
        services.AddSingleton<FlowService>();
        return services;
    }
}
