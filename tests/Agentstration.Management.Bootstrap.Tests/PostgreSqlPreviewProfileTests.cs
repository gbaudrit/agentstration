using Agentstration.ResourceManagement;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Web.Hosting;
using Microsoft.Extensions.Configuration;

namespace Agentstration.Management.Tests;

public sealed partial class DeclarativeBootstrapTests
{
    [TestMethod]
    public async Task PostgreSqlPreviewProfileIsOptionalAndUsesTargetSuppliedConnectionBindings()
    {
        var root = FindRepositoryRoot();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Agentstration:Bootstrap:Path"] = Path.Combine(root, "deploy", "bootstrap", "profiles")
        }).Build();
        var snapshot = await new BootstrapProfileCatalog(configuration, new TestHostEnvironment(root))
            .GetSnapshotAsync(default);
        var profile = snapshot.Profiles.Single(value => value.Name == "preview-postgresql-documents");

        Assert.IsTrue(profile.Valid, profile.Error);
        Assert.AreEqual(BootstrapProfileScope.Instance, profile.Scope);
        Assert.AreEqual(5, profile.ResourceCount);
        Assert.IsFalse(snapshot.InitialProfiles.Contains(profile.Name, StringComparer.Ordinal));
        Assert.IsEmpty(profile.Bindings, "Connection values come from the target configuration, without interactive bindings.");
    }
}
