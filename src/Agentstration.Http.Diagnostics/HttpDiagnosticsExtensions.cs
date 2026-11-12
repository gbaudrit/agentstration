using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Agentstration.Http.Diagnostics;

public static class HttpDiagnosticsExtensions
{
    public const string ActivitySourceName = "Agentstration.Http.Diagnostics";
    // Shared by name with the legacy GenAI handler to keep host diagnostics provider-neutral.
    internal const string CaptureOwnerKey = "Agentstration.Http.Diagnostics.Active";

    public static void AddAgentstrationHttpDiagnostics(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var section = builder.Configuration.GetSection(HttpDiagnosticsOptions.SectionName);
        foreach (var category in section.GetSection(nameof(HttpDiagnosticsOptions.Categories)).GetChildren())
            if (!Enum.TryParse<HttpDiagnosticCategory>(category.Key, ignoreCase: true, out var name) || !Enum.IsDefined(name) ||
                !Enum.TryParse<HttpCaptureLevel>(category.Value, ignoreCase: true, out var level) || !Enum.IsDefined(level))
                throw new InvalidOperationException("Observability:Http:Categories requires known HTTP categories and capture levels.");
        var options = section.Get<HttpDiagnosticsOptions>(binding => binding.ErrorOnUnknownConfiguration = true) ?? new();
        options.Validate(builder.Environment.IsDevelopment());
        builder.Services.AddSingleton(options);
        if (!options.Enabled) return;
        builder.Services.AddTransient<HttpDiagnosticsHandler>();
        builder.Services.ConfigureHttpClientDefaults(client => client.AddHttpMessageHandler<HttpDiagnosticsHandler>());
    }

    public static IApplicationBuilder UseAgentstrationHttpDiagnostics(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.ApplicationServices.GetRequiredService<HttpDiagnosticsOptions>().Enabled
            ? app.UseMiddleware<HttpDiagnosticsMiddleware>() : app;
    }
}
