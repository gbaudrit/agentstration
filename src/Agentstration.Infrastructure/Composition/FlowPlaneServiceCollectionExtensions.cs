using Agentstration.Application.Work;
using Agentstration.Flow.Application;
using Agentstration.Flow.Storage.PostgreSql;
using Agentstration.Flow.Storage.Sqlite;
using Agentstration.Infrastructure.Flows;
using Agentstration.Infrastructure.Work;
using Agentstration.Management.Abstractions;
using Agentstration.Runtime.Abstractions;
using Agentstration.Runtime.AgentFramework;
using Agentstration.Work;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Agentstration.Infrastructure;

internal static class FlowPlaneServiceCollectionExtensions
{
    internal static IServiceCollection AddAgentstrationFlowPlane(
        this IServiceCollection services,
        AgentstrationServiceRegistrationContext context)
    {
        if (context.StorageProvider == AgentstrationStorageProvider.PostgreSql)
        {
            services.AddPostgreSqlFlowStorage(context.StorageOptions.ConnectionString!);
        }
        else
        {
            services.AddSqliteFlowStorage(
                context.FlowConnectionString
                ?? $"Data Source={Path.Combine(context.DataDirectory, "flow-plane.db")}");
        }

        services.AddSingleton<FlowService>();
        services.AddSingleton<IEntryTargetResolver, EntryTargetResolver>();
        services.AddSingleton<EntryResourceDeletionGuard>();
        services.AddSingleton<IManagementResourceDeletionGuard>(provider =>
            provider.GetRequiredService<EntryResourceDeletionGuard>());
        services.AddSingleton<IFlowDeletionGuard>(provider =>
            provider.GetRequiredService<EntryResourceDeletionGuard>());
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
        services.AddSingleton<IExpressionParser>(provider =>
            provider.GetRequiredService<FlowExpressionParser>());
        services.AddSingleton<IExpressionValidator>(provider =>
            provider.GetRequiredService<FlowExpressionParser>());
        services.AddSingleton<IExpressionEvaluator>(provider =>
            provider.GetRequiredService<FlowExpressionParser>());
        services.AddSingleton<IFlowDefinitionValidator, FlowGraphValidator>();
        services.AddSingleton<FlowDraftService>();
        services.AddSingleton<FlowRunService>();
        return services;
    }
}
