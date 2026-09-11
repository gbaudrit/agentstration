using Agentstration.Secrets;
using System.Net;
using System.Net.Http.Json;
using Agentstration.Management.Abstractions;
using Agentstration.Management.Contracts;
using Agentstration.Resources;
using Agentstration.Web.Components.Pages;
using Agentstration.Web.Components.State;
using Agentstration.Web.Console;
using Bunit;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

[TestClass]
[DoNotParallelize]
public sealed class SourceRegistryConsoleTests
{
    [TestMethod]
    public void RegistryListDistinguishesOfficialRegistrationFromLocalTrust()
    {
        using var culture = new TestCultureScope("en-US");
        using var context = Context(new StubSourceRegistriesClient(registries:
        [
            Registry(SourceRegistryWellKnown.OfficialName, "Agentstration Registry", SourceRegistryTrustPolicy.Authoritative, SourceRegistryObservedStatus.Fresh),
            Registry("private-team", "Private team", SourceRegistryTrustPolicy.Trusted, SourceRegistryObservedStatus.RefreshFailed, privateEndpoint: true)
        ]));

        var rendered = context.Render<SourceRegistries>();

        rendered.WaitForAssertion(() =>
        {
            StringAssert.Contains(rendered.Markup, "Well-known official registration");
            StringAssert.Contains(rendered.Markup, "Authoritative");
            StringAssert.Contains(rendered.Markup, "Private");
            StringAssert.Contains(rendered.Markup, "Refresh failed");
            Assert.HasCount(2, rendered.FindAll("button[role='switch']"));
        });
    }

    [TestMethod]
    public void DiscoveryExplainsIndependentEvidenceAndPinsExactSelection()
    {
        using var culture = new TestCultureScope("en-US");
        var observation = Observation();
        var discovered = new SourceRegistryDiscoverySource("contoso", "assistants", "Contoso assistants", "Reusable assistants",
        [
            new("2026.09", false, SourceVerificationStatus.Verified, "source_version_verified", [observation]),
            new("2026.08", true, SourceVerificationStatus.Conflict, "source_registry_manifest_digest_conflict",
                [observation with { Selection = observation.Selection with { Version = "2026.08" }, ManifestDigest = "sha256:other" }])
        ]);
        using var context = Context(new StubSourceRegistriesClient(
            registries: [Registry("contoso", "Contoso Registry", SourceRegistryTrustPolicy.Trusted, SourceRegistryObservedStatus.Fresh)],
            page: new([discovered], 1, 1, 0, 25)));
        context.Services.AddSingleton<IResourceScopeInventoryClient>(new StubScopeInventoryClient());

        var rendered = context.Render<SourceRegistryDiscovery>();

        rendered.WaitForAssertion(() =>
        {
            StringAssert.Contains(rendered.Markup, "Registry origin trust");
            StringAssert.Contains(rendered.Markup, "Publisher evidence");
            StringAssert.Contains(rendered.Markup, "SourceVersion verification");
            StringAssert.Contains(rendered.Markup, "Snapshot verification");
            StringAssert.Contains(rendered.Markup, "Registries disagree on the manifest digest");
            Assert.HasCount(1, rendered.FindAll("button").Where(button => button.TextContent == "Select exact version" && button.HasAttribute("disabled")));
        });
        rendered.FindAll("button").Single(button => button.TextContent == "Select exact version" && !button.HasAttribute("disabled")).Click();
        rendered.WaitForAssertion(() =>
        {
            StringAssert.Contains(rendered.Markup, "Confirm exact Source import");
            StringAssert.Contains(rendered.Markup, observation.Selection.ObservationId.ToString());
            StringAssert.Contains(rendered.Markup, observation.ManifestDigest);
            StringAssert.Contains(rendered.Markup, observation.Selection.CatalogName);
        });
    }

