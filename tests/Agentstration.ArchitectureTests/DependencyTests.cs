using Agentstration.Aep.Abstractions;
using Agentstration.Aep.AspNetCore;
using Agentstration.Aep.Client;
using Agentstration.Aep.MicrosoftExtensionsAI;
using Agentstration.Application;
using Agentstration.Domain;
using Agentstration.Evaluation;
using Agentstration.Extensions.Ollama;
using Agentstration.Flow;
using Agentstration.Flow.Application;
using Agentstration.Flow.Storage.Abstractions;
using Agentstration.Management.Abstractions;
using Agentstration.Management.Contracts;
using Agentstration.Management.Core;
using Agentstration.Management.Storage.Sqlite;
using Agentstration.ModelProviders;
using Agentstration.Runtime.Abstractions;
using Agentstration.Runtime.AgentFramework;
using Agentstration.Runtime.Core;
using Agentstration.Runtime.Storage.Sqlite;
using Agentstration.Web.Console;
using Agentstration.Work;
using Agentstration.Work.Storage.Abstractions;
using Agentstration.Workplace.Client;
using Agentstration.Workplace.Components;
using Agentstration.Workplace.Web;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.ArchitectureTests;

[TestClass]
public sealed class DependencyTests
{
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
    public void DomainHasNoInfrastructureOrFrameworkDependencies()
    {
        var references = typeof(Agentstration.Domain.Workspace).Assembly.GetReferencedAssemblies().Select(reference => reference.Name).ToArray();
        Assert.IsFalse(references.Any(name => name!.Contains("EntityFramework", StringComparison.Ordinal) || name.Contains("Agents.AI", StringComparison.Ordinal) || name.Contains("Infrastructure", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void ApplicationDoesNotReferenceInfrastructureOrWeb()
    {
        var references = typeof(IPlatformStore).Assembly.GetReferencedAssemblies().Select(reference => reference.Name).ToArray();
        Assert.IsFalse(references.Any(name => name!.Contains("Infrastructure", StringComparison.Ordinal) || name.Contains("Agentstration.Web", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void EvaluationDoesNotReferenceInfrastructureOrWeb()
    {
        var references = typeof(ContentWorkflowEvaluator).Assembly.GetReferencedAssemblies().Select(reference => reference.Name).ToArray();
        Assert.IsFalse(references.Any(name => name!.Contains("Infrastructure", StringComparison.Ordinal) || name.Contains("Agentstration.Web", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void ApplicationDoesNotReferenceConcreteStorageOrRuntimeAdapters()
    {
        var references = typeof(IPlatformStore).Assembly.GetReferencedAssemblies().Select(reference => reference.Name).ToArray();
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
            || reference.Name.Contains("Ollama", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void AepMicrosoftExtensionsAiAdapterDoesNotReferenceMafOrOllama()
    {
        var references = typeof(AepChatClient).Assembly.GetReferencedAssemblies().Select(reference => reference.Name).ToArray();
        Assert.IsFalse(references.Any(name => name!.Contains("Microsoft.Agents.AI", StringComparison.Ordinal)
            || name.Contains("Ollama", StringComparison.Ordinal)));
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
    public void AgentFrameworkRuntimeDoesNotReferenceOllama()
    {
        var references = typeof(AgentFrameworkRuntimeFactory).Assembly.GetReferencedAssemblies().Select(reference => reference.Name).ToArray();
        Assert.IsFalse(references.Any(name => name!.Contains("Ollama", StringComparison.Ordinal)));
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
        var references = typeof(IControlPlaneStore).Assembly.GetReferencedAssemblies().Select(reference => reference.Name).ToArray();
        Assert.IsFalse(references.Any(name => name!.Contains("EntityFramework", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void ManagementPlaneProjectsDoNotReferenceMicrosoftAgentFramework()
    {
        var assemblies = new[]
        {
            typeof(AgentResource).Assembly,
            typeof(AgentManagementService).Assembly,
            typeof(AgentResourceRequest).Assembly,
            typeof(IControlPlaneStore).Assembly,
            typeof(SqliteControlPlaneStore).Assembly
        };

        Assert.IsFalse(assemblies.SelectMany(assembly => assembly.GetReferencedAssemblies())
            .Any(reference => reference.Name!.Contains("Microsoft.Agents.AI", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void ManagementAbstractionsDoNotReferenceCoreRuntimeStorageOrFrameworks()
    {
        var references = typeof(IControlPlaneStore).Assembly.GetReferencedAssemblies().Select(reference => reference.Name).ToArray();
        Assert.IsFalse(references.Any(name => name!.Contains("Agentstration.Management.Core", StringComparison.Ordinal)
            || name.Contains("Agentstration.Runtime", StringComparison.Ordinal)
            || name.Contains("Storage.Sqlite", StringComparison.Ordinal)
            || name.Contains("EntityFramework", StringComparison.Ordinal)
            || name.Contains("Microsoft.Agents.AI", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void ManagementCoreDoesNotReferenceWebInfrastructureConcreteStorageOrAgentFramework()
    {
        var references = typeof(AgentManagementService).Assembly.GetReferencedAssemblies().Select(reference => reference.Name).ToArray();
        Assert.IsFalse(references.Any(name => name!.Contains("Agentstration.Web", StringComparison.Ordinal)
            || name.Contains("Agentstration.Infrastructure", StringComparison.Ordinal)
            || name.Contains("Storage.Sqlite", StringComparison.Ordinal)
            || name.Contains("EntityFramework", StringComparison.Ordinal)
            || name.Contains("Microsoft.Agents.AI", StringComparison.Ordinal)
            || name.Contains("Runtime.AgentFramework", StringComparison.Ordinal)
            || name.Contains("Runtime.Local", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void NeutralLayersDoNotReferenceIdentityProviderSdksOrAspNetAuthentication()
    {
        var assemblies = new[]
        {
            typeof(Agentstration.Domain.Workspace).Assembly,
            typeof(IPlatformStore).Assembly,
            typeof(IControlPlaneStore).Assembly,
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
    public void ApiEndpointsDoNotImplementClaimOrRoleAuthorizationLogic()
    {
        var apiRoot = Path.Combine(FindRepositoryRoot(), "src", "Agentstration.Web", "Api");
        var forbidden = new[] { "User.IsInRole", "User.Claims", "User.FindFirst", "ClaimTypes." };
        var violations = Directory.EnumerateFiles(apiRoot, "*.cs", SearchOption.AllDirectories)
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
                if (string.IsNullOrWhiteSpace(include) || include.Contains("$(", StringComparison.Ordinal)) continue;

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
    public void RuntimeAbstractionsDoNotReferenceManagementAndRuntimeCoreUsesOnlyRuntimeResolver()
    {
        var references = typeof(IRuntimeAgentResolver).Assembly.GetReferencedAssemblies().Select(reference => reference.Name).ToArray();
        Assert.IsFalse(references.Any(name => name!.Contains("Agentstration.Management", StringComparison.Ordinal)));

        var dependencies = typeof(RuntimeRunService).GetConstructors()
            .SelectMany(constructor => constructor.GetParameters())
            .Select(parameter => parameter.ParameterType)
            .ToArray();
        Assert.IsTrue(dependencies.Contains(typeof(IRuntimeAgentResolver)));
        Assert.IsFalse(dependencies.Any(type => type.Name == "IControlPlaneStore"
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
