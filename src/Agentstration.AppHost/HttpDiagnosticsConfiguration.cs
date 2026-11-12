using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Configuration;

namespace Agentstration.AppHost;

internal static class HttpDiagnosticsConfiguration
{
    internal static IResourceBuilder<ProjectResource> WithHttpDiagnostics(
        this IResourceBuilder<ProjectResource> resource, IConfiguration configuration)
    {
        foreach (var entry in configuration.GetSection("Observability:Http").AsEnumerable())
            if (entry.Value is not null)
                resource.WithEnvironment(entry.Key.Replace(":", "__", StringComparison.Ordinal), entry.Value);
        return resource;
    }
}
