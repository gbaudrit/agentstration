using Agentstration.Management.Abstractions;
using Agentstration.Management.Core;
using Agentstration.Management.Storage.Sqlite;
using Agentstration.Resources;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Management.Tests;

[TestClass]
public sealed class MemoryManagementTests
{
    [TestMethod]
    public async Task ConfiguredSqliteExtensionSeedsOptionalAepProviderAndProfile()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Agentstration:Extensions:Agentstration.Extensions.Memory.Sqlite:Endpoint", "http://localhost:5285");
            builder.UseSetting("Logging:LogLevel:Default", "Warning");
        });
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/health");
        Assert.IsTrue(response.IsSuccessStatusCode);

        var providers = factory.Services.GetRequiredService<MemoryProviderManagementService>();
        var profiles = factory.Services.GetRequiredService<MemoryProfileManagementService>();
        var provider = await providers.GetAsync(ResourceNamespace.Default, "memory-sqlite-aep", default);
        var profile = await profiles.GetAsync(ResourceNamespace.Default, "aep-memory-default", default);

        Assert.IsNotNull(provider);
        Assert.AreEqual(MemoryProviderIntegrationKind.Aep, provider.Value.Definition.IntegrationKind);
        Assert.AreEqual("Agentstration.Extensions.Memory.Sqlite", provider.Value.Definition.Aep?.ExtensionId);
        Assert.AreEqual("sqlite", provider.Value.Definition.Aep?.ProviderId);
        Assert.IsNotNull(profile);
        Assert.AreEqual("memory-sqlite-aep", profile.Value.Definition.Provider.Name);
    }

    [TestMethod]
    public async Task ProviderAndProfileValidateBindingsLimitsAndImmutableIntegration()
    {
        var directory = Path.Combine(Path.GetTempPath(), "agentstration-memory-management", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var services = new ServiceCollection()
            .AddSingleton<TimeProvider>(TimeProvider.System)
            .AddSingleton<ICurrentRequestContext, SystemOperationRequestContext>()
            .AddSqliteControlPlane($"Data Source={Path.Combine(directory, "management.db")};Pooling=False")
            .AddAgentstrationMemoryManagement();
        await using var container = services.BuildServiceProvider();
        try
        {
            await container.GetRequiredService<IControlPlaneStore>().InitializeAsync(default);
            var providers = container.GetRequiredService<MemoryProviderManagementService>();
            var profiles = container.GetRequiredService<MemoryProfileManagementService>();
            var provider = await providers.CreateAsync(Provider("local-memory"), default);
            var profile = await profiles.CreateAsync(Profile("default-memory", "local-memory", 7), default);

            Assert.AreEqual("local-memory", profile.Value.Definition.Provider.Name);
            Assert.AreEqual(7, profile.Value.Definition.Retrieval.MaximumRecords);
            await Assert.ThrowsAsync<MemoryManagementException>(() => providers.CreateAsync(Provider("second-local"), default));
            await Assert.ThrowsAsync<MemoryManagementException>(() => providers.PutAsync(ResourceNamespace.Default, "local-memory",
                provider.Value.Definition with { IntegrationKind = MemoryProviderIntegrationKind.Aep, Builtin = null, Aep = new() { ExtensionId = "x", ProviderId = "y" } }, provider.ETag, default));
            await Assert.ThrowsAsync<MemoryManagementException>(() => profiles.CreateAsync(Profile("invalid", "local-memory", 21), default));
            await Assert.ThrowsAsync<MemoryManagementException>(() => providers.DeleteAsync(ResourceNamespace.Default, "local-memory", provider.ETag, default));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
    }

    private static MemoryProviderResource Provider(string name) => new()
    {
        ApiVersion = ManagementApiVersions.CoreV1,
        Kind = ResourceKinds.MemoryProvider,
        Metadata = new ResourceMetadata { Name = name },
        Definition = new MemoryProviderProperties { DisplayName = name, IntegrationKind = MemoryProviderIntegrationKind.Builtin, Builtin = new() }
    };

    private static MemoryProfileResource Profile(string name, string provider, int maximumRecords) => new()
    {
        ApiVersion = ManagementApiVersions.CoreV1,
        Kind = ResourceKinds.MemoryProfile,
        Metadata = new ResourceMetadata { Name = name },
        Definition = new MemoryProfileProperties
        {
            DisplayName = name,
            Provider = new ResourceReference(provider),
            Retrieval = new MemoryRetrievalConfiguration { MaximumRecords = maximumRecords }
        }
    };
}
