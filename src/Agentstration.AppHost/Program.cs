using Agentstration.AppHost;

var builder = DistributedApplication.CreateBuilder(args);
var worktreeRoot = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "..", ".."));
var slot = builder.Configuration["Agentstration:Slot"] ?? "main";
var dynamicApplicationPorts = bool.TryParse(
    builder.Configuration["Agentstration:DynamicApplicationPorts"],
    out var configuredDynamicApplicationPorts)
    && configuredDynamicApplicationPorts;
var instanceId = DevelopmentInstanceIdentity.Resolve(
    builder.Configuration["Agentstration:InstanceId"],
    worktreeRoot);
var storageProvider = builder.Configuration["Agentstration:Storage:Provider"] ?? "Sqlite";
if (!string.Equals(storageProvider, "Sqlite", StringComparison.OrdinalIgnoreCase)
    && !string.Equals(storageProvider, "PostgreSql", StringComparison.OrdinalIgnoreCase))
{
    throw new InvalidOperationException("Agentstration:Storage:Provider must be either 'Sqlite' or 'PostgreSql'.");
}
if (!System.Text.RegularExpressions.Regex.IsMatch(slot, "^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$"))
{
    throw new InvalidOperationException("Agentstration:Slot must contain only lowercase letters, digits, and internal hyphens (maximum 63 characters).");
}

var defaultSlotDataPath = Path.Combine(worktreeRoot, ".agentstration", "slots", slot);
var slotDataPath = Path.GetFullPath(builder.Configuration["Agentstration:SlotDataPath"] ?? defaultSlotDataPath);
Directory.CreateDirectory(slotDataPath);
var configuredBootstrapPath = builder.Configuration["Agentstration:Bootstrap:Path"];
var bootstrapPath = string.IsNullOrWhiteSpace(configuredBootstrapPath)
    ? string.Empty
    : Path.GetFullPath(configuredBootstrapPath, builder.AppHostDirectory);
var initialBootstrapEnabled = bool.TryParse(
    builder.Configuration["Agentstration:Bootstrap:InitialBootstrapEnabled"],
    out var configuredInitialBootstrapEnabled)
    && configuredInitialBootstrapEnabled;
var initialBootstrapProfiles = builder.Configuration
    .GetSection("Agentstration:Bootstrap:InitialProfiles")
    .GetChildren()
    .Select(profile => profile.Value ?? string.Empty)
    .ToArray();

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
    .WithHttpHealthCheck("/health")
    .WithDynamicHostPorts(dynamicApplicationPorts);
var llamaCppExtension = builder.AddProject<Projects.Agentstration_Extensions_LlamaCpp>("llama-cpp-extension")
    .WithEnvironment("Agentstration__Slot", slot)
    .WithEnvironment("LlamaCpp__Endpoint", parsedLlamaCppEndpoint.AbsoluteUri)
    .WithHttpHealthCheck("/health")
    .WithDynamicHostPorts(dynamicApplicationPorts);
var localAiExtension = builder.AddProject<Projects.Agentstration_Extensions_LocalAI>("localai-extension")
    .WithEnvironment("Agentstration__Slot", slot)
    .WithEnvironment("LocalAI__Endpoint", parsedLocalAiEndpoint.AbsoluteUri)
    .WithHttpHealthCheck("/health")
    .WithDynamicHostPorts(dynamicApplicationPorts);
var utilitiesExtension = builder.AddProject<Projects.Agentstration_Extensions_Utilities>("utilities-extension")
    .WithEnvironment("Agentstration__Slot", slot)
    .WithHttpHealthCheck("/health")
    .WithDynamicHostPorts(dynamicApplicationPorts);

var console = builder.AddProject<Projects.Agentstration_Web>("agentstration-console")
    .WithEnvironment("Agentstration__Slot", slot)
    .WithEnvironment("Agentstration__InstanceId", instanceId)
    .WithEnvironment("Agentstration__SlotDataPath", slotDataPath)
    .WithEnvironment("Agentstration__Bootstrap__Path", bootstrapPath)
    .WithEnvironment(
        "Agentstration__Bootstrap__InitialBootstrapEnabled",
        initialBootstrapEnabled ? "true" : "false")
    .WithEnvironment("Data__Directory", slotDataPath)
    .WithEnvironment("ConnectionStrings__ollama-extension", ollamaExtension.GetEndpoint("http"))
    .WithEnvironment("ConnectionStrings__llama-cpp-extension", llamaCppExtension.GetEndpoint("http"))
    .WithEnvironment("ConnectionStrings__localai-extension", localAiExtension.GetEndpoint("http"))
    .WithEnvironment("Agentstration__Extensions__Agentstration.Extensions.Ollama__Endpoint", ollamaExtension.GetEndpoint("http"))
    .WithEnvironment("Agentstration__Extensions__Agentstration.Extensions.LlamaCpp__Endpoint", llamaCppExtension.GetEndpoint("http"))
    .WithEnvironment("Agentstration__Extensions__Agentstration.Extensions.LocalAI__Endpoint", localAiExtension.GetEndpoint("http"))
    .WithEnvironment("Agentstration__Extensions__Agentstration.Extensions.Utilities__Endpoint", utilitiesExtension.GetEndpoint("http"))
    .WithHttpHealthCheck("/health")
    .WaitFor(ollamaExtension)
    .WithDynamicHostPorts(dynamicApplicationPorts);
if (string.Equals(storageProvider, "PostgreSql", StringComparison.OrdinalIgnoreCase))
    console.WithPostgreSqlStorage(builder, slot, instanceId);
else
    console.WithSqliteStorage(slotDataPath);
for (var index = 0; index < initialBootstrapProfiles.Length; index++)
    console.WithEnvironment($"Agentstration__Bootstrap__InitialProfiles__{index}", initialBootstrapProfiles[index]);
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
    .WithEnvironment("Agentstration__InstanceId", instanceId)
    .WithEnvironment("Agentstration__ApiBaseUrl", console.GetEndpoint("http"))
    .WithHttpHealthCheck("/health")
    .WaitFor(console)
    .WithDynamicHostPorts(dynamicApplicationPorts);

console.WithEnvironment("Agentstration__WorkplaceBaseUrl", workplace.GetEndpoint("http"));
await builder.Build().RunAsync();
