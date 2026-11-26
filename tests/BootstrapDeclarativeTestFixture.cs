using Agentstration.Aep.Abstractions;
using Agentstration.Agents;
using Agentstration.Extensions;
using Agentstration.Extensions.Contracts;
using Agentstration.Flows;
using Agentstration.Flows.Application;
using Agentstration.Identity.Contracts;
using Agentstration.Knowledge;
using Agentstration.Knowledge.Contracts;
using Agentstration.ModelProviders;
using Agentstration.Models;
using Agentstration.Parameters;
using Agentstration.ResourceManagement;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;
using Agentstration.Runtime.Core;
using Agentstration.Runtime.Profiles;
using Agentstration.Security.AspNetCoreIdentity;
using Agentstration.Tools;
using Agentstration.Web.Hosting;
using Agentstration.Work;
using Agentstration.Work.Storage.Abstractions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Agentstration.Management.Tests;

[TestClass]
public sealed partial class DeclarativeBootstrapTests
{
    private const string InitialPassword = "Initial123!Password";
    private const string ChangedPassword = "Changed123!Password";

    private static DeclarativeBootstrapService Service(
        string contentRoot,
        IBootstrapResourceHandler handler,
        string? path = null,
        bool enabled = true)
    {
        if (path is null)
            return ServiceForConfiguration(contentRoot, handler, null);

        var root = Directory.GetParent(path)?.FullName
            ?? throw new InvalidOperationException($"Bootstrap test path '{path}' has no parent directory.");
        var profile = Path.GetFileName(path);
        return ServiceForProfiles(root, handler, [profile], enabled);
    }

    private static DeclarativeBootstrapService ServiceForProfiles(
        string root,
        IBootstrapResourceHandler handler,
        IReadOnlyList<string> profiles,
        bool enabled = true)
    {
        foreach (var profile in profiles)
            EnsureInstanceProfileDescriptor(Path.Combine(root, profile));
        var values = new Dictionary<string, string?>
        {
            ["Agentstration:Bootstrap:Path"] = root,
            ["Agentstration:Bootstrap:InitialBootstrapEnabled"] = enabled.ToString()
        };
        for (var index = 0; index < profiles.Count; index++)
            values[$"Agentstration:Bootstrap:InitialProfiles:{index}"] = profiles[index];

        return ServiceForConfiguration(root, handler, values);
    }

