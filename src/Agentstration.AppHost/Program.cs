using Agentstration.AppHost;
using Aspire.Hosting.ApplicationModel;

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
var aepEnrollmentMode = builder.Configuration["Agentstration:Aep:EnrollmentMode"] ?? "SharedKeyFile";
var usePairingCode = string.Equals(aepEnrollmentMode, "PairingCode", StringComparison.OrdinalIgnoreCase);
if (!usePairingCode && !string.Equals(aepEnrollmentMode, "SharedKeyFile", StringComparison.OrdinalIgnoreCase))
{
    throw new InvalidOperationException("Agentstration:Aep:EnrollmentMode must be either 'PairingCode' or 'SharedKeyFile'.");
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
var gitExtension = builder.AddProject<Projects.Agentstration_Extensions_Git>("git-source-extension")
    .WithEnvironment("Agentstration__Slot", slot)
    .WithHttpHealthCheck("/health")
    .WithDynamicHostPorts(dynamicApplicationPorts);
var utilitiesExtension = builder.AddProject<Projects.Agentstration_Extensions_Utilities>("utilities-extension")
    .WithEnvironment("Agentstration__Slot", slot)
    .WithHttpHealthCheck("/health")
    .WithDynamicHostPorts(dynamicApplicationPorts);
var developmentExtensions = new[]
{
    new DevelopmentAepExtension("Agentstration.Extensions.Ollama", "ollama-extension", ollamaExtension),
    new DevelopmentAepExtension("Agentstration.Extensions.LlamaCpp", "llama-cpp-extension", llamaCppExtension),
    new DevelopmentAepExtension("Agentstration.Extensions.LocalAI", "localai-extension", localAiExtension),
    new DevelopmentAepExtension("Agentstration.Extensions.Git", "git-source-extension", gitExtension),
    new DevelopmentAepExtension("Agentstration.Extensions.Utilities", "utilities-extension", utilitiesExtension)
};
var sharedKeys = usePairingCode
    ? new Dictionary<string, string>(StringComparer.Ordinal)
    : AepDevelopmentSharedKeys.Provision(
        Path.Combine(slotDataPath, "aep-shared-keys"),
        developmentExtensions.Select(extension => extension.ResourceName).ToArray());

var console = builder.AddProject<Projects.Agentstration_Web>("agentstration-console")
    .WithEnvironment("Agentstration__Slot", slot)
    .WithEnvironment("Agentstration__InstanceId", instanceId)
    .WithEnvironment("Agentstration__SlotDataPath", slotDataPath)
    .WithEnvironment("Agentstration__Bootstrap__Path", bootstrapPath)
    .WithEnvironment(
        "Agentstration__Bootstrap__InitialBootstrapEnabled",
        initialBootstrapEnabled ? "true" : "false")
    .WithEnvironment("Data__Directory", slotDataPath)
    .WithHttpHealthCheck("/health")
    .WithDynamicHostPorts(dynamicApplicationPorts);
console.WithEnvironment("Agentstration__Extensions__DiscoverOnStartup", "false");
if (usePairingCode)
{
    foreach (var extension in developmentExtensions)
        ConfigurePairingCode(extension.Resource, console, slotDataPath, extension.ResourceName);
}
else
{
    foreach (var extension in developmentExtensions)
    {
        var sharedKey = sharedKeys[extension.ResourceName];
        ConfigureSharedKey(console, extension.ExtensionId, extension.ResourceName, sharedKey);
        ConfigureSharedKeyExtension(extension.Resource, console, slotDataPath, extension.ResourceName, sharedKey);
    }
}
var allowedAepHosts = new[] { "localhost", "127.0.0.1", "::1" }
    .Concat(developmentExtensions.Select(extension => extension.ResourceName))
    .Distinct(StringComparer.OrdinalIgnoreCase)
    .ToArray();
for (var index = 0; index < allowedAepHosts.Length; index++)
{
    console
        .WithEnvironment($"Agentstration__Aep__Transport__AllowedHttpHosts__{index}", allowedAepHosts[index])
        .WithEnvironment($"Agentstration__Aep__Transport__AllowedPrivateNetworkHosts__{index}", allowedAepHosts[index]);
}
if (string.Equals(storageProvider, "PostgreSql", StringComparison.OrdinalIgnoreCase))
    console.WithPostgreSqlStorage(builder, slot, instanceId);
else
    console.WithSqliteStorage(slotDataPath);
for (var index = 0; index < initialBootstrapProfiles.Length; index++)
    console.WithEnvironment($"Agentstration__Bootstrap__InitialProfiles__{index}", initialBootstrapProfiles[index]);
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

static void ConfigureSharedKeyExtension(
    IResourceBuilder<ProjectResource> resource,
    IResourceBuilder<ProjectResource> authority,
    string slotDataPath,
    string resourceName,
    string path) => resource
    .WithEnvironment("Aep__EnrollmentMode", "SharedKeyFile")
    .WithEnvironment("Aep__SharedKeyFile__Path", path)
    .WithEnvironment("Aep__SharedKeyFile__AuthorityUrl", authority.GetEndpoint("http"))
    .WithEnvironment("Aep__SharedKeyFile__PublicEndpoint", resource.GetEndpoint("http"))
    .WithEnvironment("Aep__SharedKeyFile__StateFile", Path.Combine(slotDataPath, "aep-shared-key", $"{resourceName}.instance"))
    .WithEnvironment("Aep__SharedKeyFile__AllowInsecureHttp", "true");

static void ConfigurePairingCode(
    IResourceBuilder<ProjectResource> extension,
    IResourceBuilder<ProjectResource> authority,
    string slotDataPath,
    string resourceName) => extension
    .WithEnvironment("Aep__EnrollmentMode", "PairingCode")
    .WithEnvironment("Aep__PairingCode__AuthorityUrl", authority.GetEndpoint("http"))
    .WithEnvironment("Aep__PairingCode__PublicEndpoint", extension.GetEndpoint("http"))
    .WithEnvironment("Aep__PairingCode__StateFile", Path.Combine(slotDataPath, "aep-pairing", $"{resourceName}.json"))
    .WithEnvironment("Aep__PairingCode__AllowInsecureHttp", "true");

static void ConfigureSharedKey(
    IResourceBuilder<ProjectResource> resource,
    string extensionId,
    string registrationName,
    string path)
{
    var prefix = $"Agentstration__Extensions__{extensionId}";
    resource
        .WithEnvironment($"{prefix}__RegistrationName", registrationName)
        .WithEnvironment($"{prefix}__AuthenticationMode", "StaticBearer")
        .WithEnvironment($"{prefix}__EnrollmentMode", "SharedKeyFile")
        .WithEnvironment($"{prefix}__SharedKeyFile__Path", path);
}

sealed record DevelopmentAepExtension(
    string ExtensionId,
    string ResourceName,
    IResourceBuilder<ProjectResource> Resource);
