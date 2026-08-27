using System.Net.Http.Headers;
using Agentstration.Aep.AspNetCore;
using Agentstration.Extensions.LocalAI;

var builder = WebApplication.CreateBuilder(args);
var endpointText = builder.Configuration["LocalAI:Endpoint"] ?? "http://localhost:8081";
if (!Uri.TryCreate(endpointText, UriKind.Absolute, out var endpoint)
    || (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps))
{
    throw new InvalidOperationException("LocalAI:Endpoint must be an absolute HTTP(S) URL.");
}

var apiKey = builder.Configuration["LocalAI:ApiKey"];
builder.Services.AddHttpClient<LocalAiAepModelProvider>(client =>
{
    client.BaseAddress = new Uri(endpoint.AbsoluteUri.TrimEnd('/') + '/', UriKind.Absolute);
    client.Timeout = TimeSpan.FromSeconds(90);
    if (!string.IsNullOrWhiteSpace(apiKey)) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
});
builder.Services.AddAgentstrationAep(options =>
{
    options.Extension = new(
        "Agentstration.Extensions.LocalAI",
        "LocalAI",
        "1.0.0",
        "Agentstration AEP model-provider extension for a LocalAI server.");
    options.OptionSets.Add(LocalAiOptionContracts.ModelProfile);
});
builder.Services.AddSingleton<IAepModelProvider>(services => services.GetRequiredService<LocalAiAepModelProvider>());

var app = builder.Build();
app.MapAgentstrationAep();
await app.RunAsync();

public partial class Program;
