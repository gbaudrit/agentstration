var builder = DistributedApplication.CreateBuilder(args);
var ollamaEndpoint = builder.Configuration["Ollama:Endpoint"] ?? "http://localhost:11434";
if (!Uri.TryCreate(ollamaEndpoint, UriKind.Absolute, out var parsedOllamaEndpoint)
    || (parsedOllamaEndpoint.Scheme != Uri.UriSchemeHttp && parsedOllamaEndpoint.Scheme != Uri.UriSchemeHttps))
{
    throw new InvalidOperationException("Ollama:Endpoint must be an absolute HTTP(S) URL.");
}
var llamaCppEndpoint = builder.Configuration["LlamaCpp:Endpoint"] ?? "http://localhost:8080";
if (!Uri.TryCreate(llamaCppEndpoint, UriKind.Absolute, out var parsedLlamaCppEndpoint)
    || (parsedLlamaCppEndpoint.Scheme != Uri.UriSchemeHttp && parsedLlamaCppEndpoint.Scheme != Uri.UriSchemeHttps))
{
    throw new InvalidOperationException("LlamaCpp:Endpoint must be an absolute HTTP(S) URL.");
}

var ollamaExtension = builder.AddProject<Projects.Agentstration_Extensions_Ollama>("ollama-extension")
    .WithEnvironment("Ollama__Endpoint", parsedOllamaEndpoint.AbsoluteUri)
    .WithHttpHealthCheck("/health");
var llamaCppExtension = builder.AddProject<Projects.Agentstration_Extensions_LlamaCpp>("llama-cpp-extension")
    .WithEnvironment("LlamaCpp__Endpoint", parsedLlamaCppEndpoint.AbsoluteUri)
    .WithHttpHealthCheck("/health");
var utilitiesExtension = builder.AddProject<Projects.Agentstration_Extensions_Utilities>("utilities-extension").WithHttpHealthCheck("/health");

var console = builder.AddProject<Projects.Agentstration_Web>("agentstration-console")
    .WithEnvironment("ConnectionStrings__ollama-extension", ollamaExtension.GetEndpoint("http"))
    .WithEnvironment("ConnectionStrings__llama-cpp-extension", llamaCppExtension.GetEndpoint("http"))
    .WithEnvironment("Agentstration__Extensions__Agentstration.Extensions.Ollama__Endpoint", ollamaExtension.GetEndpoint("http"))
    .WithEnvironment("Agentstration__Extensions__Agentstration.Extensions.LlamaCpp__Endpoint", llamaCppExtension.GetEndpoint("http"))
    .WithEnvironment("Agentstration__Extensions__Agentstration.Extensions.Utilities__Endpoint", utilitiesExtension.GetEndpoint("http"))
    .WithHttpHealthCheck("/health")
    .WaitFor(ollamaExtension);
console.WaitFor(llamaCppExtension);
console.WaitFor(utilitiesExtension);
console
    .WithEnvironment("Agentstration__ManagementApi__BaseAddress", console.GetEndpoint("http"))
    .WithEnvironment("Agentstration__ManagementApi__ForwardSessionCookie", "true")
    .WithEnvironment("Agentstration__RuntimeApi__BaseAddress", console.GetEndpoint("http"))
    .WithEnvironment("Agentstration__RuntimeApi__ForwardSessionCookie", "true")
    .WithEnvironment("Agentstration__WorkApi__BaseAddress", console.GetEndpoint("http"))
    .WithEnvironment("Agentstration__WorkApi__ForwardSessionCookie", "true")
    .WithEnvironment("Agentstration__FlowApi__BaseAddress", console.GetEndpoint("http"))
    .WithEnvironment("Agentstration__FlowApi__ForwardSessionCookie", "true");

var workplace = builder.AddProject<Projects.Agentstration_Workplace_Web>("agentstration-workplace")
    .WithEnvironment("Agentstration__ApiBaseUrl", console.GetEndpoint("http"))
    .WithHttpHealthCheck("/health")
    .WaitFor(console);

console.WithEnvironment("Agentstration__WorkplaceBaseUrl", workplace.GetEndpoint("http"));
await builder.Build().RunAsync();
