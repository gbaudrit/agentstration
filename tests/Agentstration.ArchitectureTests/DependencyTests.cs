using Agentstration.Aep.Abstractions;
using Agentstration.Aep.AspNetCore;
using Agentstration.Aep.Client;
using Agentstration.Aep.MicrosoftExtensionsAI;
using Agentstration.Agents;
using Agentstration.Agents.Contracts;
using Agentstration.Application.Work;
using Agentstration.Extensions;
using Agentstration.Extensions.Aep;
using Agentstration.Extensions.Contracts;
using Agentstration.Extensions.Git;
using Agentstration.Extensions.LlamaCpp;
using Agentstration.Extensions.LocalAI;
using Agentstration.Extensions.Ollama;
using Agentstration.Flows;
using Agentstration.Flows.Application;
using Agentstration.Flows.Storage.Abstractions;
using Agentstration.Identity;
using Agentstration.Identity.Contracts;
using Agentstration.ModelProviders;
using Agentstration.Models;
using Agentstration.Packs;
using Agentstration.Packs.Contracts;
using Agentstration.Parameters;
using Agentstration.ResourceManagement;
using Agentstration.ResourceManagement.Storage.Sqlite;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;
using Agentstration.Runtime.AgentFramework;
using Agentstration.Runtime.Core;
using Agentstration.Runtime.Profiles;
using Agentstration.Runtime.Storage.Sqlite;
using Agentstration.Secrets;
using Agentstration.Security.Contracts;
using Agentstration.Sources;
using Agentstration.Sources.Contracts;
using Agentstration.Tools;
using Agentstration.Tools.SourceRegistry;
using Agentstration.Triggers;
using Agentstration.Web.Components;
using Agentstration.Web.Console;
using Agentstration.Work;
using Agentstration.Work.Storage.Abstractions;
using Agentstration.Workplace.Client;
using Agentstration.Workplace.Components;
using Agentstration.Workplace.Web;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.ArchitectureTests;

[TestClass]
public sealed class DependencyTests
{
    [TestMethod]
    public void FoundryDependenciesStayInsideTheAutonomousExtension()
    {
        var sourceRoot = Path.Combine(FindRepositoryRoot(), "src");
        var foundryProject = Path.Combine(sourceRoot, "Agentstration.Extensions.Foundry", "Agentstration.Extensions.Foundry.csproj");
        Assert.IsTrue(File.Exists(foundryProject));
        Assert.Contains("<PackageReference Include=\"Azure.Identity\"", File.ReadAllText(foundryProject));

        var violations = Directory.EnumerateFiles(sourceRoot, "*.csproj", SearchOption.AllDirectories)
            .Where(path => !string.Equals(path, foundryProject, StringComparison.OrdinalIgnoreCase))
            .Where(path =>
            {
                var project = File.ReadAllText(path);
                return project.Contains("Agentstration.Extensions.Foundry", StringComparison.Ordinal)
                    || project.Contains("<PackageReference Include=\"Azure.Identity\"", StringComparison.Ordinal)
                    || project.Contains("<PackageReference Include=\"Azure.AI.", StringComparison.Ordinal);
            })
            .Select(path => Path.GetRelativePath(sourceRoot, path))
            .ToArray();

        Assert.IsEmpty(violations, $"Foundry dependencies must not enter product modules: {string.Join(", ", violations)}");
    }

