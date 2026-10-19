using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;

namespace Agentstration.Console.Web.Security;

public sealed record BffDelegationTarget(string Name, string Audience, Uri Origin);

public sealed class BffDelegatingHttpClientFactory(
    IHttpMessageHandlerFactory handlers,
    IOptionsMonitor<HttpClientFactoryOptions> options,
    IEnumerable<BffDelegationTarget> targets,
    IServiceProvider services) : IHttpClientFactory
{
    private readonly IReadOnlyDictionary<string, BffDelegationTarget> targetsByName =
        targets.ToDictionary(value => value.Name, StringComparer.Ordinal);

    public HttpClient CreateClient(string name)
    {
        var pooledHandler = handlers.CreateHandler(name);
        HttpMessageHandler handler = new PooledHandlerLease(pooledHandler);
        if (targetsByName.TryGetValue(name, out var target))
            handler = new BffDelegationHandler(
                services.GetRequiredService<BffDelegationTokenProvider>(), target.Audience, target.Origin)
            { InnerHandler = handler };
        var client = new HttpClient(handler, disposeHandler: true);
        foreach (var configure in options.Get(name).HttpClientActions) configure(client);
        return client;
    }

    private sealed class PooledHandlerLease(HttpMessageHandler handler) : HttpMessageHandler
    {
        private readonly HttpMessageInvoker invoker = new(handler, disposeHandler: false);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            invoker.SendAsync(request, cancellationToken);

        protected override void Dispose(bool disposing)
        {
            if (disposing) invoker.Dispose();
            base.Dispose(disposing);
        }
    }
}
