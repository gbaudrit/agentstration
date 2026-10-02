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
var foundryEnabledSetting = builder.Configuration["Foundry:Enabled"];
if (foundryEnabledSetting is not null && !bool.TryParse(foundryEnabledSetting, out _))
    throw new InvalidOperationException("Foundry:Enabled must be true or false.");
var foundryEnabled = bool.TryParse(foundryEnabledSetting, out var configuredFoundryEnabled) && configuredFoundryEnabled;
var crawl4AiEnabledSetting = builder.Configuration["Crawl4AI:Enabled"];
if (crawl4AiEnabledSetting is not null && !bool.TryParse(crawl4AiEnabledSetting, out _))
    throw new InvalidOperationException("Crawl4AI:Enabled must be true or false.");
var crawl4AiEnabled = bool.TryParse(crawl4AiEnabledSetting, out var configuredCrawl4AiEnabled) && configuredCrawl4AiEnabled;
var crawl4AiProvisioning = builder.Configuration["Crawl4AI:Provisioning"] ?? "Managed";
var crawl4AiManaged = string.Equals(crawl4AiProvisioning, "Managed", StringComparison.OrdinalIgnoreCase);
if (!crawl4AiManaged && !string.Equals(crawl4AiProvisioning, "External", StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException("Crawl4AI:Provisioning must be either 'Managed' or 'External'.");
var crawl4AiEndpoint = builder.Configuration["Crawl4AI:Endpoint"] ?? "http://localhost:11235";
if (!crawl4AiManaged && (!Uri.TryCreate(crawl4AiEndpoint, UriKind.Absolute, out var parsedCrawl4AiEndpoint)
    || (parsedCrawl4AiEndpoint.Scheme != Uri.UriSchemeHttp && parsedCrawl4AiEndpoint.Scheme != Uri.UriSchemeHttps)))
{
    throw new InvalidOperationException("Crawl4AI:Endpoint must be an absolute HTTP(S) URL when external provisioning is selected.");
}
var crawl4AiAllowedDomains = builder.Configuration.GetSection("Crawl4AI:AllowedDomains")
    .GetChildren()
    .Select(value => value.Value)
    .Where(value => !string.IsNullOrWhiteSpace(value))
    .Cast<string>()
    .ToArray();
if (crawl4AiEnabled && crawl4AiAllowedDomains.Length == 0)
    throw new InvalidOperationException("Crawl4AI:AllowedDomains must contain at least one domain when Crawl4AI is enabled.");
var crawl4AiAllowedMediaTypes = builder.Configuration.GetSection("Crawl4AI:AllowedMediaTypes")
    .GetChildren()
    .Select(value => value.Value)
    .Where(value => !string.IsNullOrWhiteSpace(value))
    .Cast<string>()
    .ToArray();
var crawl4AiAllowedPorts = builder.Configuration.GetSection("Crawl4AI:AllowedPorts")
    .GetChildren()
    .Select(value => value.Value)
    .Where(value => !string.IsNullOrWhiteSpace(value))
    .Cast<string>()
    .ToArray();

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
var developmentExtensions = new List<DevelopmentAepExtension>
{
    new DevelopmentAepExtension("Agentstration.Extensions.Ollama", "ollama-extension", ollamaExtension),
    new DevelopmentAepExtension("Agentstration.Extensions.LlamaCpp", "llama-cpp-extension", llamaCppExtension),
    new DevelopmentAepExtension("Agentstration.Extensions.LocalAI", "localai-extension", localAiExtension),
    new DevelopmentAepExtension("Agentstration.Extensions.Git", "git-source-extension", gitExtension),
    new DevelopmentAepExtension("Agentstration.Extensions.Utilities", "utilities-extension", utilitiesExtension)
};
if (foundryEnabled)
{
    var foundryExtension = builder.AddProject<Projects.Agentstration_Extensions_Foundry>("foundry-extension")
        .WithEnvironment("Agentstration__Slot", slot)
        .WithHttpHealthCheck("/health")
        .WithDynamicHostPorts(dynamicApplicationPorts);
    foreach (var key in new[]
    {
        "AllowedPrivateHosts", "MaximumDiscoveryPages", "MaximumDiscoveredModels",
        "MaximumDiscoveryResponseBytes", "RequestTimeoutSeconds"
    })
    {
        if (builder.Configuration[$"Foundry:{key}"] is { } value)
            foundryExtension.WithEnvironment($"Foundry__{key}", value);
    }
    developmentExtensions.Add(new DevelopmentAepExtension(
        "Agentstration.Extensions.Foundry", "foundry-extension", foundryExtension));
}
if (crawl4AiEnabled)
{
    var crawl4AiTokenFile = crawl4AiManaged
        ? DevelopmentTokenFiles.Provision(Path.Combine(slotDataPath, "crawl4ai"), "api-token")
        : builder.Configuration["Crawl4AI:ApiTokenFile"];
    var crawl4AiExtension = builder.AddProject<Projects.Agentstration_Extensions_Crawl4AI>("crawl4ai-extension")
        .WithEnvironment("Agentstration__Slot", slot)
        .WithEnvironment("Crawl4AI__ContentDirectory", Path.Combine(slotDataPath, "crawl4ai-content"))
        .WithHttpHealthCheck("/health/ready")
        .WithDynamicHostPorts(dynamicApplicationPorts);
    if (crawl4AiManaged)
    {
        var crawl4AiService = builder.AddContainer(
                "crawl4ai",
                builder.Configuration["Crawl4AI:Image"] ?? "unclecode/crawl4ai")
            .WithImageTag(builder.Configuration["Crawl4AI:ImageTag"] ?? "0.9.4")
            .WithHttpEndpoint(targetPort: 11235, name: "http")
            .WithHttpHealthCheck("/health")
            .WithBindMount(crawl4AiTokenFile!, "/run/secrets/api_token", isReadOnly: true)
            .WithContainerRuntimeArgs(
                "--shm-size=1g",
                "--cap-drop=ALL",
                "--security-opt=no-new-privileges",
                "--read-only",
                "--tmpfs=/tmp",
                "--tmpfs=/var/lib/redis:uid=999,gid=999,mode=0700",
                "--tmpfs=/var/lib/crawl4ai/outputs:uid=999,gid=999,mode=0700",
                "--tmpfs=/home/appuser/.crawl4ai:uid=999,gid=999,mode=0700",
                "--tmpfs=/home/appuser/.cache/url_seeder:uid=999,gid=999,mode=0700",
                "--tmpfs=/home/appuser/.gunicorn:uid=999,gid=999,mode=0700",
                "--pids-limit=512",
                "--memory=4g");
        crawl4AiExtension
            .WithEnvironment("Crawl4AI__Endpoint", crawl4AiService.GetEndpoint("http"))
            .WithEnvironment("Crawl4AI__ApiTokenFile", crawl4AiTokenFile!)
            .WaitFor(crawl4AiService);
    }
    else
    {
        crawl4AiExtension.WithEnvironment("Crawl4AI__Endpoint", crawl4AiEndpoint);
        if (!string.IsNullOrWhiteSpace(crawl4AiTokenFile))
            crawl4AiExtension.WithEnvironment("Crawl4AI__ApiTokenFile", crawl4AiTokenFile);
    }
    for (var index = 0; index < crawl4AiAllowedDomains.Length; index++)
        crawl4AiExtension.WithEnvironment($"Crawl4AI__AllowedDomains__{index}", crawl4AiAllowedDomains[index]);
    for (var index = 0; index < crawl4AiAllowedMediaTypes.Length; index++)
        crawl4AiExtension.WithEnvironment($"Crawl4AI__AllowedMediaTypes__{index}", crawl4AiAllowedMediaTypes[index]);
    for (var index = 0; index < crawl4AiAllowedPorts.Length; index++)
        crawl4AiExtension.WithEnvironment($"Crawl4AI__AllowedPorts__{index}", crawl4AiAllowedPorts[index]);
    foreach (var key in new[]
    {
        "AllowPrivateAddresses", "MaximumDepth", "MaximumPages", "RequestTimeoutSeconds",
        "MaximumResponseBytes", "MaximumContentBytes", "MaximumLinksPerPage", "MaximumReadChunkBytes",
        "ContentRetentionMinutes", "MaximumSpoolBytes"
    })
    {
        if (builder.Configuration[$"Crawl4AI:{key}"] is { } value)
            crawl4AiExtension.WithEnvironment($"Crawl4AI__{key}", value);
    }
    developmentExtensions.Add(new DevelopmentAepExtension(
        "Agentstration.Extensions.Crawl4AI", "crawl4ai-extension", crawl4AiExtension));
}
var sharedKeys = usePairingCode
    ? new Dictionary<string, string>(StringComparer.Ordinal)
    : AepDevelopmentSharedKeys.Provision(
        Path.Combine(slotDataPath, "aep-shared-keys"),
        developmentExtensions.Select(extension => extension.ResourceName).ToArray());
var bffWorkloadCredential = BffDevelopmentWorkloadCredential.Provision(
    Path.Combine(slotDataPath, "bff-workload", "console-bff"));

var console = builder.AddProject<Projects.Agentstration_Web>("agentstration-console")
    .WithEnvironment("Agentstration__Slot", slot)
    .WithEnvironment("Agentstration__InstanceId", instanceId)
    .WithEnvironment("Agentstration__SlotDataPath", slotDataPath)
    .WithEnvironment("Agentstration__Bootstrap__Path", bootstrapPath)
    .WithEnvironment(
        "Agentstration__Bootstrap__InitialBootstrapEnabled",
        initialBootstrapEnabled ? "true" : "false")
    .WithEnvironment("Data__Directory", slotDataPath)
    .WithEnvironment("Agentstration__BffWorkloadTrust__Enabled", "true")
    .WithEnvironment("Agentstration__BffWorkloadTrust__InstanceId", instanceId)
    .WithEnvironment("Agentstration__BffWorkloadTrust__Credentials__0__WorkloadId", "console-bff")
    .WithEnvironment("Agentstration__BffWorkloadTrust__Credentials__0__CredentialId", "primary")
    .WithEnvironment("Agentstration__BffWorkloadTrust__Credentials__0__SharedKeyFile", bffWorkloadCredential)
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
    .WithEnvironment("Agentstration__Aep__SecretAccess__PublicBaseUrl", console.GetEndpoint("http"))
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
