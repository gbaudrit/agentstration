using Aspire.Hosting.ApplicationModel;

namespace Agentstration.AppHost;

internal static class DynamicEndpointExtensions
{
    public static IResourceBuilder<TResource> WithDynamicHostPorts<TResource>(
        this IResourceBuilder<TResource> builder,
        bool enabled)
        where TResource : IResourceWithEndpoints
    {
        if (!enabled)
        {
            return builder;
        }

        foreach (var endpoint in builder.Resource.Annotations.OfType<EndpointAnnotation>())
        {
            endpoint.Port = null;
        }

        return builder;
    }
}
