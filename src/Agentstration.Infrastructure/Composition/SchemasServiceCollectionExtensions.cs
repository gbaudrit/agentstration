using Agentstration.Infrastructure.Schemas;
using Agentstration.Schemas;
using Agentstration.Schemas.Aep;
using Agentstration.Schemas.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Infrastructure;

internal static class SchemasServiceCollectionExtensions
{
    internal static IServiceCollection AddAgentstrationSchemas(this IServiceCollection services)
    {
        services.AddSingleton<ISchemaFormatHandler, BuiltinJsonSchemaFormatHandler>();
        services.AddSingleton(new AepSchemaFormatOptions());
        services.AddSingleton<ISchemaFormatHandlerSource, AepSchemaFormatHandlerSource>();
        services.AddSingleton<ISchemaFormatRegistry, SchemaFormatRegistry>();
        services.AddSingleton<SchemaManagementService>();
        services.AddSingleton<ISchemaBindingResolver, ManagementSchemaBindingResolver>();
        return services;
    }
}
