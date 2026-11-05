using Agentstration.Agents;
using Agentstration.Identity;
using Agentstration.Identity.Contracts;
using Agentstration.Infrastructure.Events;
using Agentstration.ModelProviders;
using Agentstration.ResourceManagement;
using Agentstration.Triggers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Agentstration.Infrastructure;

internal static class FoundationServiceCollectionExtensions
{
    internal static IServiceCollection AddAgentstrationFoundation(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<LocalBootstrapOptions>();
        services.TryAddSingleton<CurrentRequestContext>();
        services.TryAddSingleton<ICurrentRequestContext>(provider => provider.GetRequiredService<CurrentRequestContext>());
        services.TryAddSingleton<IRequestContextScopeFactory>(provider => provider.GetRequiredService<CurrentRequestContext>());
        services.TryAddSingleton<ITriggerExecutionContext>(provider => provider.GetRequiredService<CurrentRequestContext>());
        services.TryAddSingleton<IResourceReferenceResolver, ResourceReferenceResolver>();
        services.TryAddSingleton<ResourceScopeOperationService>();
        services.TryAddSingleton<ReadableResourceScopeService>();
        services.TryAddSingleton<IResourceScopeOperations>(provider => provider.GetRequiredService<ResourceScopeOperationService>());
        services.TryAddSingleton(new GenAiObservabilityOptions());
        services.TryAddTransient<GenAiHttpPayloadCaptureHandler>();
        services.AddSingleton<IManagementEventPublisher, InProcessManagementEventPublisher>();
        return services;
    }
}
