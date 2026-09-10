using Agentstration.Infrastructure.Agents;
using Agentstration.Infrastructure.Events;
using Agentstration.Management.Abstractions;
using Agentstration.Management.Core;
using Agentstration.ModelProviders;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Agentstration.Infrastructure;

internal static class FoundationServiceCollectionExtensions
{
    internal static IServiceCollection AddAgentstrationFoundation(
        this IServiceCollection services,
        AgentstrationServiceRegistrationContext context)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<LocalBootstrapOptions>();
        services.TryAddSingleton<CurrentRequestContext>();
        services.TryAddSingleton<ICurrentRequestContext>(provider => provider.GetRequiredService<CurrentRequestContext>());
        services.TryAddSingleton<IRequestContextScopeFactory>(provider => provider.GetRequiredService<CurrentRequestContext>());
        services.TryAddSingleton(new GenAiObservabilityOptions());
        services.TryAddTransient<GenAiHttpPayloadCaptureHandler>();
        services.AddSingleton<IManagementEventPublisher, InProcessManagementEventPublisher>();
        services.AddSingleton(context.AiOptions);

        if (string.Equals(context.AiOptions.Provider, "Deterministic", StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<IChatClient, DeterministicChatClient>();
        }
        else if (!context.UseManagedProfileResolver)
        {
            services.AddHttpClient<OpenAiCompatibleChatClient>(client => client.Timeout = TimeSpan.FromSeconds(90))
                .AddHttpMessageHandler<GenAiHttpPayloadCaptureHandler>();
            services.AddSingleton<IChatClient>(provider => provider.GetRequiredService<OpenAiCompatibleChatClient>());
        }

        return services;
    }
}