    private static DeclarativeBootstrapService ServiceForConfiguration(
        string contentRoot,
        IBootstrapResourceHandler handler,
        IEnumerable<KeyValuePair<string, string?>>? values)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var environment = new TestHostEnvironment(contentRoot);
        var catalog = new BootstrapProfileCatalog(configuration, environment);
        return new(configuration, catalog, [handler], NullLogger<DeclarativeBootstrapService>.Instance);
    }

    private static WebApplicationFactory<Program> Factory(
        string path,
        string? password,
        Action<IServiceCollection>? configureServices = null,
        bool configureOllamaExtension = false)
    {
        EnsureInstanceProfileDescriptor(path);
        return new BootstrapApiOnlyWebApplicationFactory().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Agentstration:Authentication:Mode", "Local");
            builder.UseSetting("Agentstration:Bootstrap:Path", Directory.GetParent(path)!.FullName);
            builder.UseSetting("Agentstration:Bootstrap:InitialBootstrapEnabled", "true");
            builder.UseSetting("Agentstration:Bootstrap:InitialProfiles:0", Path.GetFileName(path));
            if (configureOllamaExtension)
                builder.UseSetting("Agentstration:Extensions:Agentstration.Extensions.Ollama:Endpoint", "http://127.0.0.1:1");
            if (password is not null) builder.UseSetting("Agentstration:Bootstrap:Secrets:AdminPassword", password);
            if (configureServices is not null) builder.ConfigureServices(configureServices);
        });
    }

    private static WebApplicationFactory<Program> FactoryWithExtensionPresenceHandler(string path, bool discoverOnStartup)
    {
        EnsureInstanceProfileDescriptor(path);
        return new BootstrapApiOnlyWebApplicationFactory().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Agentstration:Authentication:Mode", "Development");
            builder.UseSetting("Agentstration:Bootstrap:Path", Directory.GetParent(path)!.FullName);
            builder.UseSetting("Agentstration:Bootstrap:InitialBootstrapEnabled", "true");
            builder.UseSetting("Agentstration:Bootstrap:InitialProfiles:0", Path.GetFileName(path));
            builder.UseSetting("Agentstration:Extensions:DiscoverOnStartup", discoverOnStartup.ToString());
            builder.UseSetting("Agentstration:Extensions:Startup.Order:Endpoint", "http://localhost:5399/");
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<ExtensionPresenceBootstrapHandler>();
                services.AddSingleton<IBootstrapResourceHandler>(provider =>
                    provider.GetRequiredService<ExtensionPresenceBootstrapHandler>());
            });
        });
    }

    private static void EnsureInstanceProfileDescriptor(string profilePath)
    {
        if (!Directory.Exists(profilePath)) return;
        var descriptor = Path.Combine(profilePath, "profile.yaml");
        if (!File.Exists(descriptor)) File.WriteAllText(descriptor, Profile(Path.GetFileName(profilePath), "instance"));
    }

    private static string PlatformAdministrator() => $$"""
        apiVersion: {{ResourceApiVersions.CoreV1}}
        kind: PlatformAdministrator
        metadata:
          name: bootstrap-admin
        definition:
          displayName: Bootstrap administrator
          email: bootstrap@example.test
          passwordFrom:
            configuration: Agentstration:Bootstrap:Secrets:AdminPassword
        """;

    private static string Tenant(string name, string displayName) => $$"""
        apiVersion: {{ResourceApiVersions.CoreV1}}
        kind: Tenant
        metadata:
          name: {{name}}
        definition:
          displayName: {{displayName}}
        """;

    private static string Workspace(string name, string displayName, string tenantName) => $$"""
        apiVersion: {{ResourceApiVersions.CoreV1}}
        kind: Workspace
        metadata:
          name: {{name}}
        definition:
          displayName: {{displayName}}
          tenantRef:
            name: {{tenantName}}
        """;

    private static string DefaultContext(string localAccount, string tenantName, string workspaceName) => $$"""
        apiVersion: {{ResourceApiVersions.CoreV1}}
        kind: PrincipalDefaultContext
        metadata:
          name: {{localAccount}}
        definition:
          principalRef:
            localAccount: {{localAccount}}
          tenantRef:
            name: {{tenantName}}
          workspaceRef:
            name: {{workspaceName}}
        """;

    private static string Resource(string kind, string name) => $$"""
        apiVersion: {{ResourceApiVersions.CoreV1}}
        kind: {{kind}}
        metadata:
          name: {{name}}
        definition: {}
        """;

    private static string Profile(string name, string targetScope) => $$"""
        apiVersion: {{ResourceApiVersions.CoreV1}}
        kind: BootstrapProfile
        metadata:
          name: {{name}}
        definition:
          displayName: {{name}}
          targetScope: {{targetScope}}
        """;

    private static string ProfileWithBinding(string name, bool includeDefault = false, string defaultTargetName = "reasoning-default") => $$"""
        apiVersion: {{ResourceApiVersions.CoreV1}}
        kind: BootstrapProfile
        metadata:
          name: {{name}}
        definition:
          displayName: {{name}}
          targetScope: workspace
          bindings:
            - name: agent-model
              targetKind: modelProfile
              displayName: Agent model
              required: true
        {{(includeDefault ? $"      defaultTarget:\n        name: {defaultTargetName}\n        namespace: default" : string.Empty)}}
        """;

    private static string ModelProvider() => $$"""
        apiVersion: {{ResourceApiVersions.CoreV1}}
        kind: ModelProvider
        metadata:
          name: bootstrap-provider
        definition:
          displayName: Bootstrap provider
          extension:
            name: ollama-extension
          contributionId: ollama
        """;

    private static string RuntimeProfile() => $$"""
        apiVersion: {{ResourceApiVersions.CoreV1}}
        kind: RuntimeProfile
        metadata:
          name: bootstrap-runtime
        definition:
          displayName: Bootstrap runtime
          runtimeType: microsoft-agent-framework
          execution:
            sessionMode: transient
            toolInvocation: automatic
            streaming: automatic
        """;

    private static string ModelProfile() => $$"""
        apiVersion: {{ResourceApiVersions.CoreV1}}
        kind: ModelProfile
        metadata:
          name: bootstrap-model
        definition:
          displayName: Bootstrap model
          provider:
            name: bootstrap-provider
            scopeRef: /instance
          model:
            name: qwen3:1.7b
          generation:
            temperature: 0.2
        """;

    private static string Agent() => $$"""
        apiVersion: {{ResourceApiVersions.CoreV1}}
        kind: Agent
        metadata:
          name: bootstrap-agent
        definition:
          displayName: Bootstrap agent
          instructions: Answer concisely.
          modelProfile:
            binding: agent-model
          runtimeProfile:
            name: bootstrap-runtime
            scopeRef: /instance
          tools: []
        """;

    private static string Flow() => $$"""
        apiVersion: {{ResourceApiVersions.CoreV1}}
        kind: Flow
        metadata:
          name: bootstrap-flow
        definition:
          displayName: Bootstrap flow
          version: 1.0.0
          enabled: true
          spec:
            flowKind: direct
            target:
              kind: agent
              id: bootstrap-agent
          publish: true
          activate: true
        """;

    private static string Entry() => $$"""
        apiVersion: {{ResourceApiVersions.CoreV1}}
        kind: Entry
        metadata:
          name: bootstrap-entry
        definition:
          displayName: Bootstrap entry
          presentation:
            kind: prompt
            placeholder: Ask something
            allowAttachments: false
            allowVoiceInput: false
            fields:
              - name: request
                type: prompt
                label: Request
                required: true
                order: 0
                role: primaryInput
          binding:
            kind: flow
            resourceId: bootstrap-flow
          behavior:
            taskCreationMode: automatic
            allowConversation: true
            streamResponse: true
          publish: true
        """;

    private static string InactiveFlow() => $$"""
        apiVersion: {{ResourceApiVersions.CoreV1}}
        kind: Flow
        metadata:
          name: inactive-flow
        definition:
          displayName: Inactive flow
          version: 1.0.0
          enabled: true
          spec:
            flowKind: direct
            target:
              kind: agent
              id: bootstrap-agent
          publish: false
          activate: false
        """;

    private static string InactiveFlowEntry() => $$"""
        apiVersion: {{ResourceApiVersions.CoreV1}}
        kind: Entry
        metadata:
          name: inactive-flow-entry
        definition:
          displayName: Inactive flow entry
          presentation:
            kind: prompt
            placeholder: Ask something
            allowAttachments: false
            allowVoiceInput: false
            fields:
              - name: request
                type: prompt
                label: Request
                required: true
                order: 0
                role: primaryInput
          binding:
            kind: flow
            resourceId: inactive-flow
          behavior:
            taskCreationMode: automatic
          publish: true
        """;

    private sealed class RecordingHandler(BootstrapProfileScope scope = BootstrapProfileScope.Instance) : IBootstrapResourceHandler
    {
        public string Kind => "Recording";
        public BootstrapProfileScope Scope { get; } = scope;
        public List<string> Names { get; } = [];

        public Task<BootstrapResourcePlanResult> PlanAsync(
            BootstrapResourceDocument resource,
            BootstrapResourceOperationContext operation,
            BootstrapPlanningContext planning,
            CancellationToken cancellationToken) =>
            Task.FromResult(new BootstrapResourcePlanResult(BootstrapResourceDisposition.Create));

        public Task<BootstrapResourceApplyResult> ApplyAsync(
            BootstrapResourceDocument resource,
            BootstrapResourceOperationContext operation,
            CancellationToken cancellationToken)
        {
            Names.Add(resource.Metadata.Name);
            return Task.FromResult(BootstrapResourceApplyResult.Created);
        }
    }

    private sealed class InterruptingHandler : IBootstrapResourceHandler
    {
        public string Kind => "Interrupting";
        public BootstrapProfileScope Scope => BootstrapProfileScope.Instance;

        public Task<BootstrapResourcePlanResult> PlanAsync(
            BootstrapResourceDocument resource,
            BootstrapResourceOperationContext operation,
            BootstrapPlanningContext planning,
            CancellationToken cancellationToken) =>
            Task.FromResult(new BootstrapResourcePlanResult(BootstrapResourceDisposition.Create));

        public Task<BootstrapResourceApplyResult> ApplyAsync(
            BootstrapResourceDocument resource,
            BootstrapResourceOperationContext operation,
            CancellationToken cancellationToken) =>
            throw new OperationCanceledException(cancellationToken);
    }

    private sealed class BindingCaptureHandler : IBootstrapResourceHandler
    {
        public string Kind => "Recording";
        public BootstrapProfileScope Scope => BootstrapProfileScope.Workspace;
        public BootstrapResourceDocument Applied { get; private set; } = new();

        public Task<BootstrapResourcePlanResult> PlanAsync(
            BootstrapResourceDocument resource,
            BootstrapResourceOperationContext operation,
            BootstrapPlanningContext planning,
            CancellationToken cancellationToken) =>
            Task.FromResult(new BootstrapResourcePlanResult(BootstrapResourceDisposition.Create));

        public Task<BootstrapResourceApplyResult> ApplyAsync(
            BootstrapResourceDocument resource,
            BootstrapResourceOperationContext operation,
            CancellationToken cancellationToken)
        {
            Applied = resource;
            return Task.FromResult(BootstrapResourceApplyResult.Created);
        }
    }

    private sealed class ExtensionPresenceBootstrapHandler(ExtensionRegistrationManagementService registrations) : IBootstrapResourceHandler
    {
        public string Kind => "ExtensionPresence";
        public BootstrapProfileScope Scope => BootstrapProfileScope.Instance;
        public bool Found { get; private set; }

        public Task<BootstrapResourcePlanResult> PlanAsync(
            BootstrapResourceDocument resource,
            BootstrapResourceOperationContext operation,
            BootstrapPlanningContext planning,
            CancellationToken cancellationToken) =>
            Task.FromResult(new BootstrapResourcePlanResult(BootstrapResourceDisposition.Create));

        public async Task<BootstrapResourceApplyResult> ApplyAsync(
            BootstrapResourceDocument resource,
            BootstrapResourceOperationContext operation,
            CancellationToken cancellationToken)
        {
            Found = await registrations.GetAsync(ResourceNamespace.Default, "startup-order", cancellationToken) is not null;
            return BootstrapResourceApplyResult.Created;
        }
    }

    private sealed class BootstrapValueInspector : IExtensionInspector
    {
        private static readonly IReadOnlyList<AepValueRequirement> Requirements =
        [
            new(AepContributionKinds.ModelProvider, "ollama", "endpoint", true, AepValueType.Text)
        ];

        public bool CanHandle(string providerType) => true;
        public bool CanInspectEndpoint(Uri endpoint) => true;

        public ValueTask<ExtensionInspection> InspectAsync(
            ModelProviderConfiguration provider,
            CancellationToken cancellationToken = default) =>
            InspectAsync(provider.Name, provider.Endpoint, cancellationToken);

        public ValueTask<ExtensionInspection> InspectAsync(
            string registrationName,
            Uri endpoint,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new ExtensionInspection(
                registrationName,
                endpoint,
                "available",
                new ExtensionIdentity("ollama-extension", "Ollama extension", "1.0.0", null),
                [new ExtensionContribution(AepContributionKinds.ModelProvider, "ollama")],
                [],
                ValueRequirements: Requirements));
    }

    private sealed class TestHostEnvironment(string contentRoot) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "Agentstration.Management.Tests";
        public string ContentRootPath { get; set; } = contentRoot;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Agentstration.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("The Agentstration repository root could not be located.");
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"agentstration-bootstrap-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
