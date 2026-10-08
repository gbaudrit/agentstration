using Agentstration.Aep.Abstractions;
using Agentstration.Aep.AspNetCore;
using Agentstration.Extensions.Crawl4AI;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<IValidateOptions<Crawl4AiOptions>, Crawl4AiOptionsValidator>();
builder.Services.AddOptions<Crawl4AiOptions>()
    .Bind(builder.Configuration.GetSection(Crawl4AiOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IDestinationAddressResolver, SystemDestinationAddressResolver>();
builder.Services.AddSingleton<DestinationPolicy>();
builder.Services.AddSingleton<ICrawl4AiTokenProvider, ConfiguredCrawl4AiTokenProvider>();
builder.Services.AddSingleton<ICrawlContentStore, FileCrawlContentStore>();
builder.Services.AddHttpClient<Crawl4AiClient>()
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
builder.Services.AddSingleton<Crawl4AiAcquisitionService>();
builder.Services.AddAgentstrationAep(options =>
{
    options.Extension = new AepExtensionIdentity(
        "Agentstration.Extensions.Crawl4AI",
        "Crawl4AI Web Acquisition",
        "1.0.0",
        "Bounded governed web acquisition through a self-hosted Crawl4AI service.");
    options.McpServers.Add(new AepMcpServerDescriptor("crawl4ai", "/mcp"));
    options.Tools.Add(new AepToolContribution("web.fetch", "Fetch web page", new("crawl4ai", "web_fetch"), "Acquire one allowlisted web page as temporary referenced content."));
    options.Tools.Add(new AepToolContribution("web.crawl", "Crawl web pages", new("crawl4ai", "web_crawl"), "Acquire a bounded allowlisted web crawl as page references and one normalized corpus reference."));
    options.Tools.Add(new AepToolContribution("content.extract", "Extract acquired content", new("crawl4ai", "content_extract"), "Extract normalized text from acquired temporary content."));
    options.Tools.Add(new AepToolContribution("content.read", "Read acquired content", new("crawl4ai", "content_read"), "Read a bounded chunk from acquired temporary content."));
    options.Tools.Add(new AepToolContribution("content.delete", "Delete acquired content", new("crawl4ai", "content_delete"), "Delete acquired temporary content after transfer."));
    options.DataSourceProfileBundles.Add(Crawl4AiDataSourceProfileBundle.Create());
});
builder.Services.AddAepEnrollmentAuthentication(builder.Configuration);
builder.Services.AddMcpServer().WithHttpTransport().WithToolsFromAssembly();

var app = builder.Build();
app.MapAgentstrationAep();
app.MapMcp("/mcp");
app.MapGet("/health/ready", async (Crawl4AiClient client, CancellationToken cancellationToken) =>
    await client.IsReadyAsync(cancellationToken) ? Results.Ok() : Results.StatusCode(StatusCodes.Status503ServiceUnavailable));
await app.RunAsync();

public partial class Program;
