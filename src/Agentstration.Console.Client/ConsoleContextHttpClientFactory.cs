using Agentstration.Web.Components.State;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;

namespace Agentstration.Web.Console;

public interface IConsoleContextHttpClientFactory
{
    HttpClient CreateClient(string name);
}

public sealed class ConsoleContextHttpClientFactory(
    IHttpMessageHandlerFactory handlers,
    IOptionsMonitor<HttpClientFactoryOptions> options,
    IConsoleNavigationContext navigationContext) : IConsoleContextHttpClientFactory
{
    public HttpClient CreateClient(string name)
    {
        var pooledHandler = handlers.CreateHandler(name);
        var client = new HttpClient(new ConsoleNavigationContextHandler(navigationContext)
        {
            InnerHandler = new PooledHandlerLease(pooledHandler)
        }, disposeHandler: true);
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

public sealed class ConsoleNavigationContextHandler(IConsoleNavigationContext navigationContext) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!request.Headers.Contains(ConsoleContextHeaders.Name)
            && navigationContext.ScopeRef is { Length: > 0 } scopeRef)
            request.Headers.TryAddWithoutValidation(ConsoleContextHeaders.Name, scopeRef);
        return base.SendAsync(request, cancellationToken);
    }
}

public static class ConsoleContextHeaders
{
    public const string Name = "X-Agentstration-Context";

    public static void Apply(HttpClient client, string? scopeRef)
    {
        ArgumentNullException.ThrowIfNull(client);
        if (!string.IsNullOrWhiteSpace(scopeRef)
            && !client.DefaultRequestHeaders.Contains(Name))
            client.DefaultRequestHeaders.TryAddWithoutValidation(Name, scopeRef);
    }
}
