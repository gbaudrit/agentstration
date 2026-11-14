using Agentstration.Functions;
using Agentstration.Functions.Abstractions;
using Agentstration.Functions.Builtins;
using Agentstration.Functions.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Infrastructure;

internal static class FunctionsServiceCollectionExtensions
{
    internal static IServiceCollection AddAgentstrationFunctions(this IServiceCollection services)
    {
        services.AddSingleton<INativeTransformationFunction, JsonSelectFunction>();
        services.AddSingleton<INativeTransformationFunction, JsonProjectFunction>();
        services.AddSingleton<INativeTransformationFunction, CollectionFilterFunction>();
        services.AddSingleton<INativeTransformationFunction, CollectionDistinctFunction>();
        services.AddSingleton<INativeTransformationFunction, Agentstration.Infrastructure.Functions.ArtifactExplodeFunction>();
        services.AddSingleton<NativeFunctionRegistry>();
        services.AddSingleton<NativeFunctionExecutionContext>();
        services.AddSingleton<INativeFunctionExecutionContext>(provider => provider.GetRequiredService<NativeFunctionExecutionContext>());
        services.AddSingleton<TransformationFunctionService>();
        services.AddSingleton<ITransformationFunctionResolver>(provider => provider.GetRequiredService<TransformationFunctionService>());
        return services;
    }
}
