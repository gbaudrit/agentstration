using Agentstration.SystemOperations;
using Agentstration.SystemOperations.Contracts;
using Agentstration.Infrastructure.SystemOperations;
using Agentstration.Reconciliation;
using Agentstration.Reconciliation.Contracts;
using Agentstration.Triggers;
using Agentstration.Functions;
using Agentstration.Knowledge;
using Agentstration.Storage;
using Agentstration.ConnectedServices;
using Agentstration.Schemas;
using Agentstration.DataSources;
using Agentstration.Agents;
using Agentstration.Flows.Application;
using Agentstration.Infrastructure.SystemGraph;
using Agentstration.Models;
using Agentstration.SystemGraph;
using Agentstration.SystemGraph.Contracts;
using Agentstration.Tools;
using Agentstration.Systems;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Infrastructure;

internal static class SystemGraphServiceCollectionExtensions
{
    internal static IServiceCollection AddAgentstrationSystemGraph(this IServiceCollection services)
    {
        services.AddSingleton<ISystemGraphContributor, AgentSystemGraphContributor>();
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
        services.AddSingleton<ISystemOperationsEvidenceReader, SystemOperationsEvidenceReader>();
        services.AddSingleton<ISystemOperationsService, SystemOperationsService>();
        services.AddSingleton<IReconciliationPreviewService, ReconciliationPreviewService>();
        services.AddSingleton<ISystemGraphQueryService>(provider => provider.GetRequiredService<SystemGraphQueryService>());
        services.AddSingleton<ISystemGraphImpactService>(provider => provider.GetRequiredService<SystemGraphQueryService>());
        return services;
    }
}
