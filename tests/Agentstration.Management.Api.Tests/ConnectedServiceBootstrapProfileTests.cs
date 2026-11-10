using System.Text.Json;
using Agentstration.Bootstrap.Contracts;
using Agentstration.ConnectedServices;
using Agentstration.ConnectedServices.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;
using Agentstration.Web.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agentstration.Management.Tests;

[TestClass]
public sealed class ConnectedServiceBootstrapProfileTests : ModelManagementApiTestBase
{
    private const string TypeNamespace = "agentstration.connected-services";
    private static readonly string[] TypeNames =
    [
        "github", "gitlab", "azure-devops", "jira", "confluence", "sharepoint-online",
        "google-drive", "slack", "azure-storage", "azure-foundry", "azure-openai",
        "azure-ai-search", "aws-s3", "s3-compatible", "openai", "ollama", "localai",
        "postgresql", "sql-server", "redis"
    ];

    [TestMethod]
    public async Task ProfilesInstallVersionedTypesWithoutConnectionsOrImplicitUseGrants()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();
        _ = await GetBootstrapContextAsync(factory);
        await using var scope = factory.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        using var system = services.GetRequiredService<IRequestContextScopeFactory>().PushSystem();
        var (catalog, bootstrap) = CreateBootstrap(services);
        var snapshot = await catalog.GetSnapshotAsync(default);
        var profiles = snapshot.Profiles.Where(profile => profile.Name.StartsWith(
            "connected-service-", StringComparison.Ordinal)).ToArray();
        CollectionAssert.AreEquivalent(TypeNames.Select(ProfileName).ToArray(), profiles.Select(profile => profile.Name).ToArray());
        Assert.IsTrue(profiles.All(profile => profile.Valid && profile.Scope == BootstrapProfileScope.Instance
            && profile.ResourceCount == 1 && profile.Bindings.Count == 0));

        var store = services.GetRequiredService<IResourceStore>();
        var connectionsBefore = await store.ListAllAsync<ConnectedServiceResource>(ConnectedServiceResourceKinds.ConnectedService, default);
        foreach (var batch in TypeNames.Chunk(catalog.MaximumSelectedProfiles))
        {
            var selection = new BootstrapProfileSelection(batch.Select(ProfileName).ToArray());
            var preview = await bootstrap.PreviewAsync(selection, default);
            Assert.IsTrue(preview.CanApply, string.Join(Environment.NewLine, preview.Resources.Select(resource => resource.Message)));
            Assert.IsTrue(preview.Resources.All(resource => resource.Disposition == BootstrapResourceDisposition.Create));
            var applied = await bootstrap.ExecuteAsync(selection, default);
            Assert.IsNull(applied.Error);
            Assert.AreEqual(batch.Length, applied.Resources.Count);
        }

