using Agentstration.Infrastructure.Agents;
using Agentstration.Infrastructure.Runtime;
using Agentstration.Management.Abstractions;
using Agentstration.Management.Core;
using Agentstration.ModelProviders;
using Agentstration.Runtime.Abstractions;
using Agentstration.Runtime.AgentFramework;
using Agentstration.Runtime.Local;
using Agentstration.Tools.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Agentstration.Infrastructure;

internal static class AgentRuntimeServiceCollectionExtensions
{
    internal static IServiceCollection AddAgentstrationAgentRuntime(
        this IServiceCollection services,
        AgentstrationServiceRegistrationContext context)
    {
        services.AddSingleton<IAgentDefinitionCompiler, AgentDefinitionCompiler>();
        services.AddSingleton<IRuntimeAgentResolver, ControlPlaneRuntimeAgentResolver>();
        services.TryAddSingleton<IModelProfileReferenceValidator, DeferredModelProfileReferenceValidator>();
        if (!context.UseManagedProfileResolver)
            services.AddSingleton<IChatClientResolver, SingleChatClientResolver>();
        services.AddAgentstrationMcpTools();
        services.AddSingleton<AgentRuntimeContext>();
        services.AddSingleton<AgentFrameworkRuntimeFactory>();
        services.AddSingleton<IAgentRuntimeFactory>(provider =>
            provider.GetRequiredService<AgentFrameworkRuntimeFactory>());
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
        return services;
    }
}
