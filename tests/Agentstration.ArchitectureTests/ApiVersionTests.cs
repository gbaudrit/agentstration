using System.Reflection;
using System.Text.RegularExpressions;
using Agentstration.Aep.Abstractions;
using Agentstration.Aep.AspNetCore;
using Agentstration.Aep.Client;
using Agentstration.Aep.MicrosoftExtensionsAI;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;
using Agentstration.Work;

namespace Agentstration.ArchitectureTests;

[TestClass]
public sealed partial class ApiVersionTests
{
    [TestMethod]
    public void ResourcesAndAepOwnOnlyTheCanonicalVersion()
    {
        Assert.AreEqual("agentstration.io/v1", typeof(ResourceApiVersions).GetField(nameof(ResourceApiVersions.CoreV1))!.GetRawConstantValue());
        Assert.AreEqual("agentstration.io/v1", typeof(AepProtocol).GetField(nameof(AepProtocol.Version))!.GetRawConstantValue());
        var constants = typeof(ResourceApiVersions).GetFields(BindingFlags.Public | BindingFlags.Static);
        Assert.HasCount(1, constants);
        Assert.AreEqual(nameof(ResourceApiVersions.CoreV1), constants[0].Name);
        Assert.IsNull(typeof(RuntimeRun).Assembly.GetType("Agentstration.Runtime.Abstractions.RuntimeApiVersions"));
        Assert.IsNull(typeof(WorkItem).Assembly.GetType("Agentstration.Work.WorkplaceApiVersions"));
    }

    [TestMethod]
    public void ProductionSourcesCannotIntroduceDatedApiVersionConstants()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Agentstration.slnx")))
            directory = directory.Parent;
        Assert.IsNotNull(directory);
        var roots = new[] { "src", Path.Combine("aep", "src") };
        var violations = roots.SelectMany(root => Directory.EnumerateFiles(Path.Combine(directory.FullName, root), "*.cs", SearchOption.AllDirectories))
            .Where(path => !path.Split(Path.DirectorySeparatorChar).Any(segment => segment is "obj" or "bin"))
            .Where(path => DatedAlias().IsMatch(File.ReadAllText(path))
                || ApiVersionClass().Matches(File.ReadAllText(path)).Any(match => DatedLiteral().IsMatch(match.Value)))
            .ToArray();
        Assert.IsEmpty(violations, string.Join(Environment.NewLine, violations));
    }

    [TestMethod]
    public void AutonomousAepDoesNotDependOnAgentstrationResourceOrRuntimeAssemblies()
    {
        var assemblies = new[] { typeof(AepProtocol).Assembly, typeof(AepClient).Assembly, typeof(IAepModelProvider).Assembly, typeof(AepChatClient).Assembly };
        var references = assemblies.SelectMany(assembly => assembly.GetReferencedAssemblies()).Select(reference => reference.Name ?? "");
        Assert.IsFalse(references.Any(name => name.StartsWith("Agentstration.", StringComparison.Ordinal)
            && !name.StartsWith("Agentstration.Aep.", StringComparison.Ordinal)));
    }

    [GeneratedRegex(@"\bconst\s+string\s+V\d{8}\b")]
    private static partial Regex DatedAlias();

    [GeneratedRegex(@"\bclass\s+\w*ApiVersions\s*\{[^}]*\}")]
    private static partial Regex ApiVersionClass();

    [GeneratedRegex("\"[0-9]{4}-[0-9]{2}-[0-9]{2}\"")]
    private static partial Regex DatedLiteral();
}
