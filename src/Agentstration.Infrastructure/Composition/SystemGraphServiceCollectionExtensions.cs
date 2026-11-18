using Agentstration.Agents;
using Agentstration.ConnectedServices;
using Agentstration.DataSources;
using Agentstration.Flows.Application;
using Agentstration.Functions;
using Agentstration.Infrastructure.SystemGraph;
using Agentstration.Infrastructure.SystemOperations;
using Agentstration.Knowledge;
using Agentstration.Models;
using Agentstration.Reconciliation;
using Agentstration.Reconciliation.Contracts;
using Agentstration.Schemas;
using Agentstration.Storage;
using Agentstration.SystemGraph;
using Agentstration.SystemGraph.Contracts;
using Agentstration.SystemOperations;
using Agentstration.SystemOperations.Contracts;
using Agentstration.Systems;
using Agentstration.Tools;
using Agentstration.Triggers;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Infrastructure;

internal static class SystemGraphServiceCollectionExtensions
{
    internal static IServiceCollection AddAgentstrationSystemGraph(this IServiceCollection services)
    {
        services.AddSingleton<ISystemGraphContributor, AgentSystemGraphContributor>();
        services.AddSingleton<AgentDeploymentAssignmentService>();
        services.AddSingleton<ISystemGraphContributor, ModelSystemGraphContributor>();
        services.AddSingleton<ISystemGraphContributor, FlowSystemGraphContributor>();
        services.AddSingleton<ISystemGraphContributor, ToolSystemGraphContributor>();
        services.AddSingleton<ISystemGraphContributor, DataSourceSystemGraphContributor>();
        services.AddSingleton<ISystemGraphContributor, SchemaSystemGraphContributor>();
        services.AddSingleton<ISystemGraphContributor, ConnectedServiceSystemGraphContributor>();
        services.AddSingleton<ISystemGraphContributor, StorageSystemGraphContributor>();
        services.AddSingleton<ISystemGraphContributor, KnowledgeSystemGraphContributor>();
        services.AddSingleton<ISystemGraphContributor, FunctionSystemGraphContributor>();
        services.AddSingleton<ISystemGraphContributor, TriggerSystemGraphContributor>();
        services.AddSingleton<ISystemGraphReadScope, SystemGraphReadScope>();
        services.AddSingleton<SystemModelService>();
        services.AddSingleton<ISystemGraphContributor, SystemModelGraphContributor>();
        services.AddSingleton<ISystemGraphAuthorization, SystemGraphAuthorization>();
        services.AddSingleton<SystemGraphQueryService>();
        services.AddSingleton<SystemOperationsTelemetry>();
        services.AddSingleton<ISystemOperationsAccess, SystemOperationsAccess>();
        services.AddSingleton<SystemFlowBindingReader>();
        services.AddSingleton<ISystemOperationsEvidenceReader, SystemOperationsEvidenceReader>();
        services.AddSingleton<ISystemOperationsService, SystemOperationsService>();
        services.AddSingleton<IReconciliationPreviewService, ReconciliationPreviewService>();
        services.AddSingleton<ISystemGraphQueryService>(provider => provider.GetRequiredService<SystemGraphQueryService>());
        services.AddSingleton<ISystemGraphStreamService>(provider => provider.GetRequiredService<SystemGraphQueryService>());
        services.AddSingleton<ISystemGraphImpactService>(provider => provider.GetRequiredService<SystemGraphQueryService>());
        return services;
    }
}
