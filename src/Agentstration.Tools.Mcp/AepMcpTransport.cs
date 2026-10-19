using System.Net.Http.Headers;
using Agentstration.Aep.Abstractions;
using Agentstration.Aep.Client;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;

namespace Agentstration.Tools.Mcp;

public sealed class AepMcpTransport(
    IHttpMessageHandlerFactory httpMessageHandlerFactory,
    ILoggerFactory loggerFactory)
{
    public async Task<AepManifest> DiscoverAsync(
        ResolvedAepExtension extension,
        string? expectedExtensionId,
        CancellationToken cancellationToken)
    {
        using var http = CreateHttpClient(
            McpToolServiceCollectionExtensions.AepClientName,
            extension.Registration.Definition.Endpoint,
            extension.AccessTokenProvider,
            TimeSpan.FromSeconds(15));
        var descriptor = await new AepClient(
            http,
            extension.AccessTokenProvider,
            expectedExtensionId: expectedExtensionId).DiscoverAsync(cancellationToken);
        var errors = AepDescriptorValidator.Validate(descriptor);
        if (errors.Count > 0)
            throw new AepProtocolException("aep_descriptor_invalid", string.Join(" ", errors));
        return descriptor;
    }

    public Task<McpClient> ConnectAsync(
        ResolvedAepExtension extension,
        AepMcpServerDescriptor server,
        CancellationToken cancellationToken) => ConnectAsync(
            AepDescriptorValidator.ResolveMcpEndpoint(extension.Registration.Definition.Endpoint, server),
            server.Id,
            extension.AccessTokenProvider,
            cancellationToken);

    public Task<McpClient> ConnectAsync(
        Uri endpoint,
        string name,
        IAepAccessTokenProvider? accessTokenProvider,
        CancellationToken cancellationToken)
    {
        var http = CreateHttpClient(
            accessTokenProvider is null
                ? McpToolServiceCollectionExtensions.McpClientName
                : McpToolServiceCollectionExtensions.AepMcpClientName,
            endpoint,
            accessTokenProvider,
            TimeSpan.FromSeconds(90));
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = endpoint, Name = name },
            http,
            loggerFactory,
            ownsHttpClient: true);
        return McpClient.CreateAsync(transport, loggerFactory: loggerFactory,
            cancellationToken: cancellationToken);
    }

    private HttpClient CreateHttpClient(
        string name,
        Uri endpoint,
        IAepAccessTokenProvider? accessTokenProvider,
        TimeSpan timeout)
    {
        HttpMessageHandler handler = httpMessageHandlerFactory.CreateHandler(name);
        if (accessTokenProvider is not null)
            handler = new AepBearerAuthenticationHandler(accessTokenProvider) { InnerHandler = handler };
        return new HttpClient(handler, disposeHandler: false) { BaseAddress = endpoint, Timeout = timeout };
    }

    private sealed class AepBearerAuthenticationHandler(IAepAccessTokenProvider accessTokenProvider)
        : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var token = await accessTokenProvider.GetAccessTokenAsync(cancellationToken);
            if (!string.IsNullOrWhiteSpace(token))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            try { return await base.SendAsync(request, cancellationToken); }
            finally { request.Headers.Authorization = null; }
        }
    }
}