        var types = services.GetRequiredService<ConnectedServiceTypeManagementService>();
        foreach (var name in TypeNames)
        {
            var stored = await types.GetExactAsync(ResourceScopeRef.Instance, new ResourceNamespace(TypeNamespace), name, default);
            Assert.IsNotNull(stored, name);
            Assert.AreEqual(ResourceOriginKind.User, stored.Value.Governance.Origin.Kind);
            Assert.IsEmpty(stored.Value.Definition.UsePolicy.Grants);
            var version = stored.Value.Definition.Versions.Single();
            Assert.AreEqual("1.0.0", version.Version);
            Assert.IsNull(version.OperationDiscovery);
            Assert.IsTrue(version.Exports.All(export => export.SourceInputId is not null));
            foreach (var tag in new[] { "category", "vendor", "product" })
                Assert.IsTrue(stored.Value.Metadata.Tags.ContainsKey(tag), $"{name}/{tag}");
        }
        var connectionsAfter = await store.ListAllAsync<ConnectedServiceResource>(ConnectedServiceResourceKinds.ConnectedService, default);
        Assert.AreEqual(connectionsBefore.Count, connectionsAfter.Count);
    }

    [TestMethod]
    public async Task CompositionInstallsOnlySelectedProfilesAndReapplicationPreservesOperatorChanges()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();
        _ = await GetBootstrapContextAsync(factory);
        await using var scope = factory.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        using var system = services.GetRequiredService<IRequestContextScopeFactory>().PushSystem();
        var (_, bootstrap) = CreateBootstrap(services);
        var selection = new BootstrapProfileSelection([ProfileName("github"), ProfileName("azure-storage")]);
        var applied = await bootstrap.ExecuteAsync(selection, default);
        Assert.IsNull(applied.Error);
        Assert.HasCount(2, applied.Resources);

        var types = services.GetRequiredService<ConnectedServiceTypeManagementService>();
        var original = await types.GetExactAsync(ResourceScopeRef.Instance, new ResourceNamespace(TypeNamespace), "github", default);
        Assert.IsNotNull(original);
        var changed = await types.PutExactAsync(ResourceScopeRef.Instance, new ResourceNamespace(TypeNamespace), "github",
            original.Value.Definition with { DisplayName = "Operator GitHub", Enabled = false }, original.ETag, default);
        var repeated = await bootstrap.ExecuteAsync(selection, default);
        Assert.IsNull(repeated.Error);
        Assert.IsTrue(repeated.Resources.All(resource => resource.Disposition == BootstrapResourceDisposition.Skip));
        var retained = await types.GetExactAsync(ResourceScopeRef.Instance, new ResourceNamespace(TypeNamespace), "github", default);
        Assert.IsNotNull(retained);
        Assert.AreEqual(changed.Value.Uid, retained.Value.Uid);
        Assert.AreEqual(changed.ETag, retained.ETag);
        Assert.AreEqual("Operator GitHub", retained.Value.Definition.DisplayName);
        Assert.IsFalse(retained.Value.Definition.Enabled);
        Assert.IsNull(await types.GetExactAsync(ResourceScopeRef.Instance, new ResourceNamespace(TypeNamespace), "gitlab", default));
    }

    [TestMethod]
    public async Task PublishedConnectionContractsRejectLiteralCredentialsAndMissingRequiredCredentials()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();
        var context = await GetBootstrapContextAsync(factory);
        await using var scope = factory.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        using var system = services.GetRequiredService<IRequestContextScopeFactory>().PushSystem();
        var (catalog, bootstrap) = CreateBootstrap(services);
        var types = services.GetRequiredService<ConnectedServiceTypeManagementService>();
        var connections = services.GetRequiredService<ConnectedServiceManagementService>();
        foreach (var batch in TypeNames.Chunk(catalog.MaximumSelectedProfiles))
            Assert.IsNull((await bootstrap.ExecuteAsync(new(batch.Select(ProfileName).ToArray()), default)).Error);

        foreach (var name in TypeNames)
        {
            var type = (await types.GetExactAsync(ResourceScopeRef.Instance, new ResourceNamespace(TypeNamespace), name, default))!.Value;
            var version = type.Definition.Versions.Single();
            var standardBindings = version.Inputs.Where(input => input.Protection == ConnectedServiceValueProtection.Standard)
                .Select(LiteralBinding).ToArray();
            var resource = new ConnectedServiceResource
            {
                ApiVersion = ResourceApiVersions.CoreV1,
                Kind = ConnectedServiceResourceKinds.ConnectedService,
                Metadata = new() { Name = $"test-{name}" },
                ScopeRef = ResourceScopeRef.Instance,
                Definition = new()
                {
                    DisplayName = $"Test {name}",
                    Type = new()
                    {
                        Type = new(name, ResourceScopeRef.Instance, new ResourceNamespace(TypeNamespace)),
                        TypeUid = type.Uid,
                        Version = version.Version,
                        SchemaDigest = version.SchemaDigest
                    },
                    Options = JsonSerializer.SerializeToElement(new { }),
                    Bindings = standardBindings
                }
            };
            if (name == "ollama")
            {
                var created = await connections.CreateAsync(resource, default);
                Assert.AreEqual(ConnectedServiceAvailability.Ready, created.Value.ObservedState?.Availability);
                // Instance-installed types require an explicit descendant grant even for an administrator.
                await Assert.ThrowsExactlyAsync<ConnectedServiceValidationException>(() => connections.CreateAsync(
                    resource with { ScopeRef = ResourceScopeRef.Workspace(context.WorkspaceId) }, default));
                continue;
            }

            Assert.IsTrue(version.Inputs.Any(input => input.Required && input.Protection == ConnectedServiceValueProtection.Secured), name);
            await Assert.ThrowsExactlyAsync<ConnectedServiceValidationException>(() => connections.CreateAsync(resource, default));
            foreach (var credential in version.Inputs.Where(input => input.Protection == ConnectedServiceValueProtection.Secured))
            {
                var credentialExport = version.Exports.Single(export => export.SourceInputId == credential.Id);
                Assert.AreEqual(ConnectedServiceValueProtection.Secured, credentialExport.Protection);
            }
            await Assert.ThrowsExactlyAsync<ConnectedServiceValidationException>(() => connections.CreateAsync(
                resource with
                {
                    Definition = resource.Definition with { Bindings = version.Inputs.Select(LiteralBinding).ToArray() }
                }, default));
        }
    }

    private static ConnectedServiceValueBinding LiteralBinding(ConnectedServiceInputDescriptor input) => new()
    {
        InputId = input.Id,
        Kind = ConnectedServiceBindingKind.Literal,
        Literal = input.Type switch
        {
            ConnectedServiceValueType.WholeNumber => JsonSerializer.SerializeToElement(5432),
            ConnectedServiceValueType.Logical => JsonSerializer.SerializeToElement(true),
            _ => JsonSerializer.SerializeToElement(input.Format == "uri" ? "https://service.example.test" : "synthetic-value")
        }
    };

    private static string ProfileName(string typeName) => $"connected-service-{typeName}";

    private static (BootstrapProfileCatalog Catalog, DeclarativeBootstrapService Bootstrap) CreateBootstrap(IServiceProvider services)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Agentstration.slnx")))
            directory = directory.Parent;
        var root = directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Agentstration:Bootstrap:Path"] = Path.Combine(root, "deploy", "bootstrap", "profiles")
        }).Build();
        var catalog = new BootstrapProfileCatalog(configuration, new TestHostEnvironment(root));
        return (catalog, new(configuration, catalog, services.GetServices<IBootstrapResourceHandler>(),
            NullLogger<DeclarativeBootstrapService>.Instance));
    }

    private sealed class TestHostEnvironment(string contentRoot) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "Agentstration.Management.Tests";
        public string ContentRootPath { get; set; } = contentRoot;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