    [TestMethod]
    public async Task ApiClientSendsIfMatchForRegistryUpdates()
    {
        var view = Registry("contoso", "Contoso Registry", SourceRegistryTrustPolicy.Trusted, SourceRegistryObservedStatus.Fresh);
        var handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(view),
            Headers = { ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"next\"") }
        });
        var client = new SourceRegistriesApiClient(new HttpClient(handler) { BaseAddress = new("https://console.test/") });

        _ = await client.UpdateRegistryAsync("contoso", new(view.Registration.Definition), "\"current\"", CancellationToken.None);

        Assert.AreEqual("\"current\"", handler.IfMatch);
        Assert.AreEqual(HttpMethod.Put, handler.Method);
    }

    [TestMethod]
    public void RegistryPagesRequirePlatformAdministratorPolicy()
    {
        foreach (var component in new[] { typeof(SourceRegistries), typeof(SourceRegistryDiscovery) })
        {
            var authorization = component.GetCustomAttributes(typeof(AuthorizeAttribute), true).Cast<AuthorizeAttribute>().Single();
            Assert.AreEqual("agentstration:platform-admin", authorization.Policy);
        }
    }

    [TestMethod]
    public void NewRegistryRouteRendersTheEditorInsteadOfTreatingNewAsARegistryName()
    {
        using var culture = new TestCultureScope("en-US");
        using var context = Context(new StubSourceRegistriesClient(), new StubSecretsClient([]));
        context.Services.GetRequiredService<NavigationManager>().NavigateTo("/settings/source-registries/new");

        var cut = context.Render<SourceRegistries>(parameters => parameters.Add(component => component.Name, "new"));

        cut.WaitForAssertion(() => Assert.AreEqual("Add a Source registry", cut.Find("h1").TextContent));
    }

    [TestMethod]
    public void PrivateRegistryEditorOffersOnlyInstanceSecretReferences()
    {
        using var culture = new TestCultureScope("en-US");
        using var context = Context(new StubSourceRegistriesClient(), new StubSecretsClient(
        [
            Secret("instance-token", "Instance token", ResourceScopeRef.Instance),
            Secret("workspace-token", "Workspace token", ResourceScopeRef.Workspace(Guid.Parse("33333333-3333-3333-3333-333333333333")))
        ]));
        context.Services.GetRequiredService<NavigationManager>().NavigateTo("/settings/source-registries/new");

        var rendered = context.Render<SourceRegistries>();
        rendered.WaitForAssertion(() => Assert.AreEqual("Add a Source registry", rendered.Find("h1").TextContent));
        rendered.FindAll("select")[1].Change(SourceRegistryAuthenticationMode.StaticBearer.ToString());

        rendered.WaitForAssertion(() =>
        {
            StringAssert.Contains(rendered.Markup, "Instance token");
            Assert.IsFalse(rendered.Markup.Contains("Workspace token", StringComparison.Ordinal));
            StringAssert.Contains(rendered.Markup, "The Secret value remains write-only.");
        });
    }

    private static BunitContext Context(ISourceRegistriesClient client, ISecretsClient? secrets = null)
    {
        var context = new BunitContext();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton(client);
        context.Services.AddSingleton(secrets ?? new StubSecretsClient());
        context.Services.AddSingleton(new NotificationState());
        context.Services.AddSingleton(TimeProvider.System);
        return context;
    }

    private static SourceRegistryRegistrationView Registry(string name, string displayName, SourceRegistryTrustPolicy trust, SourceRegistryObservedStatus status, bool privateEndpoint = false)
    {
        var uid = Guid.NewGuid();
        var registration = new SourceRegistryRegistrationResource
        {
            Uid = uid,
            ApiVersion = ManagementApiVersions.CoreV1,
            Kind = ResourceKinds.SourceRegistryRegistration,
            Metadata = new() { Name = name },
            ScopeRef = ResourceScopeRef.Instance,
            ETag = "\"etag\"",
            Definition = new()
            {
                DisplayName = displayName,
                IndexUrl = new(privateEndpoint ? "https://registry.internal/v1/index.json" : "https://registry.agentstration.io/v1/index.json"),
                Enabled = true,
                TrustPolicy = trust,
                AuthenticationMode = privateEndpoint ? SourceRegistryAuthenticationMode.StaticBearer : SourceRegistryAuthenticationMode.None,
                Credential = privateEndpoint ? new("registry-token", ResourceScopeRef.Instance, ResourceNamespace.Default) : null,
                EndpointPolicy = new() { AllowPrivateNetwork = privateEndpoint },
                RefreshPolicy = new() { PeriodicEnabled = true }
            }
        };
        var observed = new SourceRegistryObservedStateResource
        {
            ApiVersion = ManagementApiVersions.CoreV1,
            Kind = ResourceKinds.SourceRegistryObservedState,
            Metadata = new() { Name = name },
            ScopeRef = ResourceScopeRef.Instance,
            Definition = new()
            {
                RegistrationUid = uid,
                Status = status,
                LastAttemptedAt = ObservedAt,
                LastSuccessfulRefreshAt = status == SourceRegistryObservedStatus.Fresh ? ObservedAt : null,
                LastOutcome = status == SourceRegistryObservedStatus.Fresh ? SourceRegistryRefreshOutcome.Succeeded : SourceRegistryRefreshOutcome.Unavailable,
                LastErrorMessage = status == SourceRegistryObservedStatus.RefreshFailed ? "Connection failed" : null
            }
        };
        return new(registration, observed);
    }

    private static SourceRegistryDiscoveryObservation Observation() => new()
    {
        Selection = new(Guid.Parse("11111111-1111-1111-1111-111111111111"), Guid.Parse("22222222-2222-2222-2222-222222222222"), "agentstration-0.2", "contoso", "assistants", "2026.09"),
        RegistrationName = "contoso",
        RegistrationDisplayName = "Contoso Registry",
        TrustPolicy = SourceRegistryTrustPolicy.Trusted,
        Freshness = SourceRegistryObservedStatus.Fresh,
        OriginClassification = SourceRegistryOriginClassification.External,
        IndexDigest = "sha256:index",
        CatalogDigest = "sha256:catalog",
        Compatibility = new(),
        FetchedAt = ObservedAt,
        Publisher = new() { Name = "contoso", DisplayName = "Contoso", Status = SourceRegistryPublisherStatuses.Verified },
        ManifestUrl = "sources/contoso-assistants-2026.09.yaml",
        ManifestDigest = "sha256:manifest",
        IsCatalogLatest = true
    };

    private static readonly DateTimeOffset ObservedAt = new(2026, 9, 10, 10, 0, 0, TimeSpan.Zero);

    private static SecretResponse Secret(string name, string displayName, ResourceScopeRef scopeRef) => new(new SecretResource
    {
        ApiVersion = ManagementApiVersions.CoreV1,
        Kind = ResourceKinds.Secret,
        Metadata = new() { Name = name },
        ScopeRef = scopeRef,
        Definition = new() { DisplayName = displayName, Vault = new("local", scopeRef), Key = name }
    }, "Configured", true);

    private sealed class StubSourceRegistriesClient(IReadOnlyList<SourceRegistryRegistrationView>? registries = null, SourceRegistryDiscoveryPage? page = null) : ISourceRegistriesClient
    {
        public Task<IReadOnlyList<SourceRegistryRegistrationView>> GetRegistriesAsync(CancellationToken cancellationToken) => Task.FromResult(registries ?? []);
        public Task<SourceRegistryDiscoveryPage> SearchAsync(SourceRegistryDiscoveryQuery query, CancellationToken cancellationToken) => Task.FromResult(page ?? new([], 0, 0, query.Skip, query.Take));
        public Task<IReadOnlyList<SourceRegistryDiscoveryPublisher>> GetPublishersAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<SourceRegistryDiscoveryPublisher>>([]);
        public Task<ResourceSnapshot<SourceRegistryRegistrationView>> GetRegistryAsync(string name, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<SourceRegistryRegistrationView>> CreateRegistryAsync(CreateSourceRegistryRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<SourceRegistryRegistrationView>> UpdateRegistryAsync(string name, PutSourceRegistryRequest request, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeleteRegistryAsync(string name, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<SourceRegistryRegistrationView>> RefreshRegistryAsync(string name, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<SourceRegistryRefreshRecordResource>> GetRefreshesAsync(string name, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<SourceRegistryOriginTrustView> GetOriginTrustAsync(string name, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<SourceRegistryDiscoverySource?> GetSourceAsync(string publisher, string sourceName, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<SourceImportResult> ImportAsync(SourceRegistryObservationSelection selection, ResourceScopeRef? scopeRef, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class StubScopeInventoryClient : IResourceScopeInventoryClient
    {
        public Task<ResourceScopeInventoryResponse> GetAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<ResourceScopeTargetResponse>> GetTargetsAsync(string kind, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ResourceScopeTargetResponse>>([new(ResourceScopeRef.Instance, ResourceScopeKind.Instance, "Instance", true)]);
    }

    private sealed class StubSecretsClient(IReadOnlyList<SecretResponse>? secrets = null) : ISecretsClient
    {
        public Task<IReadOnlyList<SecretResponse>> GetSecretsAsync(CancellationToken cancellationToken) => Task.FromResult(secrets ?? []);
        public Task<IReadOnlyList<VaultResponse>> GetVaultsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<VaultResponse>> GetVaultAsync(string name, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<VaultResource>> CreateVaultAsync(CreateVaultRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<VaultResource>> UpdateVaultAsync(string name, PutVaultRequest request, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeleteVaultAsync(string name, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<VaultInitializationResponse> InitializeVaultAsync(string name, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<SecretResponse>> GetSecretAsync(string name, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<SecretResource>> CreateSecretAsync(CreateSecretRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<SecretResource>> UpdateSecretAsync(string name, PutSecretRequest request, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SetSecretValueAsync(string name, string value, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeleteSecretValueAsync(string name, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeleteSecretAsync(string name, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<SecretUsagesResponse> GetSecretUsagesAsync(string name, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class RecordingHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        public string? IfMatch { get; private set; }
        public HttpMethod? Method { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            IfMatch = request.Headers.IfMatch.SingleOrDefault()?.ToString(); Method = request.Method;
            return Task.FromResult(response);
        }
    }
}