    [TestMethod]
    public void ConsoleClientDoesNotReferenceAuthoritativeServerImplementations()
    {
        var references = typeof(IManagementApiClient).Assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .ToArray();

        var forbidden = new[]
        {
            "Agentstration.Application",
            "Agentstration.Infrastructure",
            "Agentstration.Management.Core",
            "Agentstration.ModelProviders",
            "Agentstration.Runtime.AgentFramework",
            "Agentstration.Runtime.Core",
            "Agentstration.Security.AspNetCoreIdentity",
            "Agentstration.Tools.Mcp",
            "Agentstration.Web"
        };

        Assert.IsFalse(references.Any(reference =>
            forbidden.Contains(reference, StringComparer.Ordinal)
            || reference!.Contains(".Storage.", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void OrganizationComponentsUseTheIdentityHttpClientBoundary()
    {
        var pages = Path.Combine(FindRepositoryRoot(), "src", "Agentstration.Console.Components", "Components", "Pages");
        var forbidden = new[]
        {
            "Agentstration.Management.Core",
            "Agentstration.Security.AspNetCoreIdentity",
            "ICurrentRequestContext",
            "IPlatformAuthorizationService",
            "AgentstrationWebOptions"
        };
        var violations = Directory.EnumerateFiles(pages, "Organization*.razor")
            .Where(path => forbidden.Any(value => File.ReadAllText(path).Contains(value, StringComparison.Ordinal)))
            .Select(Path.GetFileName)
            .ToArray();

        Assert.IsEmpty(violations, $"Organization components must use IIdentityAdministrationApiClient: {string.Join(", ", violations)}");
    }

    [TestMethod]
    public void ConsoleComponentsReferenceOnlyClientsAndNeutralUiBoundaries()
    {
        var references = typeof(ConsoleRouteAssembly).Assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .ToArray();
        var forbidden = new[]
        {
            "Agentstration.Application",
            "Agentstration.Infrastructure",
            "Agentstration.Management.Core",
            "Agentstration.ModelProviders",
            "Agentstration.Runtime.AgentFramework",
            "Agentstration.Runtime.Core",
            "Agentstration.Security.AspNetCoreIdentity",
            "Agentstration.Tools.Mcp",
            "Agentstration.Web"
        };

        Assert.IsFalse(references.Any(reference =>
            forbidden.Contains(reference, StringComparer.Ordinal)
            || reference!.Contains(".Storage.", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void IndependentConsoleHostReferencesOnlyConsoleAndNeutralUiProjects()
    {
        var root = Path.Combine(FindRepositoryRoot(), "src", "Agentstration.Console.Web");
        var project = File.ReadAllText(Path.Combine(root, "Agentstration.Console.Web.csproj")).Replace('\\', '/');
        var sources = string.Join(Environment.NewLine, Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Select(File.ReadAllText));

        foreach (var allowed in new[]
                 {
                     "Agentstration.Console.Client",
                     "Agentstration.Console.Components",
                     "Agentstration.Web.Components",
                     "Agentstration.Web.FlowDesigner"
                 })
            Assert.Contains($"../{allowed}/{allowed}.csproj", project, StringComparison.Ordinal);

        foreach (var forbidden in new[]
                 {
                     "Agentstration.Api/",
                     "Agentstration.Application/",
                     "Agentstration.Infrastructure/",
                     ".Storage.Sqlite/",
                     ".Storage.PostgreSql/",
                     "Agentstration.Runtime.Core/",
                     "Agentstration.Security.AspNetCoreIdentity/"
                 })
            Assert.DoesNotContain(forbidden, project, StringComparison.Ordinal);

        Assert.DoesNotContain("Agentstration.Web.Hosting", sources, StringComparison.Ordinal);
        Assert.DoesNotContain("Agentstration.Web.Api", sources, StringComparison.Ordinal);
        Assert.DoesNotContain("ICurrentRequestContext", sources, StringComparison.Ordinal);
    }

    [TestMethod]
    public void ConsoleBffWorkloadTrustIsPrivateAndCredentialTypeSpecific()
    {
        var repositoryRoot = FindRepositoryRoot();
        var endpoint = File.ReadAllText(Path.Combine(repositoryRoot, "src", "Agentstration.Identity.Api", "Api", "BffWorkloadEndpoints.cs"));
        var signer = File.ReadAllText(Path.Combine(repositoryRoot, "src", "Agentstration.Console.Web", "Security", "BffWorkloadSigningHandler.cs"));
        var registration = File.ReadAllText(Path.Combine(repositoryRoot, "src", "Agentstration.Console.Web", "Configuration", "ConsoleHostServiceCollectionExtensions.cs"));

        StringAssert.Contains(endpoint, "/api/internal/bff/");
        StringAssert.Contains(endpoint, "RequireAuthorization(AgentstrationPolicies.BffWorkload)");
        StringAssert.Contains(endpoint, "ExcludeFromDescription()");
        StringAssert.Contains(registration, "AddHttpMessageHandler<BffWorkloadSigningHandler>()");
        Assert.IsFalse(signer.Contains("PersonalAccessToken", StringComparison.Ordinal));
        Assert.IsFalse(signer.Contains("Aep", StringComparison.Ordinal));
        Assert.IsFalse(signer.Contains("Cookie", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ApiTransportDoesNotReferenceConsoleOrExecutableHostAssemblies()
    {
        var references = typeof(Agentstration.Web.ApiTransportEndpointRouteBuilderExtensions).Assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .ToArray();
        var forbidden = new[]
        {
            "Agentstration.Console.Client",
            "Agentstration.Console.Components",
            "Agentstration.Web",
            "Agentstration.Web.Components",
            "Agentstration.Web.FlowDesigner",
            "Agentstration.Workplace.Components"
        };

        Assert.IsFalse(references.Any(reference => forbidden.Contains(reference, StringComparer.Ordinal)));
    }

    [TestMethod]
    public void FamilyApiModulesOwnEndpointsHubsMcpAndSecurity()
    {
        var repositoryRoot = FindRepositoryRoot();
        var apiRoot = Path.Combine(repositoryRoot, "src", "Agentstration.Api");
        var hostRoot = Path.Combine(repositoryRoot, "src", "Agentstration.Web");
        var modules = new[] { "Agents", "Bootstrap", "Extensions", "Flows", "Identity", "Models", "Packs", "Resources", "Runtime", "Secrets", "Sources", "Tools", "Triggers", "Work", "Workplace" };

        foreach (var family in modules)
        {
            var moduleRoot = Path.Combine(repositoryRoot, "src", $"Agentstration.{family}.Api");
            Assert.IsTrue(Directory.Exists(moduleRoot), family);
            var module = File.ReadAllText(Path.Combine(moduleRoot, $"{family}ApiModule.cs"));
            Assert.Contains($"Add{family}Api", module, StringComparison.Ordinal, family);
            Assert.Contains($"Map{family}Api", module, StringComparison.Ordinal, family);
        }
        Assert.IsFalse(Directory.Exists(Path.Combine(apiRoot, "Api"))
            && Directory.EnumerateFiles(Path.Combine(apiRoot, "Api"), "*.cs", SearchOption.AllDirectories).Any());
        Assert.IsTrue(File.Exists(Path.Combine(repositoryRoot, "src", "Agentstration.Flows.Api", "Features", "FlowRunHub.cs")));
        Assert.IsTrue(File.Exists(Path.Combine(repositoryRoot, "src", "Agentstration.Workplace.Api", "Features", "WorkplaceHub.cs")));
        Assert.IsTrue(File.Exists(Path.Combine(repositoryRoot, "src", "Agentstration.Tools.Api", "Api", "AgentstrationMcpHandlers.cs")));
        Assert.IsTrue(File.Exists(Path.Combine(apiRoot, "Configuration", "OpenApiConfiguration.cs")));
        Assert.IsFalse(Directory.Exists(Path.Combine(hostRoot, "Api")));
    }

    [TestMethod]
    public void ApiModulesRejectExecutableHostsConsoleAndConcreteStorage()
    {
        var src = Path.Combine(FindRepositoryRoot(), "src");
        var forbidden = new[] { "Agentstration.Web/", "Agentstration.Console.", ".Storage.Sqlite/", ".Storage.PostgreSql/" };
        var violations = Directory.EnumerateDirectories(src, "Agentstration.*.Api")
            .SelectMany(directory => Directory.EnumerateFiles(directory, "*.csproj"))
            .Where(project => forbidden.Any(value => File.ReadAllText(project).Replace('\\', '/').Contains(value, StringComparison.Ordinal)))
            .Select(Path.GetFileNameWithoutExtension)
            .ToArray();
        Assert.IsEmpty(violations, $"Forbidden API module dependencies: {string.Join(", ", violations)}");
    }

    [TestMethod]
    public void ApiAggregatorOnlyComposesModulesAndFamilyNeutralConventions()
    {
        var root = FindRepositoryRoot();
        var aggregator = Path.Combine(root, "src", "Agentstration.Api");
        var routeComposition = File.ReadAllText(Path.Combine(aggregator, "ApiTransportEndpointRouteBuilderExtensions.cs"));
        var serviceComposition = File.ReadAllText(Path.Combine(aggregator, "Configuration", "ApiTransportServiceCollectionExtensions.cs"));
        var families = new[] { "Agents", "Bootstrap", "Extensions", "Flows", "Identity", "Models", "Packs", "Resources", "Runtime", "Secrets", "Sources", "Tools", "Triggers", "Work", "Workplace" };
        foreach (var family in families)
        {
            Assert.AreEqual(1, CountOccurrences(routeComposition, $"Map{family}Api("), family);
            Assert.AreEqual(1, CountOccurrences(serviceComposition, $"Add{family}Api("), family);
        }
        Assert.IsFalse(Directory.EnumerateFiles(aggregator, "*.cs", SearchOption.AllDirectories)
            .Any(path => path.Contains($"{Path.DirectorySeparatorChar}Api{Path.DirectorySeparatorChar}", StringComparison.Ordinal)));
        var foundation = File.ReadAllText(Path.Combine(root, "src", "ApiModuleGlobalUsings.cs"));
        Assert.DoesNotContain("Agentstration.", foundation, StringComparison.Ordinal);
    }

    [TestMethod]
    public void CrossFamilyWorkOperationsUseOneQueryProjection()
    {
        var root = FindRepositoryRoot();
        var endpoint = File.ReadAllText(Path.Combine(root, "src", "Agentstration.Workplace.Api", "Api", "WorkOperationsEndpoints.cs"));
        Assert.Contains("IWorkOperationsQueryService", endpoint, StringComparison.Ordinal);
        Assert.DoesNotContain("FlowRunService", endpoint, StringComparison.Ordinal);
        Assert.DoesNotContain("RunsForAsync", endpoint, StringComparison.Ordinal);
        Assert.IsTrue(File.Exists(Path.Combine(root, "src", "Agentstration.Triggers.Api", "Api", "TriggerConfigurationEndpoints.cs")));
        Assert.IsTrue(File.Exists(Path.Combine(root, "src", "Agentstration.Triggers.Api", "Api", "TriggerOccurrenceEndpoints.cs")));
    }

    [TestMethod]
    public void StandaloneProgramIsThinAndComposesEachTransportAndWorkerOnce()
    {
        var repositoryRoot = FindRepositoryRoot();
        var hostRoot = Path.Combine(repositoryRoot, "src", "Agentstration.Web");
        var srcRoot = Path.Combine(repositoryRoot, "src");
        var program = File.ReadAllText(Path.Combine(hostRoot, "Program.cs"));
        var composition = File.ReadAllText(Path.Combine(hostRoot, "Hosting", "StandaloneHostComposition.cs"));
        var apiTransport = string.Join(Environment.NewLine, Directory
            .EnumerateDirectories(srcRoot, "Agentstration.*.Api")
            .Append(Path.Combine(srcRoot, "Agentstration.Api"))
            .Distinct(StringComparer.Ordinal)
            .SelectMany(directory => Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Select(File.ReadAllText));

        Assert.IsLessThanOrEqualTo(15, program.Split('\n').Length);
        Assert.Contains("AddAgentstrationStandaloneHost()", program, StringComparison.Ordinal);
        Assert.Contains("ConfigureAgentstrationStandaloneHost(composition)", program, StringComparison.Ordinal);
        Assert.Contains("InitializeAgentstrationStandaloneHostAsync(composition)", program, StringComparison.Ordinal);
        Assert.DoesNotContain("AddHostedService", program, StringComparison.Ordinal);
        Assert.DoesNotContain("MapAgentstrationApi", program, StringComparison.Ordinal);
        Assert.DoesNotContain("InitializeAsync", program, StringComparison.Ordinal);

        Assert.AreEqual(1, CountOccurrences(composition, "AddAgentstrationApi("));
        Assert.AreEqual(1, CountOccurrences(composition, "AddAgentstrationWebConsole("));
        Assert.AreEqual(1, CountOccurrences(composition, "MapAgentstrationApi("));
        foreach (var worker in new[]
                 {
                     "AgentDeploymentReconciliationWorker",
                     "LocalWorkExecutionWorker",
                     "RuntimeRunExecutionWorker",
                     "FlowRunExecutionWorker",
                     "FlowRunRecoveryWorker",
                     "SourceRefreshWorker"
                 })
        {
            Assert.AreEqual(1, CountOccurrences(composition, $"AddHostedService<{worker}>"), worker);
        }

        Assert.AreEqual(1, CountOccurrences(apiTransport, "AddSignalR("));
        Assert.AreEqual(1, CountOccurrences(apiTransport, "AddMcpServer("));
        Assert.AreEqual(2, CountOccurrences(apiTransport, "MapHub<"));
        Assert.AreEqual(1, CountOccurrences(apiTransport, "MapMcp("));
    }

    [TestMethod]
    public void ConsoleComponentsDoNotUseServerImplementationNamespaces()
    {
        var components = Path.Combine(FindRepositoryRoot(), "src", "Agentstration.Console.Components");
        var forbidden = new[]
        {
            "Agentstration.Application",
            "Agentstration.Infrastructure",
            "Agentstration.Management.Core",
            "Agentstration.Runtime.Core",
            "Agentstration.Security.AspNetCoreIdentity",
            "Agentstration.Web.Api",
            "Agentstration.Web.Hosting"
        };
        var violations = Directory.EnumerateFiles(components, "*.*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".razor", StringComparison.OrdinalIgnoreCase))
            .Where(path => forbidden.Any(value => File.ReadAllText(path).Contains(value, StringComparison.Ordinal)))
            .Select(path => Path.GetRelativePath(components, path))
            .ToArray();

        Assert.IsEmpty(violations, $"Console components must consume typed clients and neutral contracts: {string.Join(", ", violations)}");
    }

    [TestMethod]
    public void ConsoleRoutesAreOwnedByTheDedicatedComponentAssembly()
    {
        var routes = typeof(ConsoleRouteAssembly).Assembly.GetTypes()
            .SelectMany(type => type.GetCustomAttributes(typeof(RouteAttribute), inherit: false)
                .Cast<RouteAttribute>()
                .Select(attribute => attribute.Template))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.IsGreaterThanOrEqualTo(80, routes.Count);
        foreach (var route in new[]
                 {
                     "/", "/agents", "/deployments", "/flows", "/tasks", "/settings",
                     "/settings/organization", "/settings/sources", "/triggers"
                 })
            Assert.Contains(route, routes);
    }

    [TestMethod]
    public void WorkplaceRealtimeClientIsScopedPerBlazorCircuit()
    {
        var services = new ServiceCollection();
        services.AddAgentstrationWorkplaceClient(new Uri("http://localhost:5100"), new Uri("http://localhost:5100/hubs/workplace"));

        var descriptor = services.Single(value => value.ServiceType == typeof(WorkplaceRealtimeClient));

        Assert.AreEqual(ServiceLifetime.Scoped, descriptor.Lifetime);
    }

    [TestMethod]
    public void WorkplacePagesDoNotAddNestedMainLandmarks()
    {
        var pages = Path.Combine(FindRepositoryRoot(), "src", "Agentstration.Workplace.Web", "Components", "Pages");
        var violations = Directory.EnumerateFiles(pages, "*.razor")
            .Where(path => File.ReadAllText(path).Contains("<main", StringComparison.OrdinalIgnoreCase))
            .Select(Path.GetFileName)
            .ToArray();

        Assert.IsEmpty(violations, $"WorkplaceLayout already owns the main landmark: {string.Join(", ", violations)}");
    }

    [TestMethod]
    public void WorkplaceComponentsUseStandaloneHostRoutes()
    {
        var components = Path.Combine(FindRepositoryRoot(), "src", "Agentstration.Workplace.Components");
        var violations = Directory.EnumerateFiles(components, "*.razor")
            .Where(path => File.ReadAllText(path).Contains("/workplace/tasks", StringComparison.OrdinalIgnoreCase))
            .Select(Path.GetFileName)
            .ToArray();

        Assert.IsEmpty(violations, $"Workplace task links must use /tasks: {string.Join(", ", violations)}");
    }

    [TestMethod]
    public void ApplicationDoesNotReferenceInfrastructureOrWeb()
    {
        var references = typeof(WorkplaceService).Assembly.GetReferencedAssemblies().Select(reference => reference.Name).ToArray();
        Assert.IsFalse(references.Any(name => name!.Contains("Infrastructure", StringComparison.Ordinal) || name.Contains("Agentstration.Web", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void ApplicationDoesNotReferenceConcreteStorageOrRuntimeAdapters()
    {
        var references = typeof(WorkplaceService).Assembly.GetReferencedAssemblies().Select(reference => reference.Name).ToArray();
        Assert.IsFalse(references.Any(name => name!.Contains("Storage.Sqlite", StringComparison.Ordinal)
            || name.Contains("Runtime.AgentFramework", StringComparison.Ordinal)
            || name.Contains("Runtime.Local", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void RuntimeAbstractionsDoNotReferenceMicrosoftAgentFramework()
    {
        var references = typeof(IRuntimeRegistry).Assembly.GetReferencedAssemblies().Select(reference => reference.Name).ToArray();
        Assert.IsFalse(references.Any(name => name!.Contains("Microsoft.Agents.AI", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void ModelProviderAbstractionsDoNotReferenceOllamaAspireOrRuntimeAdapters()
    {
        var references = typeof(IModelProvider).Assembly.GetReferencedAssemblies().Select(reference => reference.Name).ToArray();
        Assert.IsFalse(references.Any(name => name!.Contains("Ollama", StringComparison.Ordinal)
            || name.Contains("LlamaCpp", StringComparison.Ordinal)
            || name.Contains("LocalAI", StringComparison.Ordinal)
            || name.Contains("Extensions.Git", StringComparison.Ordinal)
            || name.Contains("Aspire", StringComparison.Ordinal)
            || name.Contains("Runtime.AgentFramework", StringComparison.Ordinal)
            || name.Contains("Microsoft.Agents.AI", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void AepContractsClientAndServerDoNotReferenceMafMicrosoftExtensionsAiOrOllama()
    {
        var assemblies = new[] { typeof(AepProtocol).Assembly, typeof(AepClient).Assembly, typeof(IAepModelProvider).Assembly };
        Assert.IsFalse(assemblies.SelectMany(value => value.GetReferencedAssemblies()).Any(reference =>
            reference.Name!.Contains("Microsoft.Agents.AI", StringComparison.Ordinal)
            || reference.Name.Contains("Microsoft.Extensions.AI", StringComparison.Ordinal)
            || reference.Name.Contains("Ollama", StringComparison.Ordinal)
            || reference.Name.Contains("LlamaCpp", StringComparison.Ordinal)
            || reference.Name.Contains("LocalAI", StringComparison.Ordinal)
            || reference.Name.Contains("Extensions.Git", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void AepMicrosoftExtensionsAiAdapterDoesNotReferenceMafOrOllama()
    {
        var references = typeof(AepChatClient).Assembly.GetReferencedAssemblies().Select(reference => reference.Name).ToArray();
        Assert.IsFalse(references.Any(name => name!.Contains("Microsoft.Agents.AI", StringComparison.Ordinal)
            || name.Contains("Ollama", StringComparison.Ordinal)
            || name.Contains("LlamaCpp", StringComparison.Ordinal)
            || name.Contains("LocalAI", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void OllamaExtensionDoesNotReferenceRuntimeMafOrAspireHosting()
    {
        var references = typeof(OllamaAepModelProvider).Assembly.GetReferencedAssemblies().Select(reference => reference.Name).ToArray();
        Assert.IsFalse(references.Any(name => name!.Contains("Agentstration.Runtime", StringComparison.Ordinal)
            || name.Contains("Microsoft.Agents.AI", StringComparison.Ordinal)
            || name.Contains("Aspire.Hosting.Ollama", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void LlamaCppExtensionDoesNotReferenceRuntimeMafOllamaOrAspireHosting()
    {
        var references = typeof(LlamaCppAepModelProvider).Assembly.GetReferencedAssemblies().Select(reference => reference.Name).ToArray();
        Assert.IsFalse(references.Any(name => name!.Contains("Agentstration.Runtime", StringComparison.Ordinal)
            || name.Contains("Microsoft.Agents.AI", StringComparison.Ordinal)
            || name.Contains("Ollama", StringComparison.Ordinal)
            || name.Contains("Aspire.Hosting", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void LocalAiExtensionDoesNotReferenceRuntimeMafOtherProvidersOrAspireHosting()
    {
        var references = typeof(LocalAiAepModelProvider).Assembly.GetReferencedAssemblies().Select(reference => reference.Name).ToArray();
        Assert.IsFalse(references.Any(name => name!.Contains("Agentstration.Runtime", StringComparison.Ordinal)
            || name.Contains("Microsoft.Agents.AI", StringComparison.Ordinal)
            || name.Contains("Ollama", StringComparison.Ordinal)
            || name.Contains("LlamaCpp", StringComparison.Ordinal)
            || name.Contains("Aspire.Hosting", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void GitSourceExtensionDoesNotReferenceManagementRuntimeMafOrAspireHosting()
    {
        var references = typeof(GitSourceProvider).Assembly.GetReferencedAssemblies().Select(reference => reference.Name).ToArray();
        Assert.IsFalse(references.Any(name => name!.Contains("Agentstration.Management", StringComparison.Ordinal)
            || name.Contains("Agentstration.Runtime", StringComparison.Ordinal)
            || name.Contains("Microsoft.Agents.AI", StringComparison.Ordinal)
            || name.Contains("Aspire.Hosting", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void AgentFrameworkRuntimeDoesNotReferenceConcreteModelProviders()
    {
        var references = typeof(AgentFrameworkRuntimeFactory).Assembly.GetReferencedAssemblies().Select(reference => reference.Name).ToArray();
        Assert.IsFalse(references.Any(name => name!.Contains("Ollama", StringComparison.Ordinal)
            || name.Contains("LlamaCpp", StringComparison.Ordinal)
            || name.Contains("LocalAI", StringComparison.Ordinal)
            || name.Contains("Extensions.Git", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void RuntimeCoreDoesNotReferenceManagementWebWorkConcreteStorageOrAgentFramework()
    {
        var references = typeof(RuntimeRunService).Assembly.GetReferencedAssemblies().Select(reference => reference.Name).ToArray();
        Assert.IsFalse(references.Any(name => name!.Contains("Agentstration.Management", StringComparison.Ordinal)
            || name.Contains("Agentstration.Web", StringComparison.Ordinal)
            || name.Contains("Agentstration.Work", StringComparison.Ordinal)
            || name.Contains("Storage.Sqlite", StringComparison.Ordinal)
            || name.Contains("Microsoft.Agents.AI", StringComparison.Ordinal)
            || name.Contains("Runtime.AgentFramework", StringComparison.Ordinal)
            || name.Contains("Runtime.Local", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void RuntimeSqliteStorageDoesNotReferenceWebWorkOrAgentFramework()
    {
        var references = typeof(SqliteRuntimeRunStore).Assembly.GetReferencedAssemblies().Select(reference => reference.Name).ToArray();
        Assert.IsFalse(references.Any(name => name!.Contains("Agentstration.Web", StringComparison.Ordinal)
            || name.Contains("Agentstration.Work", StringComparison.Ordinal)
            || name.Contains("Microsoft.Agents.AI", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void StorageAbstractionsDoNotReferenceEntityFramework()
    {
        var references = typeof(IResourceStore).Assembly.GetReferencedAssemblies().Select(reference => reference.Name).ToArray();
        Assert.IsFalse(references.Any(name => name!.Contains("EntityFramework", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void ResourcePrimitivesRemainIndependentFromManagementFamilies()
    {
        var references = typeof(Resource).Assembly.GetReferencedAssemblies().Select(reference => reference.Name).ToArray();

        Assert.IsFalse(references.Any(name => name!.StartsWith("Agentstration.", StringComparison.Ordinal)));
        Assert.IsFalse(references.Any(name => name!.Contains("EntityFramework", StringComparison.Ordinal)
            || name.Contains("Microsoft.Agents.AI", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void GenericResourceManagementDoesNotReferenceBusinessFamiliesOrAdapters()
    {
        var references = typeof(IResourceStore).Assembly.GetReferencedAssemblies().Select(reference => reference.Name).ToArray();
        var forbidden = new[] { "Agents", "Flows", "Models", "Tools", "Triggers", "Secrets", "Work", "Runtime", "Management", "Web", "Infrastructure", "Storage" };

        Assert.IsFalse(references.Any(name => forbidden.Any(value => name!.Contains($"Agentstration.{value}", StringComparison.Ordinal))));
        Assert.IsFalse(references.Any(name => name!.Contains("EntityFramework", StringComparison.Ordinal)
            || name.Contains("Microsoft.Agents.AI", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void ResourceFamilyModulesDoNotReferenceHostsOrConcreteStorage()
    {
        var assemblies = new[]
        {
            typeof(AgentResource).Assembly,
            typeof(TriggerResource).Assembly,
            typeof(FlowDefinition).Assembly,
            typeof(ModelProfileResource).Assembly,
            typeof(ToolResource).Assembly,
            typeof(SecretResource).Assembly
        };

        Assert.IsFalse(assemblies.SelectMany(assembly => assembly.GetReferencedAssemblies()).Any(reference =>
            reference.Name!.Contains("Agentstration.Web", StringComparison.Ordinal)
            || reference.Name.Contains("Agentstration.Infrastructure", StringComparison.Ordinal)
            || reference.Name.Contains(".Storage.", StringComparison.Ordinal)
            || reference.Name.Contains("Microsoft.Agents.AI", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void AgentFamilyDoesNotReferenceTriggersOrFlows()
    {
        var references = typeof(AgentManagementService).Assembly.GetReferencedAssemblies().Select(reference => reference.Name).ToArray();

        Assert.IsFalse(references.Any(name => name is "Agentstration.Triggers" or "Agentstration.Flows"));
    }

    [TestMethod]
    public void FlowFamilyUsesTheAcceptedPluralAssemblyIdentity()
    {
        Assert.AreEqual("Agentstration.Flows", typeof(FlowDefinition).Assembly.GetName().Name);
        Assert.AreEqual("Agentstration.Flows.Application", typeof(FlowService).Assembly.GetName().Name);
        Assert.AreEqual("Agentstration.Flows.Storage.Abstractions", typeof(IFlowRepository).Assembly.GetName().Name);
    }

    [TestMethod]
    public void ManagementStoreExposesHierarchicalScopeContracts()
    {
        var methods = typeof(IResourceStore).GetMethods().Select(method => method.Name).ToHashSet(StringComparer.Ordinal);

        CollectionAssert.IsSubsetOf(
            new[] { "GetByUidAsync", "GetExactAsync", "ListExactAsync", "ListVisibleAsync", "PutExactAsync", "DeleteExactAsync" },
            methods.ToArray());
        Assert.AreEqual(typeof(ResourceScopeRef?), typeof(Resource).GetProperty(nameof(Resource.ScopeRef))?.PropertyType);
        Assert.AreEqual(typeof(ResourceScopeRef), typeof(ScopedResourceAddress).GetProperty(nameof(ScopedResourceAddress.ScopeRef))?.PropertyType);
        Assert.IsTrue(typeof(IResourceScopeResolver).IsInterface);
    }

    [TestMethod]
    public void ManagementPlaneProjectsDoNotReferenceMicrosoftAgentFramework()
    {
        var assemblies = new[]
        {
            typeof(AgentResource).Assembly,
            typeof(AgentManagementService).Assembly,
            typeof(AgentResourceRequest).Assembly,
            typeof(IResourceStore).Assembly,
            typeof(SqliteResourceStore).Assembly
        };

        Assert.IsFalse(assemblies.SelectMany(assembly => assembly.GetReferencedAssemblies())
            .Any(reference => reference.Name!.Contains("Microsoft.Agents.AI", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void ResourceManagementDoesNotReferenceCoreRuntimeStorageOrFrameworks()
    {
        var references = typeof(IResourceStore).Assembly.GetReferencedAssemblies().Select(reference => reference.Name).ToArray();
        Assert.IsFalse(references.Any(name => name!.Contains("Agentstration.Management.Core", StringComparison.Ordinal)
            || name.Contains("Agentstration.Runtime", StringComparison.Ordinal)
            || name.Contains("Storage.Sqlite", StringComparison.Ordinal)
            || name.Contains("EntityFramework", StringComparison.Ordinal)
            || name.Contains("Microsoft.Agents.AI", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void ResourceFamilyApplicationModulesDoNotReferenceHostsConcreteStorageOrAgentFramework()
    {
        var assemblies = new[]
        {
            typeof(AgentManagementService).Assembly,
            typeof(ExtensionManagementService).Assembly,
            typeof(AepEnrollmentService).Assembly,
            typeof(ExternalIdentityAdministrationService).Assembly,
            typeof(ModelProviderManagementService).Assembly,
            typeof(PackManagementService).Assembly,
            typeof(SourceManagementService).Assembly,
            typeof(RuntimeProfileManagementService).Assembly,
            typeof(ToolManagementService).Assembly,
            typeof(TriggerManagementService).Assembly,
            typeof(SecretManagementService).Assembly
        };

        var references = assemblies.SelectMany(assembly => assembly.GetReferencedAssemblies()).Select(reference => reference.Name).ToArray();
        Assert.IsFalse(references.Any(name => name!.Contains("Agentstration.Web", StringComparison.Ordinal)
            || name.Contains("Agentstration.Infrastructure", StringComparison.Ordinal)
            || name.Contains(".Storage.", StringComparison.Ordinal)
            || name.Contains("EntityFramework", StringComparison.Ordinal)
            || name.Contains("Microsoft.Agents.AI", StringComparison.Ordinal)
            || name.Contains("Runtime.AgentFramework", StringComparison.Ordinal)
            || name.Contains("Runtime.Local", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void ManagementCoreCatchAllProjectIsRemoved()
    {
        var repositoryRoot = FindRepositoryRoot();

        Assert.IsFalse(Directory.Exists(Path.Combine(repositoryRoot, "src", "Agentstration.Management.Core")));

        var projectFiles = Directory.EnumerateFiles(repositoryRoot, "*.csproj", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase));
        var staleReferences = projectFiles
            .Where(path => File.ReadAllText(path).Contains("Agentstration.Management.Core", StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(repositoryRoot, path))
            .ToArray();

        Assert.IsEmpty(staleReferences, $"The removed catch-all project is still referenced by: {string.Join(", ", staleReferences)}");
    }

    [TestMethod]
    public void ManagementContractsCatchAllProjectIsRemoved()
    {
        var repositoryRoot = FindRepositoryRoot();

        Assert.IsFalse(Directory.Exists(Path.Combine(repositoryRoot, "src", "Agentstration.Management.Contracts")));

        var projectFiles = Directory.EnumerateFiles(repositoryRoot, "*.csproj", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase));
        var staleReferences = projectFiles
            .Where(path => File.ReadAllText(path).Contains("Agentstration.Management.Contracts", StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(repositoryRoot, path))
            .ToArray();

        Assert.IsEmpty(staleReferences, $"The removed catch-all Contracts project is still referenced by: {string.Join(", ", staleReferences)}");
    }

    [TestMethod]
    public void ManagementAbstractionsCatchAllProjectIsRemoved()
    {
        var repositoryRoot = FindRepositoryRoot();

        Assert.IsFalse(File.Exists(Path.Combine(repositoryRoot, "src", "Agentstration.Management.Abstractions", "Agentstration.Management.Abstractions.csproj")));

        var projectFiles = Directory.EnumerateFiles(repositoryRoot, "*.csproj", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase));
        var staleReferences = projectFiles
            .Where(path => File.ReadAllText(path).Contains("Agentstration.Management.Abstractions", StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(repositoryRoot, path))
            .ToArray();

        var removedSymbols = new[]
        {
            string.Concat("Agentstration.Management", ".Abstractions"),
            string.Concat("IControlPlane", "Store"),
            string.Concat("ManagementApi", "Versions")
        };
        var sourceFiles = new[] { "src", "tests" }
            .SelectMany(directory => Directory.EnumerateFiles(Path.Combine(repositoryRoot, directory), "*.cs", SearchOption.AllDirectories))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(path => !string.Equals(path, Path.Combine(repositoryRoot, "tests", "Agentstration.ArchitectureTests", "DependencyTests.cs"), StringComparison.OrdinalIgnoreCase));
        var staleUsages = sourceFiles
            .Where(path => removedSymbols.Any(symbol => File.ReadAllText(path).Contains(symbol, StringComparison.Ordinal)))
            .Select(path => Path.GetRelativePath(repositoryRoot, path))
            .ToArray();

        Assert.IsEmpty(staleReferences, $"The removed catch-all Abstractions project is still referenced by: {string.Join(", ", staleReferences)}");
        Assert.IsEmpty(staleUsages, $"Removed Management abstractions are still used by: {string.Join(", ", staleUsages)}");
    }

    [TestMethod]
    public void ResourceKindCatalogsAreOwnedByTheirFamiliesAndPreserveWireValues()
    {
        var expected = new[]
        {
            "Agent", "AgentRevision", "AgentDeployment", "Flow", "Entry", "ModelProvider", "ModelProfile", "RuntimeProfile", "Parameter",
            "Secret", "Vault", "Tool", "ToolDefinition", "ToolProvider", "ToolExecutionHook", "Trigger", "Source", "SourceVersion", "SourceProvider"
        };
        var actual = new[]
        {
            AgentResourceKinds.Agent, AgentResourceKinds.AgentRevision, AgentResourceKinds.AgentDeployment, FlowResourceKinds.Flow,
            EntryResourceKinds.Entry, ModelResourceKinds.ModelProvider, ModelResourceKinds.ModelProfile, RuntimeProfileResourceKinds.RuntimeProfile, ParameterResourceKinds.Parameter,
            SecretResourceKinds.Secret, SecretResourceKinds.Vault, ToolResourceKinds.Tool, ToolResourceKinds.ToolDefinition,
            ToolResourceKinds.ToolProvider, ToolResourceKinds.ToolExecutionHook, TriggerResourceKinds.Trigger, SourceResourceKinds.Source,
            SourceResourceKinds.SourceVersion, SourceResourceKinds.SourceProvider
        };

        CollectionAssert.AreEqual(expected, actual);
    }

    [TestMethod]
    public void ResourceFamilyContractModulesRemainPortable()
    {
        var assemblies = new[]
        {
            typeof(Agentstration.Api.Contracts.PagedResponse<>).Assembly,
            typeof(Agentstration.Agents.Contracts.AgentResourceRequest).Assembly,
            typeof(Agentstration.Bootstrap.Contracts.BootstrapApplicationResource).Assembly,
            typeof(Agentstration.Extensions.Contracts.ExtensionResponse).Assembly,
            typeof(Agentstration.Identity.Contracts.IdentityConsoleContextResponse).Assembly,
            typeof(Agentstration.Models.Contracts.ModelProviderResponse).Assembly,
            typeof(Agentstration.Parameters.Contracts.CreateParameterRequest).Assembly,
            typeof(Agentstration.ResourceManagement.Contracts.ResourceDeclaration<>).Assembly,
            typeof(Agentstration.Runtime.Contracts.RuntimeProfileSummaryResponse).Assembly,
            typeof(Agentstration.Secrets.Contracts.SecretResponse).Assembly,
            typeof(Agentstration.Sources.Contracts.SourceConsoleDetailView).Assembly,
            typeof(Agentstration.Tools.Contracts.CreateToolDefinitionRequest).Assembly,
            typeof(Agentstration.Triggers.Contracts.TriggerSchedulePreviewRequest).Assembly
        };
        var forbidden = new[]
        {
            "Agentstration.Management.Contracts",
            "Agentstration.Infrastructure",
            "Agentstration.Web",
            ".Storage.",
            "EntityFramework",
            "Microsoft.Agents.AI",
            "YamlDotNet"
        };

        var references = assemblies
            .SelectMany(assembly => assembly.GetReferencedAssemblies())
            .Select(reference => reference.Name)
            .ToArray();

        Assert.IsFalse(references.Any(reference =>
            forbidden.Any(value => reference!.Contains(value, StringComparison.Ordinal))));
    }

    [TestMethod]
    public void IdentityAndSecurityContractsAreOwnedByTheirFamilies()
    {
        Assert.AreEqual("Agentstration.Identity.Contracts", typeof(Principal).Namespace);
        Assert.AreEqual("Agentstration.Security.Contracts", typeof(SecurityAuditEvent).Namespace);
    }

    [TestMethod]
    public void IdentityAndSecurityContractModulesRemainPortable()
    {
        var forbidden = new[]
        {
            "Agentstration.Management.Abstractions",
            "Agentstration.Infrastructure",
            "Agentstration.Web",
            ".Storage.",
            "EntityFramework",
            "Microsoft.AspNetCore.Identity",
            "Microsoft.Agents.AI"
        };
        var references = new[]
            {
                typeof(Principal).Assembly,
                typeof(SecurityAuditEvent).Assembly
            }
            .SelectMany(assembly => assembly.GetReferencedAssemblies())
            .Select(reference => reference.Name)
            .ToArray();

        Assert.IsFalse(references.Any(reference =>
            forbidden.Any(value => reference!.Contains(value, StringComparison.Ordinal))));
    }

    [TestMethod]
    public void CoreSourceContractsAreOwnedBySources()
    {
        var sourceContractsAssembly = typeof(Agentstration.Sources.Contracts.SourceResource).Assembly;
        var forbiddenNames = new HashSet<string>(StringComparer.Ordinal)
        {
            nameof(Agentstration.Sources.Contracts.SourceResource),
            nameof(Agentstration.Sources.Contracts.SourceVersionResource),
            nameof(Agentstration.Sources.Contracts.SourceProviderResource),
            nameof(Agentstration.Sources.Contracts.SourceBindingStatusView),
            nameof(Agentstration.Sources.Contracts.SourceRefreshSchedule),
            nameof(Agentstration.Sources.Contracts.SourceSemanticVersion),
            nameof(Agentstration.Sources.Contracts.ISourceProviderMaterializer),
            nameof(Agentstration.Sources.Contracts.ISourceManifestRetriever)
        };

        Assert.AreEqual("Agentstration.Sources.Contracts", sourceContractsAssembly.GetName().Name);
        Assert.IsTrue(forbiddenNames.All(name => sourceContractsAssembly.GetTypes().Any(type => type.Name == name)));
    }

    [TestMethod]
    public void SourceRegistryContractsAreOwnedBySources()
    {
        var sourceContractsAssembly = typeof(SourceRegistryRegistrationResource).Assembly;

        Assert.AreEqual("Agentstration.Sources.Contracts", sourceContractsAssembly.GetName().Name);
        Assert.AreEqual("Agentstration.Sources.Contracts", typeof(SourceRegistryRegistrationResource).Namespace);
        Assert.AreEqual("Agentstration.Sources.Contracts", typeof(ISourceRegistryDocumentRetriever).Namespace);
        Assert.AreEqual("Agentstration.Sources.Contracts", typeof(SourceRegistryImportProvenance).Namespace);
    }

    [TestMethod]
    public void PackContractsAreOwnedByPacks()
    {
        var packContractsAssembly = typeof(PackManifest).Assembly;

        Assert.AreEqual("Agentstration.Packs.Contracts", packContractsAssembly.GetName().Name);
        Assert.AreEqual("Agentstration.Packs.Contracts", typeof(PackManifest).Namespace);
        Assert.AreEqual("Agentstration.Packs.Contracts", typeof(PackProjectResource).Namespace);
        Assert.AreEqual("Agentstration.Packs.Contracts", typeof(SourcePackInstallationPreview).Namespace);
        Assert.AreEqual("Agentstration.Packs.Contracts", typeof(PackCatalogManifest).Namespace);
    }

    [TestMethod]
    public void PacksConsumeSourcesWithoutAReverseDependency()
    {
        var sourceAssemblies = new[]
        {
            typeof(SourceResource).Assembly,
            typeof(SourceManagementService).Assembly
        };
        var forbiddenReferences = sourceAssemblies
            .SelectMany(assembly => assembly.GetReferencedAssemblies()
                .Where(reference => reference.Name!.StartsWith("Agentstration.Packs", StringComparison.Ordinal))
                .Select(reference => $"{assembly.GetName().Name} -> {reference.Name}"))
            .ToArray();

        Assert.IsEmpty(forbiddenReferences,
            $"Sources must remain a generic distribution boundary: {string.Join(", ", forbiddenReferences)}");
        Assert.AreEqual("Agentstration.Packs", typeof(PackSourceInstallationService).Namespace);
        Assert.AreEqual("Agentstration.Packs", typeof(PackSourceCatalogHandler).Namespace);
        Assert.Contains("Agentstration.Sources.Contracts",
            typeof(PackSourceInstallationService).Assembly.GetReferencedAssemblies().Select(reference => reference.Name));
    }

    [TestMethod]
    public void PackContractsRemainPortable()
    {
        var forbidden = new[]
        {
            "Agentstration.Infrastructure",
            "Agentstration.Web",
            ".Storage.",
            "EntityFramework",
            "Microsoft.Agents.AI",
            "YamlDotNet",
            "SharpCompress"
        };
        var references = typeof(PackManifest).Assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .ToArray();

        Assert.IsFalse(references.Any(reference =>
            forbidden.Any(value => reference!.Contains(value, StringComparison.Ordinal))));
        Assert.DoesNotContain("Agentstration.Sources", references);
    }

    [TestMethod]
    public void ExtensionAndAepContractsAreOwnedByTheirFamilies()
    {
        Assert.AreEqual("Agentstration.Extensions.Contracts", typeof(ExtensionRegistrationResource).Namespace);
        Assert.AreEqual("Agentstration.Extensions.Contracts", typeof(AepEnrollmentRequestResource).Namespace);
        Assert.AreEqual("Agentstration.Agents.Contracts", typeof(ExternalBinding).Namespace);
    }

    [TestMethod]
    public void ExtensionContractsRemainPortable()
    {
        var forbidden = new[]
        {
            "Agentstration.Management.Abstractions",
            "Agentstration.Infrastructure",
            "Agentstration.Web",
            ".Storage.",
            "EntityFramework",
            "Microsoft.Agents.AI",
            "YamlDotNet"
        };
        var references = typeof(ExtensionRegistrationResource).Assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .ToArray();

        Assert.IsFalse(references.Any(reference =>
            forbidden.Any(value => reference!.Contains(value, StringComparison.Ordinal))));
    }

    [TestMethod]
    public void BootstrapContractsAreOwnedByExplicitModules()
    {
        Assert.AreEqual("Agentstration.ResourceManagement.Contracts", typeof(Agentstration.ResourceManagement.Contracts.BootstrapResourceDocument).Namespace);
        Assert.AreEqual("Agentstration.ResourceManagement.Contracts", typeof(Agentstration.ResourceManagement.Contracts.IBootstrapResourceHandler).Namespace);
        Assert.AreEqual("Agentstration.Sources.Contracts", typeof(Agentstration.Sources.Contracts.BootstrapSourceProfileSelection).Namespace);
        Assert.AreEqual("Agentstration.Sources.Contracts", typeof(Agentstration.Sources.Contracts.BootstrapSourceProvenance).Namespace);
        Assert.AreEqual("Agentstration.Bootstrap.Contracts", typeof(Agentstration.Bootstrap.Contracts.BootstrapApplicationResource).Namespace);
    }

    [TestMethod]
    public void BootstrapContractModulesRemainPortable()
    {
        var forbidden = new[]
        {
            "Agentstration.Management.Abstractions",
            "Agentstration.Infrastructure",
            "Agentstration.Web",
            ".Storage.",
            "EntityFramework",
            "Microsoft.Agents.AI",
            "YamlDotNet"
        };
        var references = new[]
            {
                typeof(Agentstration.ResourceManagement.Contracts.BootstrapResourceDocument).Assembly,
                typeof(Agentstration.Bootstrap.Contracts.BootstrapApplicationResource).Assembly
            }
            .SelectMany(assembly => assembly.GetReferencedAssemblies())
            .Select(reference => reference.Name)
            .ToArray();

        Assert.IsFalse(references.Any(reference =>
            forbidden.Any(value => reference!.Contains(value, StringComparison.Ordinal))));
    }

    [TestMethod]
    public void SourceContractsRemainProviderAndSerializationNeutral()
    {
        var forbidden = new[]
        {
            "Agentstration.Management.Abstractions",
            "Agentstration.Infrastructure",
            "Agentstration.Web",
            ".Storage.",
            "EntityFramework",
            "Agentstration.Aep",
            "Microsoft.Agents.AI",
            "YamlDotNet"
        };
        var references = typeof(Agentstration.Sources.Contracts.SourceResource).Assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .ToArray();

        Assert.IsFalse(references.Any(reference =>
            forbidden.Any(value => reference!.Contains(value, StringComparison.Ordinal))));
    }

    [TestMethod]
    public void SourceRegistryToolDependsOnSourcesWithoutHostAdapters()
    {
        var references = typeof(SourceRegistryCli).Assembly.GetReferencedAssemblies().Select(reference => reference.Name).ToArray();

        Assert.Contains("Agentstration.Sources", references);
        Assert.Contains("Agentstration.Sources.Contracts", references);
        Assert.IsFalse(references.Any(name => name!.Contains("Agentstration.Management.Core", StringComparison.Ordinal)
            || name.Contains("Agentstration.Management.Contracts", StringComparison.Ordinal)
            || name.Contains("Agentstration.Management.Abstractions", StringComparison.Ordinal)
            || name.Contains("Agentstration.Infrastructure", StringComparison.Ordinal)
            || name.Contains("Agentstration.Web", StringComparison.Ordinal)
            || name.Contains("Storage.Sqlite", StringComparison.Ordinal)
            || name.Contains("Microsoft.Agents.AI", StringComparison.Ordinal)));
        Assert.AreSame(typeof(SourceManifestReader).Assembly, typeof(SourceManifestValidator).Assembly);
    }

    [TestMethod]
    public void NeutralLayersDoNotReferenceIdentityProviderSdksOrAspNetAuthentication()
    {
        var assemblies = new[]
        {
            typeof(Agentstration.Resources.ResourceAddress).Assembly,
            typeof(WorkplaceService).Assembly,
            typeof(IResourceStore).Assembly,
            typeof(AgentManagementService).Assembly
        };
        var forbidden = new[] { "Azure.Identity", "Microsoft.Identity", "Keycloak", "Zitadel", "Auth0", "WorkOS", "OpenIddict", "Microsoft.AspNetCore.Authentication", "Microsoft.AspNetCore.Identity" };

        Assert.IsFalse(assemblies.SelectMany(value => value.GetReferencedAssemblies())
            .Any(reference => forbidden.Any(value => reference.Name!.Contains(value, StringComparison.OrdinalIgnoreCase))));
    }

    [TestMethod]
    public void PrincipalContainsNoCredentialMaterial()
    {
        var forbidden = new[] { "password", "hash", "salt", "token", "secret", "credential", "recovery", "mfa" };
        Assert.IsFalse(typeof(Principal).GetProperties().Any(property =>
            forbidden.Any(value => property.Name.Contains(value, StringComparison.OrdinalIgnoreCase))));
    }

    [TestMethod]
    public void SecurityAuditEventsContainNoCredentialOrMutablePersonalAttributes()
    {
        var forbidden = new[] { "password", "hash", "salt", "token", "secret", "credential", "recovery", "mfa", "email", "username", "claim" };
        Assert.IsFalse(typeof(SecurityAuditEvent).GetProperties().Any(property =>
            forbidden.Any(value => property.Name.Contains(value, StringComparison.OrdinalIgnoreCase))));
    }

    [TestMethod]
    public void ApiEndpointsDoNotImplementClaimOrRoleAuthorizationLogic()
    {
        var srcRoot = Path.Combine(FindRepositoryRoot(), "src");
        var forbidden = new[] { "User.IsInRole", "User.Claims", "User.FindFirst", "ClaimTypes." };
        var violations = Directory.EnumerateDirectories(srcRoot, "Agentstration.*.Api")
            .SelectMany(directory => Directory.Exists(Path.Combine(directory, "Api"))
                ? Directory.EnumerateFiles(Path.Combine(directory, "Api"), "*.cs", SearchOption.AllDirectories)
                : [])
            .Where(path => forbidden.Any(value => File.ReadAllText(path).Contains(value, StringComparison.Ordinal)))
            .Select(Path.GetFileName)
            .ToArray();
        Assert.IsEmpty(violations, $"Endpoint authorization must remain policy based: {string.Join(", ", violations)}");
    }

    [TestMethod]
    public void WorkPlaneCoreDoesNotReferenceInfrastructureRuntimeOrFrameworks()
    {
        var references = typeof(WorkItem).Assembly.GetReferencedAssemblies().Select(reference => reference.Name).ToArray();
        Assert.IsFalse(references.Any(name => name!.Contains("EntityFramework", StringComparison.Ordinal)
            || name.Contains("Microsoft.Agents.AI", StringComparison.Ordinal)
            || name.Contains("Agentstration.Runtime", StringComparison.Ordinal)
            || name.Contains("Agentstration.Infrastructure", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void WorkStorageAbstractionsDoNotReferenceEntityFramework()
    {
        var references = typeof(IWorkItemRepository).Assembly.GetReferencedAssemblies().Select(reference => reference.Name).ToArray();
        Assert.IsFalse(references.Any(name => name!.Contains("EntityFramework", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void WorkplaceClientAndComponentsDoNotReferenceProvidersAzureRuntimeOrStorage()
    {
        var assemblies = new[] { typeof(IWorkplaceApiClient).Assembly, typeof(EntryRenderer).Assembly };
        Assert.IsFalse(assemblies.SelectMany(assembly => assembly.GetReferencedAssemblies()).Any(reference =>
            reference.Name!.Contains("Azure", StringComparison.Ordinal)
            || reference.Name.Contains("Ollama", StringComparison.Ordinal)
            || reference.Name.Contains("LlamaCpp", StringComparison.Ordinal)
            || reference.Name.Contains("LocalAI", StringComparison.Ordinal)
            || reference.Name.Contains("Microsoft.Agents.AI", StringComparison.Ordinal)
            || reference.Name.Contains("Agentstration.Runtime", StringComparison.Ordinal)
            || reference.Name.Contains("Storage.Sqlite", StringComparison.Ordinal)
            || reference.Name.Contains("EntityFramework", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void PublishedEntryProjectionContainsOnlyAFlowTargetAndWorkplaceServiceHasNoAgentDependency()
    {
        Assert.IsNull(typeof(EntryResource).GetProperty("Binding"));
        Assert.IsNotNull(typeof(EntryResource).GetProperty(nameof(EntryResource.ResolvedTarget)));
        Assert.AreEqual(typeof(EntryResolvedTarget), typeof(EntryResource).GetProperty(nameof(EntryResource.ResolvedTarget))!.PropertyType);
        var constructorDependencies = typeof(Agentstration.Application.Work.WorkplaceService).GetConstructors()
            .SelectMany(value => value.GetParameters()).Select(value => value.ParameterType.FullName).ToArray();
        Assert.IsFalse(constructorDependencies.Any(value => value?.Contains("AgentManagement", StringComparison.Ordinal) == true
            || value?.Contains("Runtime", StringComparison.Ordinal) == true));
    }

    [TestMethod]
    public void WorkplaceWebReferencesOnlyClientContractsAndNeutralComponents()
    {
        var references = typeof(WorkplaceWebAssemblyMarker).Assembly.GetReferencedAssemblies().Select(reference => reference.Name).ToArray();
        Assert.IsFalse(references.Any(name => name!.Equals("Agentstration.Web", StringComparison.Ordinal)
            || name.Contains("Agentstration.Application", StringComparison.Ordinal)
            || name.Contains("Agentstration.Infrastructure", StringComparison.Ordinal)
            || name.Contains("Storage.Sqlite", StringComparison.Ordinal)
            || name.Contains("Agentstration.Runtime", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void ProjectsDoNotCompileSourcesOwnedByOtherProjects()
    {
        var repositoryRoot = FindRepositoryRoot();
        var violations = new List<string>();

        foreach (var projectPath in Directory.EnumerateFiles(repositoryRoot, "*.csproj", SearchOption.AllDirectories)
                     .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                         && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)))
        {
            var projectDirectory = Path.GetDirectoryName(projectPath)!;
            var document = System.Xml.Linq.XDocument.Load(projectPath);
            foreach (var compile in document.Descendants().Where(element => element.Name.LocalName == "Compile"))
            {
                var include = compile.Attribute("Include")?.Value;
                if (string.IsNullOrWhiteSpace(include) || include.Contains("$(", StringComparison.Ordinal)
                    || string.Equals(include.Replace('\\', '/'), "../ApiModuleGlobalUsings.cs", StringComparison.Ordinal)) continue;

                var normalized = include.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
                var sourcePath = Path.GetFullPath(Path.Combine(projectDirectory, normalized));
                var relativePath = Path.GetRelativePath(projectDirectory, sourcePath);
                if (relativePath == ".." || relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                {
                    violations.Add($"{Path.GetRelativePath(repositoryRoot, projectPath)} -> {include}");
                }
            }
        }

        Assert.IsEmpty(violations, $"Projects must not compile source files owned by another project:{Environment.NewLine}{string.Join(Environment.NewLine, violations)}");
    }

    [TestMethod]
    public void TestProjectsReflectApiConsoleAndStandaloneHostOwnership()
    {
        var testsRoot = Path.Combine(FindRepositoryRoot(), "tests");
        foreach (var project in new[]
                 {
                     "Agentstration.Api.Tests",
                     "Agentstration.Console.Client.Tests",
                     "Agentstration.Console.Components.Tests",
                     "Agentstration.Console.Web.Tests",
                     "Agentstration.Web.Tests"
                 })
        {
            Assert.IsTrue(File.Exists(Path.Combine(testsRoot, project, $"{project}.csproj")), $"Missing {project}.");
        }

        Assert.IsFalse(File.Exists(Path.Combine(testsRoot, "Agentstration.Work.Api.Tests", "Agentstration.Work.Api.Tests.csproj")));
    }

    [TestMethod]
    public void BusinessTestProjectsDoNotReferenceTheApiOrExecutableHost()
    {
        var testsRoot = Path.Combine(FindRepositoryRoot(), "tests");
        foreach (var project in new[]
                 {
                     "Agentstration.Application.Tests",
                     "Agentstration.Runtime.Tests",
                     "Agentstration.Tools.Tests",
                     "Agentstration.Management.Sources.Tests",
                     "Agentstration.Management.Storage.Tests"
                 })
        {
            var contents = File.ReadAllText(Path.Combine(testsRoot, project, $"{project}.csproj"));
            Assert.DoesNotContain("src/Agentstration.Api/", contents, StringComparison.Ordinal, project);
            Assert.DoesNotContain("src/Agentstration.Web/", contents, StringComparison.Ordinal, project);
            Assert.DoesNotContain("Microsoft.AspNetCore.Mvc.Testing", contents, StringComparison.Ordinal, project);
        }
    }

    [TestMethod]
    public void ConsoleTestProjectsDoNotDependOnTheAuthoritativeServer()
    {
        var testsRoot = Path.Combine(FindRepositoryRoot(), "tests");
        var clientRoot = Path.Combine(testsRoot, "Agentstration.Console.Client.Tests");
        var componentRoot = Path.Combine(testsRoot, "Agentstration.Console.Components.Tests");

        foreach (var projectRoot in new[] { clientRoot, componentRoot })
        {
            var project = File.ReadAllText(Directory.EnumerateFiles(projectRoot, "*.csproj").Single());
            var sources = string.Join(Environment.NewLine, Directory.EnumerateFiles(projectRoot, "*.cs").Select(File.ReadAllText));
            Assert.DoesNotContain("src/Agentstration.Api/", project, StringComparison.Ordinal);
            Assert.DoesNotContain("src/Agentstration.Web/", project, StringComparison.Ordinal);
            Assert.DoesNotContain("WebApplicationFactory", sources, StringComparison.Ordinal);
            Assert.DoesNotContain("Agentstration.Web.Api", sources, StringComparison.Ordinal);
            Assert.DoesNotContain("Agentstration.Web.Hosting", sources, StringComparison.Ordinal);
            Assert.DoesNotContain("Agentstration.Web.Security", sources, StringComparison.Ordinal);
        }
    }

    [TestMethod]
    public void StandaloneHostTestsDoNotOwnClientOrComponentTestInfrastructure()
    {
        var project = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "tests", "Agentstration.Web.Tests", "Agentstration.Web.Tests.csproj"));

        Assert.DoesNotContain(
            "<ProjectReference Include=\"../../src/Agentstration.Console.Components/",
            project,
            StringComparison.Ordinal);
        Assert.DoesNotContain("bunit", project, StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
    public void RuntimeAbstractionsDoNotReferenceManagementAndRuntimeCoreUsesOnlyRuntimeResolver()
    {
        var references = typeof(IRuntimeAgentResolver).Assembly.GetReferencedAssemblies().Select(reference => reference.Name).ToArray();
        Assert.IsFalse(references.Any(name => name!.Contains("Agentstration.Management", StringComparison.Ordinal)));

        var dependencies = typeof(RuntimeRunService).GetConstructors()
            .SelectMany(constructor => constructor.GetParameters())
            .Select(parameter => parameter.ParameterType)
            .ToArray();
        Assert.IsTrue(dependencies.Contains(typeof(IRuntimeAgentResolver)));
        Assert.IsFalse(dependencies.Any(type => type.Name == "IResourceStore"
            || type.Name is "AgentResource" or "AgentRevision" or "AgentDeployment"));
    }

    [TestMethod]
    public void AspireDoesNotOverrideTheManagedAiProvider()
    {
        var repositoryRoot = FindRepositoryRoot();
        var appHost = File.ReadAllText(Path.Combine(repositoryRoot, "src", "Agentstration.AppHost", "Program.cs"));
        Assert.DoesNotContain("AI__Provider", appHost, StringComparison.Ordinal);

        using var settings = System.Text.Json.JsonDocument.Parse(File.ReadAllText(
            Path.Combine(repositoryRoot, "src", "Agentstration.Web", "appsettings.json")));
        Assert.AreEqual("Managed", settings.RootElement.GetProperty("AI").GetProperty("Provider").GetString());
    }

    [TestMethod]
    public void AspirePostgreSqlStorageUsesThePersistentWorktreeIdentity()
    {
        var repositoryRoot = FindRepositoryRoot();
        var appHostDirectory = Path.Combine(repositoryRoot, "src", "Agentstration.AppHost");
        var program = File.ReadAllText(Path.Combine(appHostDirectory, "Program.cs"));
        var storage = File.ReadAllText(Path.Combine(appHostDirectory, "StorageResourceExtensions.cs"));
        var identity = File.ReadAllText(Path.Combine(appHostDirectory, "DevelopmentInstanceIdentity.cs"));

        Assert.Contains("DevelopmentInstanceIdentity.Resolve", program, StringComparison.Ordinal);
        Assert.Contains("Agentstration:InstanceId", program, StringComparison.Ordinal);
        Assert.Contains("agentstration-{slot}-{instanceId}-postgresql", storage, StringComparison.Ordinal);
        Assert.Contains("postgres-password-{instanceId}", storage, StringComparison.Ordinal);
        Assert.Contains("Path.Combine(worktreeRoot, \".agentstration\")", identity, StringComparison.Ordinal);
        Assert.Contains("File.Move(temporaryPath, identityPath)", identity, StringComparison.Ordinal);
    }

    [TestMethod]
    public void ProductionSourcesDoNotReintroduceHierarchicalResourcePaths()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "src");
        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".cs", ".razor", ".json" };
        var forbidden = new[] { "resource" + "Groups", "resource" + "Group" };
        var violations = Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories)
            .Where(path => extensions.Contains(Path.GetExtension(path))
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(path => forbidden.Any(value => File.ReadAllText(path).Contains(value, StringComparison.OrdinalIgnoreCase)))
            .Select(path => Path.GetRelativePath(repositoryRoot, path))
            .ToArray();

        Assert.IsEmpty(violations, $"Production sources must use canonical resource names:{Environment.NewLine}{string.Join(Environment.NewLine, violations)}");
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Agentstration.slnx"))) return directory.FullName;
        }

        throw new InvalidOperationException("Could not locate the Agentstration repository root.");
    }

    private static int CountOccurrences(string source, string value) =>
        source.Split(value, StringSplitOptions.None).Length - 1;

    [TestMethod]
    public void ConsoleWorkOperationsClientDependsOnlyOnHttpAndPublicContracts()
    {
        var type = typeof(WorkApiClient);
        Assert.IsTrue(type.GetConstructors().SelectMany(value => value.GetParameters()).All(value => value.ParameterType == typeof(HttpClient)));
        Assert.IsFalse(type.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            .Any(value => value.FieldType.FullName?.Contains("Repository", StringComparison.Ordinal) == true
                || value.FieldType.Namespace?.Contains("Storage", StringComparison.Ordinal) == true
                || value.FieldType.FullName?.Contains("WorkplaceService", StringComparison.Ordinal) == true));
    }

    [TestMethod]
    public void FlowCoreDoesNotReferenceInfrastructureRuntimeOrAgentFramework()
    {
        var references = typeof(FlowDefinition).Assembly.GetReferencedAssemblies().Select(reference => reference.Name).ToArray();
        Assert.IsFalse(references.Any(name => name!.Contains("EntityFramework", StringComparison.Ordinal)
            || name.Contains("Microsoft.Agents.AI", StringComparison.Ordinal)
            || name.Contains("Agentstration.Runtime", StringComparison.Ordinal)
            || name.Contains("Agentstration.Infrastructure", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void FlowApplicationOnlyReferencesCoreAndStorageAbstractions()
    {
        var references = typeof(FlowService).Assembly.GetReferencedAssemblies().Select(reference => reference.Name).ToArray();
        Assert.IsFalse(references.Any(name => name!.Contains("Storage.Sqlite", StringComparison.Ordinal)
            || name.Contains("Agentstration.Web", StringComparison.Ordinal)
            || name.Contains("Agentstration.Runtime", StringComparison.Ordinal)
            || name.Contains("Microsoft.Agents.AI", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void FlowStorageAbstractionsDoNotReferenceEntityFramework()
    {
        var references = typeof(IFlowRepository).Assembly.GetReferencedAssemblies().Select(reference => reference.Name).ToArray();
        Assert.IsFalse(references.Any(name => name!.Contains("EntityFramework", StringComparison.Ordinal)));
    }
}
