using Agentstration.Web.Components.State;

namespace Agentstration.Web.Console;

public interface IConsoleContextHttpClientFactory
{
    HttpClient CreateClient(string name);
}

public sealed class ConsoleContextHttpClientFactory(
    IHttpClientFactory clients,
    IConsoleNavigationContext navigationContext) : IConsoleContextHttpClientFactory
{
    public HttpClient CreateClient(string name)
    {
        var client = clients.CreateClient(name);
        ConsoleContextHeaders.Apply(client, navigationContext.ScopeRef);
        return client;
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
