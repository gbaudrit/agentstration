using Agentstration.Aep.Abstractions;
using Agentstration.Aep.AspNetCore;
using Agentstration.Extensions.OpenApi;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<IValidateOptions<OpenApiOptions>, OpenApiOptionsValidator>();
builder.Services.AddOptions<OpenApiOptions>()
    .Bind(builder.Configuration.GetSection(OpenApiOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton<IOpenApiAddressResolver, SystemOpenApiAddressResolver>();
builder.Services.AddSingleton<OpenApiDestinationPolicy>();
builder.Services.AddSingleton<OpenApiContractParser>();
builder.Services.AddHttpClient<OpenApiService>()
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        MaxConnectionsPerServer = 8,
        ConnectTimeout = TimeSpan.FromSeconds(10)
    });
builder.Services.AddAgentstrationAep(options =>
{
    options.Extension = new AepExtensionIdentity(
        "Agentstration.Extensions.OpenApi", "OpenAPI Discovery and Acquisition", "1.0.0",
        "Bounded governed OpenAPI 3.0 and 3.1 contract discovery and explicit operation invocation.");
    options.McpServers.Add(new("openapi", "/mcp"));
    options.SchemaFormats.Add(new AepSchemaFormatContribution(
        "openapi.validation", "openapi", "OpenAPI", "1.0.0",
        ["https://spec.openapis.org/oas/3.0", "https://spec.openapis.org/oas/3.1"],
        new(new("openapi", "openapi_validate_definition"), new("openapi", "openapi_validate_instance")),
        Description: "Validates OpenAPI 3.0 and 3.1 JSON or YAML contracts under bounded reference rules."));
    options.Tools.Add(new("openapi.contract.discover", "Discover OpenAPI contracts", new("openapi", "openapi_discover"),
        "Discover only explicit bounded same-origin OpenAPI candidates; discovery does not grant execution authority."));
    options.Tools.Add(new("openapi.operation.invoke", "Invoke selected OpenAPI operation", new("openapi", "openapi_invoke"),
        "Invoke exactly one promoted selector tied to an exact discovered contract digest."));
    options.DataSourceProfileBundles.Add(OpenApiDataSourceProfileBundle.Create());
});
builder.Services.AddAepEnrollmentAuthentication(builder.Configuration);
builder.Services.AddMcpServer().WithHttpTransport().WithToolsFromAssembly();

var app = builder.Build();
app.MapAgentstrationAep();
app.MapMcp("/mcp");
await app.RunAsync();

public partial class Program;
