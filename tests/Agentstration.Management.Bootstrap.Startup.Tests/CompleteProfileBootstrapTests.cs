using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agentstration.ConnectedServices;
using Agentstration.ConnectedServices.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.Infrastructure;
using Agentstration.Infrastructure.ConnectedServices;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Secrets;
using Agentstration.Secrets.Abstractions;
using Agentstration.Storage.Contracts;
using Agentstration.Web.Hosting;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Agentstration.Management.Tests;

public sealed partial class DeclarativeBootstrapTests
{
    [TestMethod]
    public async Task DiscoveryWithoutAnAuthorizedActorIdentifiesTheInstructionAndLeavesOtherResourcesBlocked()
    {
        using var directory = new TemporaryDirectory();
        await WriteCompleteFixtureAsync(directory.Path);
        var gateway = new BootstrapDiscoveryGateway();
        await using var factory = CompleteFactory(directory.Path, gateway, Guid.NewGuid().ToString("N"));
        using var client = factory.CreateClient();
        _ = await factory.Services.GetRequiredService<ILocalEnvironmentBootstrapper>().EnsureInitializedAsync(default);
        await using var scope = factory.Services.CreateAsyncScope();
        var startup = scope.ServiceProvider.GetRequiredService<BootstrapStartupState>();
        var profile = (await scope.ServiceProvider.GetRequiredService<BootstrapProfileCatalog>().LoadAsync(["complete"], default)).Single();
        startup.Begin(); startup.MoveTo("resources");
        foreach (var source in profile.Resources)
            startup.RegisterResource("/instance", profile.Summary.Name, source.Location, "resources", source.Resource.Kind, source.Resource.Metadata.Name, source.Resource.Metadata.Namespace.Value);
        using var system = scope.ServiceProvider.GetRequiredService<IRequestContextScopeFactory>().PushSystem();
        var result = await scope.ServiceProvider.GetRequiredService<DeclarativeBootstrapService>().ExecutePhaseAsync(new(["complete"]), BootstrapStartupPhase.Resources, default, startupTargetLevel: "/instance");
        Assert.AreEqual("bootstrap_discovery_actor_required", result.Error);
        Assert.HasCount(0, result.Resources);
        Assert.AreEqual(0, gateway.Starts);
        startup.Fail(new DeclarativeBootstrapException(result.Error!));
        var snapshot = startup.Read();
        var rows = startup.ReadResources(new(snapshot.AttemptId))!.Items;
        var failure = rows.Single(row => row.State == "failed");
        Assert.AreEqual("ConnectedServiceOperationDiscovery", failure.Kind);
        Assert.AreEqual(failure.Id, snapshot.Activity?.ResourceId);
        Assert.AreEqual(1, snapshot.Failed);
        Assert.IsTrue(rows.Where(row => row.Id != failure.Id).All(row => row.State == "blocked"));
        Assert.AreEqual(result.Error, snapshot.Failure?.Code);
        Assert.IsNull(await scope.ServiceProvider.GetRequiredService<IResourceStore>().GetExactAsync<VaultResource>(new(ResourceScopeRef.Instance, ResourceNamespace.Default, "Vault", "fixture-vault"), default));
    }

    [TestMethod]
    public async Task ProfileFilesExposeOriginalManifestsWithoutResolvingSecretsOrExecutingDiscovery()
    {
        using var directory = new TemporaryDirectory();
        await WriteCompleteFixtureAsync(directory.Path);
        var gateway = new BootstrapDiscoveryGateway();
        var password = Guid.NewGuid().ToString("N");
        await using var factory = CompleteFactory(directory.Path, gateway, password);
        using var client = factory.CreateClient();
        var actor = await factory.Services.GetRequiredService<ILocalEnvironmentBootstrapper>().EnsureInitializedAsync(default);
        await using var scope = factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<BootstrapProfileManagementService>();
        var listing = await service.GetFilesAsync(new("complete"), actor.PrincipalId, default);
        using var response = await client.PostAsJsonAsync("/api/bootstrap/profiles/files", new BootstrapProfileFilesRequest("complete", "profile.yaml"));
        response.EnsureSuccessStatusCode();
        Assert.IsTrue(response.Headers.CacheControl?.NoStore);
        Assert.Contains("profile.yaml", listing.Files);
        Assert.Contains("20-secret.yaml", listing.Files);
        Assert.IsNull(listing.Yaml);
        var source = await service.GetFilesAsync(new("complete", "20-secret.yaml", ExpectedDigest: listing.Digest), actor.PrincipalId, default);
        Assert.AreEqual(await File.ReadAllTextAsync(Path.Combine(directory.Path, "complete", "20-secret.yaml")), source.Yaml);
        Assert.DoesNotContain(password, source.Yaml!, StringComparison.Ordinal);
        StringAssert.Contains(source.Yaml!, "valueFrom");
        StringAssert.Contains(source.Yaml!, "configuration");
        Assert.AreEqual(0, gateway.Starts);
        foreach (var invalidFile in new[] { "../20-secret.yaml", "..\\20-secret.yaml", Path.Combine(directory.Path, "complete", "20-secret.yaml"), "missing.yaml" })
            await Assert.ThrowsAsync<DeclarativeBootstrapException>(() => service.GetFilesAsync(new("complete", invalidFile), actor.PrincipalId, default));
        await Assert.ThrowsAsync<DeclarativeBootstrapException>(() => service.GetFilesAsync(new("complete", "profile.yaml", ExpectedDigest: "stale"), actor.PrincipalId, default));
        await Assert.ThrowsAsync<Agentstration.Identity.Contracts.AuthorizationDeniedException>(() => service.GetFilesAsync(new("complete"), Guid.NewGuid(), default));
    }

