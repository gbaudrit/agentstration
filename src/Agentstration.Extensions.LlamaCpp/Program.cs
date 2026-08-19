using Agentstration.Aep.AspNetCore;
using Agentstration.Extensions.LlamaCpp;

var builder = WebApplication.CreateBuilder(args);
var endpointText = builder.Configuration["LlamaCpp:Endpoint"] ?? "http://localhost:8080";
if (!Uri.TryCreate(endpointText, UriKind.Absolute, out var endpoint)
    || (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps))
{
    throw new InvalidOperationException("LlamaCpp:Endpoint must be an absolute HTTP(S) URL.");
}

builder.Services.AddSingleton(new LlamaCppExtensionOptions(endpoint));
builder.Services.AddHttpClient<LlamaCppAepModelProvider>(client =>
{
    client.BaseAddress = new Uri(endpoint.AbsoluteUri.TrimEnd('/') + '/', UriKind.Absolute);
    client.Timeout = TimeSpan.FromSeconds(90);
});
builder.Services.AddAgentstrationAep(options => options.Extension = new(
    "Agentstration.Extensions.LlamaCpp",
    "llama.cpp",
    "1.0.0",
    "Agentstration AEP model-provider extension for a local llama.cpp server."));
builder.Services.AddSingleton<IAepModelProvider>(services => services.GetRequiredService<LlamaCppAepModelProvider>());

var app = builder.Build();
app.MapAgentstrationAep();
await app.RunAsync();

public partial class Program;
