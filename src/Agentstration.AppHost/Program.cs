var builder = DistributedApplication.CreateBuilder(args);
var canonicalFlowPath = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "..", "Agentstration.Web", ".agentstration", "flow-plane.db"));
var consoleInternalFlowPath = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, ".agentstration", "console-flow-plane.db"));
var ollamaEndpoint = builder.Configuration["Ollama:Endpoint"] ?? "http://localhost:11434";
if (!Uri.TryCreate(ollamaEndpoint, UriKind.Absolute, out var parsedOllamaEndpoint)
    || (parsedOllamaEndpoint.Scheme != Uri.UriSchemeHttp && parsedOllamaEndpoint.Scheme != Uri.UriSchemeHttps))
{
    throw new InvalidOperationException("Ollama:Endpoint must be an absolute HTTP(S) URL.");
}

var ollamaExtension = builder.AddProject<Projects.Agentstration_Extensions_Ollama>("ollama-extension")
    .WithEnvironment("Ollama__Endpoint", parsedOllamaEndpoint.AbsoluteUri)
    .WithHttpHealthCheck("/health");
var utilitiesExtension = builder.AddProject<Projects.Agentstration_Extensions_Utilities>("utilities-extension").WithHttpHealthCheck("/health");

var workApi = builder.AddProject<Projects.Agentstration_Work_Api>("agentstration-work-api")
    .WithEnvironment("AI__Provider", "Deterministic")
    .WithEnvironment("Data__FlowPath", canonicalFlowPath)
    .WithEnvironment("ConnectionStrings__ollama-extension", ollamaExtension.GetEndpoint("http"))
    .WithEnvironment("Agentstration__Extensions__Agentstration.Extensions.Ollama__Endpoint", ollamaExtension.GetEndpoint("http"))
    .WithEnvironment("Agentstration__Extensions__Agentstration.Extensions.Utilities__Endpoint", utilitiesExtension.GetEndpoint("http"))
    .WithHttpHealthCheck("/health")
    .WaitFor(ollamaExtension);

var workplace = builder.AddProject<Projects.Agentstration_Workplace_Web>("agentstration-workplace")
    .WithEnvironment("Agentstration__ApiBaseUrl", workApi.GetEndpoint("http"))
    .WithHttpHealthCheck("/health")
    .WaitFor(workApi);

var console = builder.AddProject<Projects.Agentstration_Web>("agentstration-console")
    .WithEnvironment("Agentstration__WorkApi__BaseAddress", workApi.GetEndpoint("http"))
    .WithEnvironment("Data__FlowPath", consoleInternalFlowPath)
    .WithEnvironment("Agentstration__WorkplaceBaseUrl", workplace.GetEndpoint("http"))
    .WithEnvironment("ConnectionStrings__ollama-extension", ollamaExtension.GetEndpoint("http"))
    .WithEnvironment("Agentstration__Extensions__Agentstration.Extensions.Ollama__Endpoint", ollamaExtension.GetEndpoint("http"))
    .WithEnvironment("Agentstration__Extensions__Agentstration.Extensions.Utilities__Endpoint", utilitiesExtension.GetEndpoint("http"))
    .WithHttpHealthCheck("/health")
    .WaitFor(workApi)
    .WaitFor(ollamaExtension);
console.WaitFor(utilitiesExtension);
console
    .WithEnvironment("Agentstration__ManagementApi__BaseAddress", console.GetEndpoint("http"))
    .WithEnvironment("Agentstration__RuntimeApi__BaseAddress", console.GetEndpoint("http"))
    .WithEnvironment("Agentstration__FlowApi__BaseAddress", workApi.GetEndpoint("http"));
await builder.Build().RunAsync();
