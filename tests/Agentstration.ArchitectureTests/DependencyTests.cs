using Agentstration.Application;
using Agentstration.Domain;
using Agentstration.Management.Abstractions;
using Agentstration.Evaluation;
using Agentstration.Flow;
using Agentstration.Flow.Application;
using Agentstration.Flow.Storage.Abstractions;
using Agentstration.Management.Core;
using Agentstration.Management.Contracts;
using Agentstration.Management.Storage.Sqlite;
using Agentstration.ModelProviders;
using Agentstration.ModelProviders.Ollama;
using Agentstration.Runtime.Abstractions;
using Agentstration.Runtime.AgentFramework;
using Agentstration.Runtime.Core;
using Agentstration.Runtime.Storage.Sqlite;
using Agentstration.Work;
using Agentstration.Work.Storage.Abstractions;

namespace Agentstration.ArchitectureTests;

[TestClass]
public sealed class DependencyTests
{
    [TestMethod]
    public void DomainHasNoInfrastructureOrFrameworkDependencies()
    {
        var references = typeof(Workspace).Assembly.GetReferencedAssemblies().Select(reference => reference.Name).ToArray();
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
    public void OllamaProviderDoesNotReferenceRuntimeOrAspireHosting()
    {
        var references = typeof(OllamaModelProvider).Assembly.GetReferencedAssemblies().Select(reference => reference.Name).ToArray();
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
    public void RuntimeCoreDoesNotReferenceWebWorkConcreteStorageOrAgentFramework()
    {
        var references = typeof(RuntimeRunService).Assembly.GetReferencedAssemblies().Select(reference => reference.Name).ToArray();
        Assert.IsFalse(references.Any(name => name!.Contains("Agentstration.Web", StringComparison.Ordinal)
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
            || name.Contains("Agentstration.Runtime", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void FlowStorageAbstractionsDoNotReferenceEntityFramework()
    {
        var references = typeof(IFlowRepository).Assembly.GetReferencedAssemblies().Select(reference => reference.Name).ToArray();
        Assert.IsFalse(references.Any(name => name!.Contains("EntityFramework", StringComparison.Ordinal)));
    }
}
