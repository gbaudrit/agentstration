using Agentstration.Infrastructure.Schemas;
using Agentstration.Schemas;
using Agentstration.Schemas.Aep;
using Agentstration.Schemas.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Infrastructure;

internal static class SchemasServiceCollectionExtensions
{
    internal static IServiceCollection AddAgentstrationSchemas(this IServiceCollection services, AgentstrationCompositionContext context)
    {
        services.AddSingleton(context.Options.XmlContent);
        services.AddSingleton<ISchemaFormatHandler, BuiltinXmlSchemaFormatHandler>();
        services.AddSingleton<ISchemaFormatHandler, Agentstration.Functions.TransformationSchemaFormatHandler>();
        services.AddSingleton<ISchemaFormatHandler, BuiltinJsonSchemaFormatHandler>();
        services.AddSingleton(new AepSchemaFormatOptions());
        services.AddSingleton<AepSchemaFormatHandlerSource>();
        services.AddSingleton<ISchemaFormatHandlerSource>(provider => new StartupSchemaFormatHandlerSource(
            provider.GetRequiredService<AepSchemaFormatHandlerSource>(), provider.GetRequiredService<BootstrapStartupState>()));
        services.AddSingleton<ISchemaFormatRegistry, SchemaFormatRegistry>();
        services.AddSingleton<SchemaManagementService>();
        services.AddSingleton<ISchemaBindingResolver, ManagementSchemaBindingResolver>();
        return services;
    }
}
