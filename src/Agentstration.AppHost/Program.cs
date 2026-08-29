var builder = DistributedApplication.CreateBuilder(args);
var slot = builder.Configuration["Agentstration:Slot"] ?? "main";
if (!System.Text.RegularExpressions.Regex.IsMatch(slot, "^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$"))
{
    throw new InvalidOperationException("Agentstration:Slot must contain only lowercase letters, digits, and internal hyphens (maximum 63 characters).");
}

var defaultSlotDataPath = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "..", "..", ".agentstration", "slots", slot));
var slotDataPath = Path.GetFullPath(builder.Configuration["Agentstration:SlotDataPath"] ?? defaultSlotDataPath);
Directory.CreateDirectory(slotDataPath);
var configuredBootstrapPath = builder.Configuration["Agentstration:Bootstrap:Path"];
var bootstrapPath = string.IsNullOrWhiteSpace(configuredBootstrapPath)
    ? string.Empty
    : Path.GetFullPath(configuredBootstrapPath, builder.AppHostDirectory);

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
var localAiEndpoint = builder.Configuration["LocalAI:Endpoint"] ?? "http://localhost:8081";
if (!Uri.TryCreate(localAiEndpoint, UriKind.Absolute, out var parsedLocalAiEndpoint)
    || (parsedLocalAiEndpoint.Scheme != Uri.UriSchemeHttp && parsedLocalAiEndpoint.Scheme != Uri.UriSchemeHttps))
{
    throw new InvalidOperationException("LocalAI:Endpoint must be an absolute HTTP(S) URL.");
}

var ollamaExtension = builder.AddProject<Projects.Agentstration_Extensions_Ollama>("ollama-extension")
    .WithEnvironment("Agentstration__Slot", slot)
    .WithEnvironment("Ollama__Endpoint", parsedOllamaEndpoint.AbsoluteUri)
    .WithHttpHealthCheck("/health");
var llamaCppExtension = builder.AddProject<Projects.Agentstration_Extensions_LlamaCpp>("llama-cpp-extension")
    .WithEnvironment("Agentstration__Slot", slot)
    .WithEnvironment("LlamaCpp__Endpoint", parsedLlamaCppEndpoint.AbsoluteUri)
    .WithHttpHealthCheck("/health");
var localAiExtension = builder.AddProject<Projects.Agentstration_Extensions_LocalAI>("localai-extension")
    .WithEnvironment("Agentstration__Slot", slot)
    .WithEnvironment("LocalAI__Endpoint", parsedLocalAiEndpoint.AbsoluteUri)
    .WithHttpHealthCheck("/health");
var utilitiesExtension = builder.AddProject<Projects.Agentstration_Extensions_Utilities>("utilities-extension")
    .WithEnvironment("Agentstration__Slot", slot)
    .WithHttpHealthCheck("/health");

var console = builder.AddProject<Projects.Agentstration_Web>("agentstration-console")
    .WithEnvironment("Agentstration__Slot", slot)
    .WithEnvironment("Agentstration__SlotDataPath", slotDataPath)
    .WithEnvironment("Agentstration__Bootstrap__Path", bootstrapPath)
    .WithEnvironment("Data__Directory", slotDataPath)
    .WithEnvironment("Data__ControlPlanePath", Path.Combine(slotDataPath, "control-plane.db"))
    .WithEnvironment("Data__WorkPlanePath", Path.Combine(slotDataPath, "work-plane.db"))
    .WithEnvironment("Data__FlowPath", Path.Combine(slotDataPath, "flow-plane.db"))
    .WithEnvironment("Data__RuntimePath", Path.Combine(slotDataPath, "runtime-plane.db"))
    .WithEnvironment("ConnectionStrings__ollama-extension", ollamaExtension.GetEndpoint("http"))
    .WithEnvironment("ConnectionStrings__llama-cpp-extension", llamaCppExtension.GetEndpoint("http"))
    .WithEnvironment("ConnectionStrings__localai-extension", localAiExtension.GetEndpoint("http"))
    .WithEnvironment("Agentstration__Extensions__Agentstration.Extensions.Ollama__Endpoint", ollamaExtension.GetEndpoint("http"))
    .WithEnvironment("Agentstration__Extensions__Agentstration.Extensions.LlamaCpp__Endpoint", llamaCppExtension.GetEndpoint("http"))
    .WithEnvironment("Agentstration__Extensions__Agentstration.Extensions.LocalAI__Endpoint", localAiExtension.GetEndpoint("http"))
    .WithEnvironment("Agentstration__Extensions__Agentstration.Extensions.Utilities__Endpoint", utilitiesExtension.GetEndpoint("http"))
    .WithHttpHealthCheck("/health")
    .WaitFor(ollamaExtension);
console.WaitFor(llamaCppExtension);
console.WaitFor(localAiExtension);
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
    .WithEnvironment("Agentstration__Slot", slot)
    .WithEnvironment("Agentstration__ApiBaseUrl", console.GetEndpoint("http"))
    .WithHttpHealthCheck("/health")
    .WaitFor(console);

console.WithEnvironment("Agentstration__WorkplaceBaseUrl", workplace.GetEndpoint("http"));
await builder.Build().RunAsync();
