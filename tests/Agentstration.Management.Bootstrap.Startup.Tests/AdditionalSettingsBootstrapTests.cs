using System.Text.Json;
using System.Net.Http.Json;
using Agentstration.Infrastructure;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Web.Hosting;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Management.Tests;

public sealed partial class DeclarativeBootstrapTests
{
    [TestMethod]
    [DataRow(true, true, true, "core,shared,one,two")]
    [DataRow(true, true, false, "core,shared,one")]
    [DataRow(true, false, true, "core")]
    [DataRow(false, true, true, "")]
    public async Task AdditionalBootstrapRetainsIndependentReferencesArraysActivationAndOrder(
        bool coreEnabled, bool additionalEnabled, bool secondEnabled, string expected)
    {
        using var directory = new TemporaryDirectory();
        var core = await ProbeProfileAsync(directory.Path, "core");
        var shared = await ProbeProfileAsync(directory.Path, "shared");
        var first = await ProbeProfileAsync(Path.Combine(directory.Path, "first-root"), "one");
        var second = await ProbeProfileAsync(Path.Combine(directory.Path, "second-root"), "two");
        var guids = new[] { Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D") };
        var files = new[] { Path.Combine(directory.Path, guids[0] + ".appsettings.json"), Path.Combine(directory.Path, guids[1] + ".appsettings.json") };
        await File.WriteAllTextAsync(files[0], JsonSerializer.Serialize(new Dictionary<string, object>
        {
            [guids[0]] = new
            {
                Bootstrap = new
                {
                    InitialBootstrapEnabled = true,
                    ProfileReferences = new[] { new { Path = shared }, new { Path = first } }
                }
            }
        }));
        await File.WriteAllTextAsync(files[1], JsonSerializer.Serialize(new Dictionary<string, object>
        {
            [guids[1]] = new
            {
                Bootstrap = new
                {
                    InitialBootstrapEnabled = secondEnabled,
                    Path = Path.GetDirectoryName(second),
                    InitialProfiles = new[] { "two" }
                }
            },
            [Guid.NewGuid().ToString("D")] = new
            {
                Bootstrap = new
                {
                    InitialBootstrapEnabled = true,
                    ProfileReferences = new[] { new { Path = "unselected-missing-directory" } }
                }
            }
        }));
        var probe = new StartupProbeHandler();
        await using var factory = new BootstrapApiOnlyWebApplicationFactory().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Agentstration:Authentication:Mode", "Development");
            builder.UseSetting("Agentstration:Extensions:DiscoverOnStartup", "false");
            builder.UseSetting("Agentstration:Bootstrap:Path", directory.Path);
            builder.UseSetting("Agentstration:Bootstrap:InitialProfiles:0", Path.GetFileName(core));
            builder.UseSetting("Agentstration:Bootstrap:InitialBootstrapEnabled", coreEnabled.ToString());
            builder.UseSetting("Agentstration:Bootstrap:EnableAdditionalSettingsBootstrap", additionalEnabled.ToString());
            builder.UseSetting("Agentstration:AdditionalSettings:0", files[0]);
            builder.UseSetting("Agentstration:AdditionalSettings:1", files[1]);
            builder.ConfigureServices(services => services.AddSingleton<IBootstrapResourceHandler>(probe));
        });
        using var client = factory.CreateClient();
        Assert.IsTrue(factory.Services.GetRequiredService<BootstrapStartupState>().IsReady);
        Assert.AreEqual(expected, string.Join(',', probe.Applied));
        // Replay through the same phased service, retaining existing resource identities.
        await using var scope = factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<BootstrapStartupCoordinator>().ExecuteAsync(_ => Task.CompletedTask, default);
        Assert.AreEqual(expected, string.Join(',', probe.Applied));
    }

    [TestMethod]
    [Timeout(90_000)]
    public async Task ShippedGuidExampleCreatesTopologyAndAPublishedDeterministicEntry()
    {
        using var directory = new TemporaryDirectory();
        var repository = FindRepositoryRoot();
        const string guid = "2e65e8a4-0f73-49b6-ae24-636a65d7e793";
        var selected = "deploy/profiles/previews/" + guid;
        foreach (var pair in new[] { (Source: "deploy/bootstrap/profiles/development", Target: "bootstrap/profiles/development"),
                                    (Source: selected, Target: selected) })
            foreach (var file in Directory.EnumerateFiles(Path.Combine(repository, pair.Source), "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(directory.Path, pair.Target, Path.GetRelativePath(Path.Combine(repository, pair.Source), file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target);
            }
        File.Copy(Path.Combine(repository, "src/Agentstration.Web/appsettings.json"), Path.Combine(directory.Path, "appsettings.json"));
        await using var factory = new BootstrapApiOnlyWebApplicationFactory().WithWebHostBuilder(builder =>
        {
            builder.UseContentRoot(directory.Path);
            builder.UseSetting("Agentstration:Authentication:Mode", "Local");
            builder.UseSetting("Agentstration:Bootstrap:Secrets:AdminPassword", InitialPassword);
            builder.UseSetting("Agentstration:Bootstrap:InitialBootstrapEnabled", "true");
            builder.UseSetting("Agentstration:Bootstrap:EnableAdditionalSettingsBootstrap", "true");
            builder.UseSetting("Agentstration:AdditionalSettings:0", Path.Combine(directory.Path, selected, guid + ".appsettings.json"));
        });
        using var client = factory.CreateClient();
        using var login = await client.PostAsJsonAsync("/api/auth/local/login", new { userName = "admin", password = InitialPassword });
        login.EnsureSuccessStatusCode();
        using var entry = await client.GetAsync("/api/entries/bootstrap-echo");
        entry.EnsureSuccessStatusCode();
        using var versions = await client.GetAsync("/api/flows/bootstrap-echo/versions");
        versions.EnsureSuccessStatusCode();
        StringAssert.Contains(await versions.Content.ReadAsStringAsync(), "1.0.0");
        Assert.IsTrue(factory.Services.GetRequiredService<BootstrapStartupState>().IsReady);
    }

    private static async Task<string> ProbeProfileAsync(string root, string name)
    {
        var directory = Directory.CreateDirectory(Path.Combine(root, name)).FullName;
        await File.WriteAllTextAsync(Path.Combine(directory, "profile.yaml"), Profile(name, "instance"));
        await File.WriteAllTextAsync(Path.Combine(directory, "10-probe.yaml"), $$"""
            apiVersion: agentstration.io/v1
            kind: StartupProbe
            metadata: {name: {{name}}}
            definition: {}
            """);
        return directory;
    }

    private sealed class StartupProbeHandler : IBootstrapResourceHandler
    {
        public string Kind => "StartupProbe";
        public BootstrapProfileScope Scope => BootstrapProfileScope.Instance;
        public List<string> Applied { get; } = [];
        public Task<BootstrapResourcePlanResult> PlanAsync(BootstrapResourceDocument resource,
            BootstrapResourceOperationContext operation, BootstrapPlanningContext planning, CancellationToken cancellationToken) =>
            Task.FromResult(new BootstrapResourcePlanResult(Applied.Contains(resource.Metadata.Name)
                ? BootstrapResourceDisposition.Skip : BootstrapResourceDisposition.Create));
        public Task<BootstrapResourceApplyResult> ApplyAsync(BootstrapResourceDocument resource,
            BootstrapResourceOperationContext operation, CancellationToken cancellationToken)
        {
            if (Applied.Contains(resource.Metadata.Name)) return Task.FromResult(BootstrapResourceApplyResult.Skipped);
            Applied.Add(resource.Metadata.Name);
            return Task.FromResult(BootstrapResourceApplyResult.Created);
        }
    }
}
