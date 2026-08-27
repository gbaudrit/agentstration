using Agentstration.Aep.AspNetCore;
using Agentstration.Extensions.Ollama;
using Microsoft.Extensions.AI;
using OllamaSharp;

var builder = WebApplication.CreateBuilder(args);
var endpointText = builder.Configuration["Ollama:Endpoint"]
    ?? builder.Configuration.GetConnectionString("ollama")
    ?? "http://localhost:11434";
if (!Uri.TryCreate(endpointText, UriKind.Absolute, out var endpoint))
    throw new InvalidOperationException("Ollama:Endpoint must be an absolute URL.");
builder.Services.AddSingleton(new OllamaExtensionOptions(endpoint));
builder.Services.AddHttpClient("ollama", client =>
{
    client.BaseAddress = endpoint;
    client.Timeout = TimeSpan.FromSeconds(90);
});
builder.Services.AddSingleton<OllamaApiClient>(services => new OllamaApiClient(services.GetRequiredService<IHttpClientFactory>().CreateClient("ollama")));
builder.Services.AddSingleton<IChatClient>(services => services.GetRequiredService<OllamaApiClient>());
builder.Services.AddAgentstrationAep(options =>
{
    options.Extension = new(
        "Agentstration.Extensions.Ollama",
        "Ollama",
        "1.0.0",
        "Agentstration AEP model-provider extension for Ollama.");
    options.OptionSets.Add(OllamaOptionContracts.ModelProfile);
})
    .AddModelProvider<OllamaAepModelProvider>();

var app = builder.Build();
app.MapAgentstrationAep();
await app.RunAsync();

public partial class Program;
