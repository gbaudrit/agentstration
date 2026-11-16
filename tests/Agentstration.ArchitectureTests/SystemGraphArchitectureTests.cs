using System.Reflection;
using Agentstration.SystemGraph;
using Agentstration.SystemGraph.Contracts;

namespace Agentstration.ArchitectureTests;

[TestClass]
public sealed class SystemGraphArchitectureTests
{
    [TestMethod]
    public void SemanticContractsAndQueryHaveNoPresentationOrExecutionDependencies()
    {
        foreach (var assembly in new[] { typeof(SystemGraphPage).Assembly, typeof(SystemGraphQueryService).Assembly })
        {
            var forbiddenAssemblies = new[] { "EntityFramework", "AspNetCore", "LikeC4", "React", "Agentstration.Flows", "Agentstration.Runtime", "Agentstration.Tools", "Agentstration.Web" };
            foreach (var dependency in assembly.GetReferencedAssemblies())
                Assert.IsFalse(forbiddenAssemblies.Any(name => dependency.Name!.Contains(name, StringComparison.OrdinalIgnoreCase)), dependency.Name);
            var forbiddenProperties = new[] { "X", "Y", "Layout", "Viewport", "Style", "Position", "Renderer", "VisualGroup", "Diagram", "LikeC4" };
            foreach (var type in assembly.GetExportedTypes())
                foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                    Assert.IsFalse(forbiddenProperties.Contains(property.Name, StringComparer.OrdinalIgnoreCase), $"{type.Name}.{property.Name}");
        }
    }

    [TestMethod]
    public void PublicQuerySurfaceCannotReturnTheContributorIndexOrInvokeLifecycleCommands()
    {
        var methods = typeof(ISystemGraphQueryService).GetMethods();
        Assert.HasCount(1, methods);
        Assert.AreEqual(typeof(Task<SystemGraphPage>), methods[0].ReturnType);
        Assert.AreEqual("QueryAsync", methods[0].Name);
        Assert.IsFalse(typeof(SystemGraphPage).GetProperties().Any(property => property.PropertyType == typeof(SystemGraphContribution)));
        CollectionAssert.AreEquivalent(new[] { "AnalyzeAsync", "CompareAsync", "GetCoverageAsync" },
            typeof(ISystemGraphImpactService).GetMethods().Select(method => method.Name).ToArray());
    }

    [TestMethod]
    public void SystemGraphTransportHasNoConsoleOrRendererDependency()
    {
        var assembly = typeof(Agentstration.SystemGraph.Api.SystemGraphApiModule).Assembly;
        foreach (var dependency in assembly.GetReferencedAssemblies())
            foreach (var name in new[] { "LikeC4", "React", "FlowDesigner", "Console.Components" })
                Assert.IsFalse(dependency.Name!.Contains(name, StringComparison.OrdinalIgnoreCase), dependency.Name);
    }
}
