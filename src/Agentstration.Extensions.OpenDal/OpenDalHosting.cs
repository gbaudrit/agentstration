using Agentstration.Aep.Abstractions;
using Agentstration.Aep.AspNetCore;
using Agentstration.Asp.Aep;
using Agentstration.Asp.Server;
using Microsoft.Extensions.Options;
using ModelContextProtocol.AspNetCore;

namespace Agentstration.Extensions.OpenDal;

public static class OpenDalHosting
{
    public static IServiceCollection AddOpenDalStorage(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IValidateOptions<OpenDalOptions>, OpenDalOptionsValidator>();
        services.AddOptions<OpenDalOptions>().BindConfiguration(OpenDalOptions.SectionName).ValidateOnStart();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<OpenDalExecutionGate>();
        services.AddHttpContextAccessor();
        services.AddSingleton<IAspAepBindingFactory, OpenDalBindingFactory>();
        services.AddAgentstrationAep(options =>
        {
            options.Extension = new(OpenDalStorageTypes.ExtensionId, "OpenDAL Storage", OpenDalStorageTypes.Version);
            options.Capabilities[AepCapabilityNames.BoundValues] = new(AepProtocol.BoundValuesCapabilityVersion);
            options.Capabilities[AepCapabilityNames.SecretAccess] = new(AepProtocol.SecretAccessVersion);
            options.McpServers.Add(new("content", "/mcp"));
            foreach (var contribution in OpenDalStorageTypes.Contributions) options.StorageTypes.Add(contribution);
        });
        services.AddAepEnrollmentAuthentication(configuration);
        services.AddSingleton(services => new AspAepBindings(
            services.GetRequiredService<IAspAepBindingFactory>(), services.GetRequiredService<IHttpContextAccessor>(),
            caller => services.GetRequiredService<IOptionsMonitor<OpenDalOptions>>().CurrentValue.Authorizes(caller),
            services.GetRequiredService<TimeProvider>()));
        services.AddSingleton(services => new AspMcpServer(services.GetRequiredService<AspAepBindings>().ResolveAsync,
            services.GetRequiredService<TimeProvider>()));
        services.AddMcpServer().WithHttpTransport(options => options.SessionMode = HttpServerSessionMode.Stateless)
            .WithListToolsHandler((request, ct) => request.Services!.GetRequiredService<AspMcpServer>().ListToolsAsync(request, ct))
            .WithCallToolHandler((request, ct) => request.Services!.GetRequiredService<AspMcpServer>().CallToolAsync(request, ct));
        return services;
    }

    public static WebApplication MapOpenDalStorage(this WebApplication app)
    {
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapAgentstrationAep();
        app.MapAspAepBindings();
        app.MapMcp("/mcp").RequireAuthorization(AepAuthenticationDefaults.Policy);
        return app;
    }
}