    [TestMethod]
    public async Task NantesIsAnOptInInstanceCatalogProfileWithPortableReferencesAndNoPassword()
    {
        var root = FindRepositoryRoot();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Agentstration:Bootstrap:Path"] = Path.Combine(root, "deploy/bootstrap/profiles") }).Build();
        var catalog = new BootstrapProfileCatalog(configuration, new TestHostEnvironment(root));
        var profile = (await catalog.LoadAsync(["nantes-alertes"], default)).Single();
        Assert.AreEqual("Nantes - Alertes", profile.Summary.DisplayName);
        Assert.AreEqual(BootstrapProfileScope.Instance, profile.Summary.Scope);
        Assert.HasCount(13, profile.Resources);
        Assert.HasCount(2, profile.Resources.Where(item => item.Resource.Kind == "ConnectedServiceOperationDiscovery").ToArray());
        var text = string.Join('\n', profile.Resources.Select(item => item.Resource.Definition.GetRawText()));
        var initializer = profile.Resources.Single(item => item.Resource.Kind == "Secret").Resource.Definition.GetProperty("valueFrom");
        CollectionAssert.AreEqual(new[] { "configuration" }, initializer.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.DoesNotContain("operationUid", text, StringComparison.Ordinal);
        Assert.DoesNotContain("storageUid", text, StringComparison.Ordinal);
        StringAssert.Contains(text, "Agentstration:Bootstrap:Secrets:NantesPostgreSqlPassword");
        var snapshot = await catalog.GetSnapshotAsync(default);
        Assert.IsFalse(snapshot.InitialBootstrapEnabled, "Discovering a profile must not activate initial bootstrap.");
        Assert.DoesNotContain("nantes-alertes", snapshot.InitialProfiles);
    }

    [TestMethod]
    [Timeout(90_000)]
    public async Task ConfiguredProfilePreviewIsReadOnlyAndApplyDiscoversPinsAndReappliesWithoutRotation()
    {
        using var directory = new TemporaryDirectory();
        await WriteCompleteFixtureAsync(directory.Path);
        var gateway = new BootstrapDiscoveryGateway();
        var password = Guid.NewGuid().ToString("N");
        await using var factory = CompleteFactory(directory.Path, gateway, password);
        using var client = factory.CreateClient();
        var context = await factory.Services.GetRequiredService<ILocalEnvironmentBootstrapper>().EnsureInitializedAsync(default);
        await using var scope = factory.Services.CreateAsyncScope();
        var management = scope.ServiceProvider.GetRequiredService<BootstrapProfileManagementService>();
        var selection = new BootstrapProfileSelection(["complete"]);
        var preview = await management.PreviewAsync(selection, context.PrincipalId, default);
        Assert.IsTrue(preview.CanApply, string.Join('\n', preview.Resources.Select(item => item.Message)));
        Assert.IsTrue(preview.Resources.Any(item => item.ValidationDeferred));
        Assert.AreEqual(0, gateway.Starts);
        Assert.DoesNotContain(password, JsonSerializer.Serialize(preview), StringComparison.Ordinal);
        using (scope.ServiceProvider.GetRequiredService<IRequestContextScopeFactory>().PushSystem())
        {
            var store = scope.ServiceProvider.GetRequiredService<IResourceStore>();
            Assert.IsNull(await store.GetExactAsync<ConnectedServiceResource>(new(ResourceScopeRef.Instance, ResourceNamespace.Default, "ConnectedService", "fixture-http"), default));
            gateway.Type = (await scope.ServiceProvider.GetRequiredService<ConnectedServiceTypeManagementService>()
                .GetExactAsync(ResourceScopeRef.Instance, ResourceNamespace.Default, "http-endpoint", default))!.Value.Definition.Versions.Single().OperationDiscovery!.ProducedTypes.Single();
        }
        var application = await management.ApplyAsync(selection, preview.Digest, context.PrincipalId, default);
        Assert.AreEqual(BootstrapApplicationStatus.Succeeded, application.Definition.Status,
            string.Join('\n', application.Definition.Resources.Select(item => item.Kind + ": " + item.Failure?.Code)));
        Assert.AreEqual(1, gateway.Starts);
        var serialized = JsonSerializer.Serialize(application);
        Assert.DoesNotContain(password, serialized, StringComparison.Ordinal);
        using (scope.ServiceProvider.GetRequiredService<IRequestContextScopeFactory>().PushSystem())
        {
            var store = scope.ServiceProvider.GetRequiredService<IResourceStore>();
            var source = (await store.GetExactAsync<Agentstration.DataSources.Contracts.DataSourceResource>(
                new(ResourceScopeRef.Instance, ResourceNamespace.Default, "DataSource", "fixture-data"), default))!.Value;
            Assert.AreNotEqual(Guid.Empty, source.Definition.Dataset!.Storage!.StorageUid);
            Assert.AreNotEqual(Guid.Empty, source.Definition.Dataset.Acquisition.Operation.OperationUid);
            Assert.DoesNotContain("resourceRef", JsonSerializer.Serialize(source.Definition), StringComparison.Ordinal);
            Assert.AreEqual(SecretValueStatus.Configured, (await scope.ServiceProvider.GetRequiredService<SecretManagementService>()
                .GetSecretViewExactAsync(ResourceScopeRef.Instance, "fixture-password", default)).ValueStatus);
        }
        factory.Services.GetRequiredService<IConfiguration>()["Agentstration:Bootstrap:Secrets:FixturePassword"] = null;
        var retryPreview = await management.PreviewAsync(selection, context.PrincipalId, default);
        Assert.IsTrue(retryPreview.CanApply, string.Join('\n', retryPreview.Resources.Select(item => item.Message)));
        Assert.IsTrue(retryPreview.Resources.All(item => item.Disposition == BootstrapResourceDisposition.Skip));
        var retry = await management.ApplyAsync(selection, retryPreview.Digest, context.PrincipalId, default);
        Assert.AreEqual(BootstrapApplicationStatus.Succeeded, retry.Definition.Status);
        Assert.AreEqual(1, gateway.Starts);
        using (scope.ServiceProvider.GetRequiredService<IRequestContextScopeFactory>().PushSystem())
        {
            var storageService = scope.ServiceProvider.GetRequiredService<Agentstration.Storage.StorageManagementService>();
            var stored = (await storageService.GetExactAsync(ResourceScopeRef.Instance, ResourceNamespace.Default, "fixture-content", default))!;
            _ = await storageService.PutExactAsync(ResourceScopeRef.Instance, ResourceNamespace.Default, "fixture-content",
                stored.Value.Definition with { DisplayName = "Operator changed this storage" }, stored.ETag, default);
        }
        var changedDependencies = await management.PreviewAsync(selection, context.PrincipalId, default);
        Assert.IsFalse(changedDependencies.CanApply);
        Assert.AreEqual("bootstrap_dependency_revision_changed", changedDependencies.Resources.Single(item => item.Kind == "DataSource").Message);
    }

    [TestMethod]
    [Timeout(90_000)]
    public async Task MissingSecretIsActionableAndPlaintextOverrideCannotEnterDurableHistory()
    {
        using var directory = new TemporaryDirectory();
        await WriteCompleteFixtureAsync(directory.Path);
        await using var factory = CompleteFactory(directory.Path, new(), null);
        using var client = factory.CreateClient();
        var context = await factory.Services.GetRequiredService<ILocalEnvironmentBootstrapper>().EnsureInitializedAsync(default);
        await using var scope = factory.Services.CreateAsyncScope();
        var management = scope.ServiceProvider.GetRequiredService<BootstrapProfileManagementService>();
        var preview = await management.PreviewAsync(new(["complete"]), context.PrincipalId, default);
        Assert.IsFalse(preview.CanApply);
        var missing = preview.Inputs.Single(item => item.Secured);
        Assert.IsFalse(missing.Configured);
        Assert.AreEqual("Agentstration:Bootstrap:Secrets:FixturePassword", missing.Configuration);
        var selections = new[] { new BootstrapInputSelection("complete", missing.Configuration, "literal", JsonSerializer.SerializeToElement("must-not-be-stored")) };
        await Assert.ThrowsAsync<BootstrapRequestException>(() => management.StartAsync(
            new(["complete"], preview.Digest, Inputs: selections), context.PrincipalId, default));
        var history = await management.GetAsync(context.PrincipalId, default);
        Assert.HasCount(0, history.Applications);
    }

    [TestMethod]
    [Timeout(90_000)]
    public async Task MissingWrongKindAndCollectionReferencesFailPreviewBeforeAnyDiscovery()
    {
        using var directory = new TemporaryDirectory();
        await WriteCompleteFixtureAsync(directory.Path);
        var data = Path.Combine(directory.Path, "complete/70-data.yaml");
        var original = await File.ReadAllTextAsync(data);
        var gateway = new BootstrapDiscoveryGateway();
        await using var factory = CompleteFactory(directory.Path, gateway, Guid.NewGuid().ToString("N"));
        using var client = factory.CreateClient();
        var context = await factory.Services.GetRequiredService<ILocalEnvironmentBootstrapper>().EnsureInitializedAsync(default);
        await using var scope = factory.Services.CreateAsyncScope();
        var management = scope.ServiceProvider.GetRequiredService<BootstrapProfileManagementService>();
        foreach (var text in new[] { original.Replace("name: fixture-content", "name: absent", StringComparison.Ordinal),
                     original.Replace("name: fixture-content", "name: fixture-content, kind: ConnectedService", StringComparison.Ordinal),
                     original.Replace("    rule:", "    documentCollection: absent\n    rule:", StringComparison.Ordinal),
                     original.Replace("output: data", "output: absent", StringComparison.Ordinal) })
        {
            await File.WriteAllTextAsync(data, text);
            var preview = await management.PreviewAsync(new(["complete"]), context.PrincipalId, default);
            Assert.IsFalse(preview.CanApply);
            Assert.AreEqual(BootstrapResourceDisposition.Invalid, preview.Resources.Single(item => item.Kind == "DataSource").Disposition);
        }
        Assert.AreEqual(0, gateway.Starts);
    }

    private static WebApplicationFactory<Program> CompleteFactory(string path, BootstrapDiscoveryGateway gateway, string? password,
        Action<IServiceCollection>? configure = null) =>
        new BootstrapApiOnlyWebApplicationFactory().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Agentstration:Authentication:Mode", "Development");
            builder.UseSetting("Agentstration:Authentication:DevelopmentGrantPlatformAdministrator", "true");
            builder.UseSetting("Agentstration:Bootstrap:Path", path);
            builder.UseSetting("Agentstration:Bootstrap:InitialBootstrapEnabled", "false");
            if (password is not null) builder.UseSetting("Agentstration:Bootstrap:Secrets:FixturePassword", password);
            builder.ConfigureServices(services =>
            {
                services.Replace(ServiceDescriptor.Singleton<IConnectedServiceOperationDiscoveryFlowGateway>(gateway));
                configure?.Invoke(services);
            });
        });

    [TestMethod]
    [Timeout(90_000)]
    public async Task ConfiguredLocalSecretIsNotReinitializedOrOverwrittenWhenItsKeyBecomesUnavailable()
    {
        using var directory = new TemporaryDirectory();
        await WriteCompleteFixtureAsync(directory.Path);
        File.Delete(Path.Combine(directory.Path, "complete/60-discovery.yaml"));
        File.Delete(Path.Combine(directory.Path, "complete/70-data.yaml"));
        var keys = new BootstrapMasterKey();
        var initialPassword = Guid.NewGuid().ToString("N");
        await using var factory = CompleteFactory(directory.Path, new(), initialPassword,
            services => services.Replace(ServiceDescriptor.Singleton<IMasterKeyProvider>(keys)));
        using var client = factory.CreateClient();
        var actor = await factory.Services.GetRequiredService<ILocalEnvironmentBootstrapper>().EnsureInitializedAsync(default);
        await using var scope = factory.Services.CreateAsyncScope();
        var management = scope.ServiceProvider.GetRequiredService<BootstrapProfileManagementService>();
        var preview = await management.PreviewAsync(new(["complete"]), actor.PrincipalId, default);
        var application = await management.ApplyAsync(new(["complete"]), preview.Digest, actor.PrincipalId, default);
        Assert.AreEqual(BootstrapApplicationStatus.Succeeded, application.Definition.Status);
        var initialized = keys.Initializations;
        keys.Available = false;
        factory.Services.GetRequiredService<IConfiguration>()["Agentstration:Bootstrap:Secrets:FixturePassword"] = Guid.NewGuid().ToString("N");
        var unavailable = await management.PreviewAsync(new(["complete"]), actor.PrincipalId, default);
        Assert.IsTrue(unavailable.CanApply);
        Assert.AreEqual(BootstrapResourceDisposition.Skip, unavailable.Resources.Single(item => item.Kind == "Secret").Disposition);
        var retry = await management.ApplyAsync(new(["complete"]), unavailable.Digest, actor.PrincipalId, default);
        Assert.AreEqual(BootstrapApplicationStatus.Succeeded, retry.Definition.Status);
        Assert.AreEqual(initialized, keys.Initializations);
        keys.Available = true;
        var restored = await management.PreviewAsync(new(["complete"]), actor.PrincipalId, default);
        Assert.IsTrue(restored.CanApply);
        Assert.AreEqual(BootstrapResourceDisposition.Skip, restored.Resources.Single(item => item.Kind == "Secret").Disposition);
        using (scope.ServiceProvider.GetRequiredService<IRequestContextScopeFactory>().PushSystem())
        {
            using var value = await scope.ServiceProvider.GetRequiredService<SecretManagementService>().ResolveAsync(
                new(new(ResourceNamespace.Default, "Secret", "fixture-password"), ResourceScopeRef.Instance),
                new(ResourceScopeRef.Instance, new(ResourceNamespace.Default, "ConnectedService", "fixture-http")), default);
            Assert.IsNotNull(value);
            CollectionAssert.AreEqual(System.Text.Encoding.UTF8.GetBytes(initialPassword), value.Value.AccessValue().ToArray());
        }
    }

    private sealed class BootstrapMasterKey : IMasterKeyProvider, IMasterKeyInitializer
    {
        private readonly byte[] key = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        public bool Available { get; set; } = true;
        public int Initializations { get; private set; }
        public ValueTask<byte[]> GetKeyAsync(CancellationToken cancellationToken = default) => Available
            ? ValueTask.FromResult(key.ToArray()) : throw new InvalidOperationException("Fixture key unavailable.");
        public Task<SecretVaultInitializationResult> InitializeAsync(CancellationToken cancellationToken = default)
        {
            Initializations++;
            return Task.FromResult(new SecretVaultInitializationResult(false, "fixture-key"));
        }
    }

    [TestMethod]
    [Timeout(90_000)]
    public async Task AProtectedTypeRejectsAnOrdinaryInitializerBeforeItsValueCanAffectThePreviewDigest()
    {
        using var directory = new TemporaryDirectory();
        await WriteCompleteFixtureAsync(directory.Path);
        var serviceFile = Path.Combine(directory.Path, "complete/30-http.yaml");
        await File.WriteAllTextAsync(serviceFile, (await File.ReadAllTextAsync(serviceFile)).Replace("name: http-endpoint", "name: fixture-protected", StringComparison.Ordinal));
        await using var factory = CompleteFactory(directory.Path, new(), Guid.NewGuid().ToString("N"));
        using var client = factory.CreateClient();
        var actor = await factory.Services.GetRequiredService<ILocalEnvironmentBootstrapper>().EnsureInitializedAsync(default);
        await using var scope = factory.Services.CreateAsyncScope();
        using (scope.ServiceProvider.GetRequiredService<IRequestContextScopeFactory>().PushSystem())
        {
            var types = scope.ServiceProvider.GetRequiredService<ConnectedServiceTypeManagementService>();
            var original = (await types.GetExactAsync(ResourceScopeRef.Instance, ResourceNamespace.Default, "http-endpoint", default))!.Value;
            var version = original.Definition.Versions.Single();
            _ = await types.CreateAsync(new ConnectedServiceTypeResource
            {
                ApiVersion = original.ApiVersion,
                Kind = original.Kind,
                ScopeRef = ResourceScopeRef.Instance,
                Metadata = new() { Name = "fixture-protected" },
                Definition = original.Definition with
                {
                    Versions = [version with
                    {
                        Inputs = version.Inputs.Select(input => input with { Protection = ConnectedServiceValueProtection.Secured }).ToArray(),
                        Exports = version.Exports.Select(export => export with { Protection = ConnectedServiceValueProtection.Secured }).ToArray(),
                        OperationDiscovery = null
                    }]
                }
            }, default);
        }
        var configuration = factory.Services.GetRequiredService<IConfiguration>();
        var management = scope.ServiceProvider.GetRequiredService<BootstrapProfileManagementService>();
        configuration["Agentstration:Bootstrap:Parameters:FixtureUrl"] = Guid.NewGuid().ToString("N");
        var first = await management.PreviewAsync(new(["complete"]), actor.PrincipalId, default);
        configuration["Agentstration:Bootstrap:Parameters:FixtureUrl"] = Guid.NewGuid().ToString("N");
        var second = await management.PreviewAsync(new(["complete"]), actor.PrincipalId, default);
        Assert.IsFalse(first.CanApply);
        Assert.IsFalse(second.CanApply);
        Assert.AreEqual(first.Digest, second.Digest, "A secured value must not enter an ordinary configuration fingerprint, including on invalid preview.");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [Timeout(90_000)]
    public async Task DurableDiscoveryWaitCanReconnectOrCancelWithoutCreatingPendingConsumers(bool cancel)
    {
        using var directory = new TemporaryDirectory();
        await WriteCompleteFixtureAsync(directory.Path);
        var gateway = new BootstrapDiscoveryGateway { AllowCompletion = false };
        await using var factory = CompleteFactory(directory.Path, gateway, Guid.NewGuid().ToString("N"));
        using var client = factory.CreateClient();
        var actor = await factory.Services.GetRequiredService<ILocalEnvironmentBootstrapper>().EnsureInitializedAsync(default);
        await using var scope = factory.Services.CreateAsyncScope();
        var management = scope.ServiceProvider.GetRequiredService<BootstrapProfileManagementService>();
        using (scope.ServiceProvider.GetRequiredService<IRequestContextScopeFactory>().PushSystem())
            gateway.Type = (await scope.ServiceProvider.GetRequiredService<ConnectedServiceTypeManagementService>()
                .GetExactAsync(ResourceScopeRef.Instance, ResourceNamespace.Default, "http-endpoint", default))!.Value.Definition.Versions.Single().OperationDiscovery!.ProducedTypes.Single();
        var selection = new BootstrapProfileSelection(["complete"]);
        var preview = await management.PreviewAsync(selection, actor.PrincipalId, default);
        var application = await management.StartAsync(new(["complete"], preview.Digest, RequestId: Guid.NewGuid()), actor.PrincipalId, default);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        BootstrapApplicationResource observed;
        do
        {
            await Task.Delay(50, deadline.Token);
            observed = (await management.GetApplicationAsync(application.Name, actor.PrincipalId, deadline.Token))!;
        } while (observed.Definition.Operation is null && observed.Definition.Status == BootstrapApplicationStatus.Running);
        Assert.AreEqual(BootstrapApplicationStatus.Running, observed.Definition.Status);
        Assert.AreEqual("waiting_worker", observed.Definition.Operation!.Stage);
        Assert.IsNotNull(observed.Definition.Operation.FlowRunId);
        Assert.IsFalse(observed.Definition.Resources.Any(item => item.Kind == "DataSource"));
        using var reconnectedClient = factory.CreateClient();
        var reconnected = await reconnectedClient.GetFromJsonAsync<BootstrapApplicationResource>("/api/bootstrap/applications/" + application.Name, deadline.Token);
        Assert.AreEqual(application.Name, reconnected!.Name);
        if (cancel) _ = await management.CancelAsync(application.Name, actor.PrincipalId, deadline.Token);
        else gateway.AllowCompletion = true;
        do
        {
            await Task.Delay(50, deadline.Token);
            observed = (await management.GetApplicationAsync(application.Name, actor.PrincipalId, deadline.Token))!;
        } while (observed.Definition.Status == BootstrapApplicationStatus.Running);
        Assert.AreEqual(cancel ? BootstrapApplicationStatus.Cancelled : BootstrapApplicationStatus.Succeeded, observed.Definition.Status);
        Assert.AreEqual(cancel, gateway.Cancelled);
        Assert.AreEqual(1, gateway.Starts);
        if (cancel) Assert.IsFalse(observed.Definition.Resources.Any(item => item.Kind == "DataSource"));
        else
        {
            using (scope.ServiceProvider.GetRequiredService<IRequestContextScopeFactory>().PushSystem())
            {
                var discoveryService = scope.ServiceProvider.GetRequiredService<ConnectedServiceOperationDiscoveryService>();
                var operation = (await discoveryService.ListOperationsAsync(ResourceScopeRef.Instance, "fixture-http", default)).Single();
                var exact = (await discoveryService.GetOperationAsync(ResourceScopeRef.Instance, ResourceNamespace.Default, operation.Name, default))!;
                _ = await scope.ServiceProvider.GetRequiredService<ConnectedServiceOperationService>().SetEnabledAsync(
                    new(ResourceScopeRef.Instance, ResourceNamespace.Default, operation.Kind, operation.Name), false, exact.ETag, default);
            }
            var disabledPreview = await management.PreviewAsync(selection, actor.PrincipalId, default);
            Assert.IsFalse(disabledPreview.CanApply);
            Assert.IsTrue(disabledPreview.Resources.Any(item => item.Message == "bootstrap_operation_disabled_by_operator"));
        }
    }

    private static async Task WriteCompleteFixtureAsync(string path)
    {
        var profile = Directory.CreateDirectory(Path.Combine(path, "complete")).FullName;
        await File.WriteAllTextAsync(Path.Combine(profile, "profile.yaml"), Profile("complete", "instance"));
        await File.WriteAllTextAsync(Path.Combine(profile, "10-vault.yaml"), """
            apiVersion: agentstration.io/v1
            kind: Vault
            metadata: {name: fixture-vault}
            definition: {displayName: Fixture vault, providerType: local}
            """);
        await File.WriteAllTextAsync(Path.Combine(profile, "20-secret.yaml"), """
            apiVersion: agentstration.io/v1
            kind: Secret
            metadata: {name: fixture-password}
            definition:
              displayName: Fixture password
              vault: {name: fixture-vault, scopeRef: /instance}
              key: fixture-password
              valueFrom: {configuration: 'Agentstration:Bootstrap:Secrets:FixturePassword'}
            """);
        await File.WriteAllTextAsync(Path.Combine(profile, "30-http.yaml"), """
            apiVersion: agentstration.io/v1
            kind: ConnectedService
            metadata: {name: fixture-http}
            definition:
              displayName: Fixture HTTP
              type: {resourceRef: {name: http-endpoint, version: 1.0.0}}
              options: {}
              bindings:
                - inputId: baseUrl
                  kind: literal
                  literal: {valueFrom: {configuration: 'Agentstration:Bootstrap:Parameters:FixtureUrl', type: string, default: 'https://example.invalid/data?q=unused'}}
            """);
        await File.WriteAllTextAsync(Path.Combine(profile, "40-storage.yaml"), """
            apiVersion: agentstration.io/v1
            kind: Storage
            metadata: {name: fixture-content}
            definition:
              displayName: Fixture content
              type: {resourceRef: {name: local, version: 1.0.0}}
              configuration: {}
              envelope: {list: true, stat: true, read: true, write: true, prefix: '', maximumPageSize: 100, maximumReadBytes: 10485760, maximumWriteBytes: 10485760}
            """);
        await File.WriteAllTextAsync(Path.Combine(profile, "60-discovery.yaml"), """
            apiVersion: agentstration.io/v1
            kind: ConnectedServiceOperationDiscovery
            metadata: {name: fixture-discovery}
            definition:
              connectedService: {resourceRef: {name: fixture-http}}
              operations:
                - name: data
                  selector: {method: GET, endpoint: 'https://example.invalid/data'}
                  enabled: true
            """);
        await File.WriteAllTextAsync(Path.Combine(profile, "70-data.yaml"), """
            apiVersion: agentstration.io/v1
            kind: DataSource
            metadata: {name: fixture-data}
            definition:
              displayName: Fixture data
              dataset:
                storage: {resourceRef: {name: fixture-content}}
                rule: {list: true, stat: true, read: true, write: true, prefix: ''}
                acquisition:
                  operation: {resourceRef: {name: fixture-discovery, output: data}}
                  parameters: {q: test}
            """);
    }

    [TestMethod]
    [Timeout(90_000)]
    public async Task RetryAfterDiscoveryMaterializedCanActivateBeforeValidatingThePendingDataSource()
    {
        using var directory = new TemporaryDirectory();
        await WriteCompleteFixtureAsync(directory.Path);
        var profile = Path.Combine(directory.Path, "complete");
        var prerequisites = Directory.CreateDirectory(Path.Combine(directory.Path, "prerequisites")).FullName;
        await File.WriteAllTextAsync(Path.Combine(prerequisites, "profile.yaml"), Profile("prerequisites", "instance"));
        foreach (var name in new[] { "10-vault.yaml", "20-secret.yaml", "30-http.yaml", "40-storage.yaml" })
            File.Copy(Path.Combine(profile, name), Path.Combine(prerequisites, name));
        var gateway = new BootstrapDiscoveryGateway();
        await using var factory = CompleteFactory(directory.Path, gateway, Guid.NewGuid().ToString("N"));
        using var client = factory.CreateClient();
        var actor = await factory.Services.GetRequiredService<ILocalEnvironmentBootstrapper>().EnsureInitializedAsync(default);
        await using var scope = factory.Services.CreateAsyncScope();
        var management = scope.ServiceProvider.GetRequiredService<BootstrapProfileManagementService>();
        var preliminary = await management.PreviewAsync(new(["prerequisites"]), actor.PrincipalId, default);
        var created = await management.ApplyAsync(new(["prerequisites"]), preliminary.Digest, actor.PrincipalId, default);
        Assert.AreEqual(BootstrapApplicationStatus.Succeeded, created.Definition.Status);
        var operational = await scope.ServiceProvider.GetRequiredService<IOperationalTopologyProvisioner>().GetInstanceOperationsWorkspaceAsync(default);
        using (scope.ServiceProvider.GetRequiredService<IRequestContextScopeFactory>().PushInstance(new(actor.PrincipalId, operational.TenantId, operational.Id)))
        {
            gateway.Type = (await scope.ServiceProvider.GetRequiredService<ConnectedServiceTypeManagementService>()
                .GetExactAsync(ResourceScopeRef.Instance, ResourceNamespace.Default, "http-endpoint", default))!.Value.Definition.Versions.Single().OperationDiscovery!.ProducedTypes.Single();
            var discoveries = scope.ServiceProvider.GetRequiredService<ConnectedServiceOperationDiscoveryService>();
            var discovery = await discoveries.StartAsync(ResourceScopeRef.Instance, "fixture-http", null, "materialized-before-crash", default);
            _ = await discoveries.GetAsync(ResourceNamespace.Default, discovery.Value.Name, default);
            Assert.IsFalse((await discoveries.ListOperationsAsync(ResourceScopeRef.Instance, "fixture-http", default)).Single().Enabled);
        }
        var preview = await management.PreviewAsync(new(["complete"]), actor.PrincipalId, default);
        Assert.IsTrue(preview.CanApply, string.Join('\n', preview.Resources.Select(item => item.Message)));
        Assert.IsTrue(preview.Resources.Single(item => item.Kind == "DataSource").ValidationDeferred);
        var application = await management.ApplyAsync(new(["complete"]), preview.Digest, actor.PrincipalId, default);
        Assert.AreEqual(BootstrapApplicationStatus.Succeeded, application.Definition.Status);
        Assert.AreEqual(1, gateway.Starts, "A materialized compatible discovery must be reused.");
    }

    private sealed class BootstrapDiscoveryGateway : IConnectedServiceOperationDiscoveryFlowGateway
    {
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
        public ConnectedServiceOperationTypeReference Type { get; set; } = null!;
        public int Starts { get; private set; }
        public bool AllowCompletion { get; set; } = true;
        public bool Cancelled { get; private set; }
        private ConnectedServiceDiscoveryFlowRunSnapshot? snapshot;
        public Task<ConnectedServiceDiscoveryFlowRunSnapshot> StartAsync(ConnectedServiceDiscoveryFlowRunRequest request, CancellationToken cancellationToken)
        {
            Starts++;
            var output = JsonSerializer.SerializeToElement(new ConnectedServiceOperationDiscoveryManifest
            { Operations = [BuiltinHttpOperation.Discover("https://example.invalid/data?q=unused", Type)] }, Json);
            snapshot = new(request.RunId, ConnectedServiceOperationDiscoveryState.Succeeded, output, null, null, TimeProvider.System.GetUtcNow());
            return Task.FromResult(Current()!);
        }
        private ConnectedServiceDiscoveryFlowRunSnapshot? Current() => snapshot is null || AllowCompletion ? snapshot
            : snapshot with { State = ConnectedServiceOperationDiscoveryState.Pending, Output = null, CompletedAt = null };
        public Task<ConnectedServiceDiscoveryFlowRunSnapshot?> GetAsync(Guid workspaceId, string runId, Guid tenantId, Guid principalId, CancellationToken cancellationToken) => Task.FromResult(Current());
        public Task<ConnectedServiceDiscoveryFlowRunSnapshot> CancelAsync(Guid workspaceId, string runId, Guid tenantId, Guid principalId, CancellationToken cancellationToken)
        {
            Cancelled = true;
            AllowCompletion = true;
            snapshot = snapshot! with { State = ConnectedServiceOperationDiscoveryState.Cancelled };
            return Task.FromResult(snapshot);
        }
    }
}
