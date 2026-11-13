using System.Net;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;

namespace Agentstration.Playwright.AcquisitionFailure;

internal static class FixtureHost
{
    public static async Task Main()
    {
        await using var factory = new AcquisitionFailureFactory();
        var address = new Uri(Environment.GetEnvironmentVariable("ASPNETCORE_URLS")
            ?? throw new InvalidOperationException("The browser fixture requires ASPNETCORE_URLS."));
        // Configure before Build; UseKestrel(port) mutates addresses after the minimal app can start.
        factory.UseKestrel(options => options.Listen(IPAddress.Loopback, address.Port));
        factory.StartServer();
        var shutdown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lifetime = factory.Services.GetRequiredService<IHostApplicationLifetime>();
        using var registration = lifetime.ApplicationStopping.Register(() => shutdown.TrySetResult());
        await shutdown.Task;
    }
}

internal sealed class AcquisitionFailureFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseContentRoot(Path.Combine(Directory.GetCurrentDirectory(), "src", "Agentstration.Web"));
        builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddCommandLine(Environment.GetCommandLineArgs().Skip(1).ToArray()));
        builder.UseEnvironment("Development");
        builder.ConfigureLogging(logging => logging.AddConsole());
        builder.ConfigureServices(services => services.AddSingleton<IHttpMessageHandlerBuilderFilter, FixtureTransportFilter>());
    }
}

// Replace only the fixture response transport. SafeHttpContentFetcher still validates
// the URL, HTTP status and media type; no production service or network policy changes.
internal sealed class FixtureTransportFilter : IHttpMessageHandlerBuilderFilter
{
    public Action<HttpMessageHandlerBuilder> Configure(Action<HttpMessageHandlerBuilder> next) => builder =>
    {
        next(builder);
        if (builder.Name == "IHttpContentFetcher") builder.AdditionalHandlers.Insert(0, new HtmlResponseHandler());
    };
}

internal sealed class HtmlResponseHandler : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri?.AbsoluteUri != "https://acquisition-failure.example.test/rejected.html")
            return base.SendAsync(request, cancellationToken);
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            RequestMessage = request,
            Content = new StringContent("<html><body>Offline acquisition fixture</body></html>", Encoding.UTF8, "text/html")
        });
    }
}
