using Agentstration.Agents;
using Agentstration.Infrastructure.Agents;
using Agentstration.Infrastructure.Runtime;
using Agentstration.ModelProviders;
using Agentstration.Runtime.Abstractions;
using Agentstration.Runtime.Local;
using Agentstration.Runtime.MicrosoftAgentFramework;
using Agentstration.Runtime.Profiles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Agentstration.Infrastructure;

internal static class AgentsServiceCollectionExtensions
{
    internal static IServiceCollection AddAgentstrationAgents(this IServiceCollection services, AgentstrationCompositionContext context)
    {
        var useManagedProfileResolver = context.UseManagedProfileResolver;
        services.AddSingleton<IAgentDefinitionCompiler, AgentDefinitionCompiler>();
        services.AddSingleton<IRuntimeAgentResolver, ResourceRuntimeAgentResolver>();
        services.AddSingleton<IRuntimeAgentAccessValidator, RuntimeAgentAccessValidator>();
        services.TryAddSingleton<IModelProfileReferenceValidator, DeferredModelProfileReferenceValidator>();
        if (!useManagedProfileResolver)
            services.TryAddSingleton<IChatClientResolver, SingleChatClientResolver>();
        services.AddSingleton<IAgentDeploymentReconciler, ExternalWorkerAgentDeploymentReconciler>();
        services.AddSingleton<IAgentRouter, AgentFrameworkAgentRouter>();
        services.AddSingleton(new AgentRevisionRetentionOptions());
        services.AddSingleton<AgentManagementService>();
        services.AddSingleton<AgentExecutionCoordinator>();
        services.AddSingleton<IAgentExecutionCoordinator>(provider => provider.GetRequiredService<AgentExecutionCoordinator>());
        services.AddSingleton<IAgentRuntimePreparationService, AgentRuntimePreparationService>();
        services.AddSingleton<RuntimeProfileManagementService>();
        return services;
    }
}
