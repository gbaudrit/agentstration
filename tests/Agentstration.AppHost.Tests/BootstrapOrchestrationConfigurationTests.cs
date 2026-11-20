extern alias apphost;
using apphost::Agentstration.AppHost;
using Microsoft.Extensions.Configuration;

namespace Agentstration.AppHost.Tests;

[TestClass]
public sealed class BootstrapOrchestrationConfigurationTests
{
    [TestMethod]
    public void AppHostForwardsScopedStartupApplicationsAndExternalTrustWithoutOverridingLaunchActivation()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Agentstration:Bootstrap:Path"] = "../../deploy/bootstrap/profiles",
            ["Agentstration:Bootstrap:InitialBootstrapEnabled"] = "false",
            ["Agentstration:Bootstrap:Secrets:AdminPassword"] = "public-disposable-test-value",
            ["Agentstration:Bootstrap:PhasedStartupEnabled"] = "false",
            ["Agentstration:Bootstrap:ExtensionDependencyTimeout"] = "00:02:00",
            ["Agentstration:Bootstrap:InitialApplications:0:Profiles:0"] = "workspace-profile",
            ["Agentstration:Bootstrap:InitialApplications:0:TargetLevel"] = "/tenants/dev/workspaces/default",
            ["Agentstration:Aep:Enrollment:AutoEnrollmentLevel"] = "/instance",
            ["Agentstration:Aep:Enrollment:SharedKeyFileAllowed"] = "false",
            ["Agentstration:Extensions:DiscoverOnStartup"] = "true",
            ["Agentstration:Extensions:example.custom:EnrollmentMode"] = "SharedKeyFile",
            ["Agentstration:Extensions:example.custom:SharedKeyFile:Path"] = "/tmp/example.key",
            ["Agentstration:Extensions:example.custom:SharedKeyFile:Value"] = "not-a-supported-setting"
        }).Build();
        var environment = BootstrapEnrollmentConfiguration.CreateEnvironment(configuration);
        Assert.IsFalse(environment.ContainsKey("Agentstration__Bootstrap__PhasedStartupEnabled"));
        Assert.AreEqual("workspace-profile", environment["Agentstration__Bootstrap__InitialApplications__0__Profiles__0"]);
        Assert.AreEqual("/tenants/dev/workspaces/default", environment["Agentstration__Bootstrap__InitialApplications__0__TargetLevel"]);
        Assert.AreEqual("/instance", environment["Agentstration__Aep__Enrollment__AutoEnrollmentLevel"]);
        Assert.AreEqual("false", environment["Agentstration__Aep__Enrollment__SharedKeyFileAllowed"]);
        Assert.AreEqual("/tmp/example.key", environment["Agentstration__Extensions__example.custom__SharedKeyFile__Path"]);
        Assert.IsFalse(environment.ContainsKey("Agentstration__Bootstrap__InitialBootstrapEnabled"));
        Assert.IsFalse(environment.ContainsKey("Agentstration__Bootstrap__Path"));
        Assert.IsFalse(environment.ContainsKey("Agentstration__Bootstrap__Secrets__AdminPassword"));
        Assert.IsFalse(environment.ContainsKey("Agentstration__Extensions__DiscoverOnStartup"));
        Assert.IsFalse(environment.ContainsKey("Agentstration__Extensions__example.custom__SharedKeyFile__Value"));
    }
}
