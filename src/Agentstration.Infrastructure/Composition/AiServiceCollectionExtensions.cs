using Agentstration.Infrastructure.Agents;
using Agentstration.ModelProviders;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Infrastructure;

internal static class AiServiceCollectionExtensions
{
    internal static IServiceCollection AddAgentstrationAi(this IServiceCollection services, AgentstrationCompositionContext context)
    {
        var aiOptions = context.Options.Ai;
        var useManagedProfileResolver = context.UseManagedProfileResolver;
        services.AddSingleton(aiOptions);
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
        return services;
    }
}
