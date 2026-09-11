using Agentstration.Secrets;
using Agentstration.ResourceManagement;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using Agentstration.Management.Abstractions;
using Agentstration.Management.Contracts;
using Agentstration.Management.Core;
using Agentstration.ResourceManagement.Storage.Sqlite;
using Agentstration.Resources;
using Agentstration.Web.Api.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agentstration.Management.Tests;

[TestClass]
public sealed class SourceRegistryManagementTests
{
    [TestMethod]
    public async Task OfficialApiIsSeededOfflineAndMutationsRequirePlatformAdministrator()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.UseEnvironment("Testing"));
        using var client = factory.CreateClient();

        var forbidden = await client.GetAsync("/api/sourceregistries");
        Assert.AreEqual(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden,
            (await client.GetAsync("/api/sourceregistries/discovery")).StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden,
            (await client.GetAsync($"/api/sourceregistries/{SourceRegistryWellKnown.OfficialName}/trust")).StatusCode);
        var context = await client.GetFromJsonAsync<ConsoleContextView>("/api/identity/context");
        Assert.IsNotNull(context);
        await factory.Services.GetRequiredService<IIdentityStore>().AddPlatformAdministratorAsync(
            new PlatformAdministrator(context.Context.PrincipalId, DateTimeOffset.UtcNow),
            default);

        var list = await client.GetFromJsonAsync<ValueResponse<SourceRegistryRegistrationView>>("/api/sourceregistries");
        var official = list!.Value.Single();
        Assert.AreEqual(SourceRegistryWellKnown.OfficialIndexUrl, official.Registration.Definition.IndexUrl.AbsoluteUri);
        Assert.AreEqual(SourceRegistryObservedStatus.NeverFetched, official.Observed.Definition.Status);
        var discovery = await client.GetFromJsonAsync<SourceRegistryDiscoveryPage>("/api/sourceregistries/discovery");
        Assert.IsNotNull(discovery);
        Assert.AreEqual(0, discovery.Total);
        var originTrust = await client.GetFromJsonAsync<SourceRegistryOriginTrustView>(
            $"/api/sourceregistries/{SourceRegistryWellKnown.OfficialName}/trust");
        Assert.IsNotNull(originTrust);
        Assert.AreEqual(SourceRegistryOriginClassification.Official, originTrust.Classification);
        var sourceTrust = await client.GetFromJsonAsync<SourceRegistrySourceTrustView>(
            "/api/sourceregistries/trust/sources/agentstration/sample/versions/1");
        Assert.IsNotNull(sourceTrust);
        Assert.AreEqual(SourceVerificationStatus.Unverified, sourceTrust.VersionStatus);

        using var update = new HttpRequestMessage(HttpMethod.Put, $"/api/sourceregistries/{SourceRegistryWellKnown.OfficialName}")
        {
            Content = JsonContent.Create(new PutSourceRegistryRequest(
                official.Registration.Definition with
                {
                    IndexUrl = new Uri("https://registry.example/v1/index.json"),
                    Enabled = false
                }))
        };
        update.Headers.TryAddWithoutValidation("If-Match", official.Registration.ETag);
        using var response = await client.SendAsync(update);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        var changed = await response.Content.ReadFromJsonAsync<SourceRegistryRegistrationView>();
        Assert.IsNotNull(changed);
        Assert.IsFalse(changed.Registration.Definition.Enabled);
        Assert.AreEqual(SourceRegistryObservedStatus.Disabled, changed.Observed.Definition.Status);

        using var create = await client.PostAsJsonAsync("/api/sourceregistries", new CreateSourceRegistryRequest(
            "community",
            new SourceRegistryRegistrationProperties
            {
                DisplayName = "Community registry",
                IndexUrl = new Uri("https://registry.community.example/v1/index.json"),
                TrustPolicy = SourceRegistryTrustPolicy.Untrusted
            }));
        Assert.AreEqual(HttpStatusCode.Created, create.StatusCode, await create.Content.ReadAsStringAsync());
        var community = await create.Content.ReadFromJsonAsync<SourceRegistryRegistrationView>();
        Assert.IsNotNull(community);

        using var put = new HttpRequestMessage(HttpMethod.Put, "/api/sourceregistries/community")
        {
            Content = JsonContent.Create(new PutSourceRegistryRequest(
                community.Registration.Definition with { DisplayName = "Community registry updated", Enabled = false }))
        };
        put.Headers.TryAddWithoutValidation("If-Match", community.Registration.ETag);
        using var putResponse = await client.SendAsync(put);
        Assert.AreEqual(HttpStatusCode.OK, putResponse.StatusCode, await putResponse.Content.ReadAsStringAsync());
        var updatedCommunity = await putResponse.Content.ReadFromJsonAsync<SourceRegistryRegistrationView>();
        Assert.IsNotNull(updatedCommunity);

        using var stale = new HttpRequestMessage(HttpMethod.Put, "/api/sourceregistries/community")
        {
            Content = JsonContent.Create(new PutSourceRegistryRequest(community.Registration.Definition))
        };
        stale.Headers.TryAddWithoutValidation("If-Match", community.Registration.ETag);
        using var staleResponse = await client.SendAsync(stale);
        Assert.AreEqual(HttpStatusCode.Conflict, staleResponse.StatusCode);

        using var delete = new HttpRequestMessage(HttpMethod.Delete, "/api/sourceregistries/community");
        delete.Headers.TryAddWithoutValidation("If-Match", updatedCommunity.Registration.ETag);
        using var deleteResponse = await client.SendAsync(delete);
        Assert.AreEqual(HttpStatusCode.NoContent, deleteResponse.StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, (await client.GetAsync("/api/sourceregistries/community")).StatusCode);

        var audits = await factory.Services.GetRequiredService<ISecurityAuditStore>().ListLatestAsync(20, default);
        Assert.IsTrue(audits.Any(value => value.Action == SecurityAuditActions.SourceRegistryConfigurationUpdated
            && value.ActorPrincipalId == context.Context.PrincipalId));
        Assert.IsTrue(audits.Any(value => value.Action == SecurityAuditActions.SourceRegistryCreated));
        Assert.IsTrue(audits.Any(value => value.Action == SecurityAuditActions.SourceRegistryDeleted));
    }

    [TestMethod]
    public async Task EndpointAndCredentialPoliciesAreExplicitAndInstanceScoped()
    {
        using var fixture = new Fixture();
        var privateFailure = await Assert.ThrowsAsync<SourceRegistryOperationException>(() => fixture.Service.CreateAsync(
            "private-denied",
            Registration("https://127.0.0.1/v1/index.json"),
            default));
        Assert.AreEqual("source_registry_endpoint_policy_denied", privateFailure.Code);

        var httpFailure = await Assert.ThrowsAsync<SourceRegistryOperationException>(() => fixture.Service.CreateAsync(
            "http-denied",
            Registration("http://registry.internal/v1/index.json"),
            default));
        Assert.AreEqual("source_registry_index_url_invalid", httpFailure.Code);

        _ = await fixture.Store.PutExactAsync(ResourceScopeRef.Instance, new VaultResource
        {
            ApiVersion = ManagementApiVersions.CoreV1,
            Kind = ResourceKinds.Vault,
            Metadata = new ResourceMetadata { Name = "registry-vault" },
            ScopeRef = ResourceScopeRef.Instance,
            Definition = new VaultProperties { DisplayName = "Registry vault", ProviderType = "local" }
        }, null, true, default);
        _ = await fixture.Store.PutExactAsync(ResourceScopeRef.Instance, new SecretResource
        {
            ApiVersion = ManagementApiVersions.CoreV1,
            Kind = ResourceKinds.Secret,
            Metadata = new ResourceMetadata { Name = "registry-token" },
            ScopeRef = ResourceScopeRef.Instance,
            Definition = new SecretProperties
            {
                DisplayName = "Registry token",
                Key = "registry-token",
                Vault = new ResourceReference("registry-vault", ResourceScopeRef.Instance)
            }
        }, null, true, default);

        var scopeFailure = await Assert.ThrowsAsync<SourceRegistryOperationException>(() => fixture.Service.CreateAsync(
            "wrong-scope",
            Registration("https://registry.example/v1/index.json") with
            {
                AuthenticationMode = SourceRegistryAuthenticationMode.StaticBearer,
                Credential = new ResourceReference("registry-token")
            },
            default));
        Assert.AreEqual("source_registry_credential_scope_invalid", scopeFailure.Code);

        var created = await fixture.Service.CreateAsync(
            "private-registry",
            Registration("http://127.0.0.1/v1/index.json") with
            {
                EndpointPolicy = new SourceRegistryEndpointPolicy { AllowHttp = true, AllowPrivateNetwork = true },
                AuthenticationMode = SourceRegistryAuthenticationMode.StaticBearer,
                Credential = new ResourceReference("registry-token", ResourceScopeRef.Instance)
            },
            default);
        Assert.AreEqual(SourceRegistryAuthenticationMode.StaticBearer, created.Value.Definition.AuthenticationMode);
        Assert.AreEqual("registry-token", created.Value.Definition.Credential!.Name);
    }

    [TestMethod]
    public void AgentstrationOriginClassificationIsBoundarySafeAndInformational()
    {
        var registration = RegistrationResource("community", "https://registry.agentstration.io/v1/index.json");

        Assert.AreEqual(SourceRegistryOriginClassification.AgentstrationOwned,
            SourceRegistryTrustEvaluationService.ClassifyOrigin(registration, registration.Definition.IndexUrl));
        Assert.IsTrue(SourceRegistryTrustEvaluationService.IsAgentstrationOwnedHttpsOrigin(new("https://agentstration.io/v1/index.json")));
        Assert.IsTrue(SourceRegistryTrustEvaluationService.IsAgentstrationOwnedHttpsOrigin(new("https://a.b.agentstration.io/v1/index.json")));
        Assert.IsFalse(SourceRegistryTrustEvaluationService.IsAgentstrationOwnedHttpsOrigin(new("https://agentstration.io.example/v1/index.json")));
        Assert.IsFalse(SourceRegistryTrustEvaluationService.IsAgentstrationOwnedHttpsOrigin(new("https://notagentstration.io/v1/index.json")));
        Assert.IsFalse(SourceRegistryTrustEvaluationService.IsAgentstrationOwnedHttpsOrigin(new("https://xn--agentstratin-f7a.io/v1/index.json")));
        Assert.IsFalse(SourceRegistryTrustEvaluationService.IsAgentstrationOwnedHttpsOrigin(new("https://agentstratíon.io/v1/index.json")));
        Assert.IsTrue(SourceRegistryTrustEvaluationService.IsAgentstrationOwnedHttpsOrigin(new("https://registry.agentstration.io:443/v1/index.json")));
        Assert.IsFalse(SourceRegistryTrustEvaluationService.IsAgentstrationOwnedHttpsOrigin(new("https://registry.agentstration.io:8443/v1/index.json")));
        Assert.IsFalse(SourceRegistryTrustEvaluationService.IsAgentstrationOwnedHttpsOrigin(new("http://registry.agentstration.io/v1/index.json")));
        Assert.IsFalse(Uri.TryCreate("https://registry..agentstration.io/v1/index.json", UriKind.Absolute, out _));

        var official = registration with
        {
            Uid = Guid.NewGuid(),
            Metadata = new ResourceMetadata
            {
                Name = SourceRegistryWellKnown.OfficialName,
                Annotations = new Dictionary<string, string> { [ResourceProvenanceAnnotations.BuiltIn] = "true" }
            },
            Definition = registration.Definition with { IndexUrl = new("https://registry.example/v1/index.json") }
        };
        Assert.AreEqual(SourceRegistryOriginClassification.Official,
            SourceRegistryTrustEvaluationService.ClassifyOrigin(official, official.Definition.IndexUrl));
        Assert.AreEqual(SourceRegistryOriginClassification.External,
            SourceRegistryTrustEvaluationService.ClassifyOrigin(
                official with { Metadata = official.Metadata with { Annotations = new Dictionary<string, string>() } },
                official.Definition.IndexUrl));
        Assert.AreEqual(SourceRegistryOriginClassification.Internal,
            SourceRegistryTrustEvaluationService.ClassifyOrigin(
                RegistrationResource("private", "https://127.0.0.1/v1/index.json"),
                new("https://127.0.0.1/v1/index.json")));
        Assert.AreEqual(SourceRegistryOriginClassification.AgentstrationOwned,
            SourceRegistryTrustEvaluationService.ClassifyOrigin(
                registration with
                {
                    Definition = registration.Definition with
                    {
                        EndpointPolicy = new SourceRegistryEndpointPolicy { AllowPrivateNetwork = true }
                    }
                },
                registration.Definition.IndexUrl));
    }

    [TestMethod]
    public async Task TrustPolicyReevaluatesPublisherAndExactVersionWithoutRewritingObservationAsync()
    {
        using var fixture = new Fixture();
        var seeded = await fixture.Service.EnsureOfficialAsync(default);
        fixture.EnqueueSuccessfulRefresh("https://registry.agentstration.io/v1");
        var refreshed = await fixture.Service.RefreshOfficialAsync(default);
        var digest = RegistryManifestDigest;

        var trusted = await fixture.Trust.EvaluateSourceAsync("agentstration", "sample", "1", digest, default);

        Assert.AreEqual(SourceRegistryPublisherStatus.Official, trusted.Publisher.EffectiveStatus);
        Assert.AreEqual(SourceVerificationStatus.Verified, trusted.VersionStatus);
        Assert.HasCount(1, trusted.Evidence);
        Assert.AreEqual(refreshed.Observed.Definition.Current!.Id, trusted.Evidence[0].Evidence.ObservationId);
        var verification = new SourceVerificationService(new EmptyVerificationIndex(), [fixture.Trust]);
        var version = SourceVersion(RegistryManifestDigest);
        Assert.AreEqual(SourceVerificationStatus.Verified,
            (await verification.VerifyDefinitionAsync(version, default)).Status);
        Assert.AreEqual(SourceVerificationStatus.Unverified,
            (await fixture.Trust.EvaluateSourceAsync(
                "agentstration", "sample", "1", $"sha256:{new string('f', 64)}", default)).VersionStatus);

        _ = await fixture.Service.UpdateAsync(
            SourceRegistryWellKnown.OfficialName,
            seeded.Registration.Definition with { TrustPolicy = SourceRegistryTrustPolicy.Untrusted },
            seeded.Registration.ETag,
            default);
        var downgraded = await fixture.Trust.EvaluateSourceAsync("agentstration", "sample", "1", digest, default);

        Assert.AreEqual(SourceRegistryPublisherStatus.Declared, downgraded.Publisher.EffectiveStatus);
        Assert.AreEqual(SourceVerificationStatus.Unverified, downgraded.VersionStatus);
        Assert.AreEqual(SourceVerificationStatus.Unverified,
            (await verification.VerifyDefinitionAsync(version, default)).Status);
        Assert.AreEqual(refreshed.Observed.Definition.Current.Id, downgraded.Evidence[0].Evidence.ObservationId);
        Assert.IsNotNull(await fixture.Cache.GetAsync(refreshed.Observed.Definition.Current.Id, default));
    }

    [TestMethod]
    public async Task RevocationAndConflictingTrustedDigestsFailClosedWhileRetainingEvidenceAsync()
    {
        using var fixture = new Fixture();
        await AddAndRefreshAsync(fixture, "first", "https://first.example/v1", SourceRegistryTrustPolicy.Trusted,
            SourceRegistryPublisherStatuses.Verified, RegistryManifestDigest);
        await AddAndRefreshAsync(fixture, "second", "https://second.example/v1", SourceRegistryTrustPolicy.Trusted,
            SourceRegistryPublisherStatuses.Verified, $"sha256:{new string('a', 64)}");

        var conflict = await fixture.Trust.EvaluateSourceAsync(
            "agentstration", "sample", "1", RegistryManifestDigest, default);

        Assert.AreEqual(SourceVerificationStatus.Conflict, conflict.VersionStatus);
        Assert.HasCount(2, conflict.Evidence);
        Assert.HasCount(2, conflict.Publisher.Evidence);

        await AddAndRefreshAsync(fixture, "revocations", "https://revoked.example/v1", SourceRegistryTrustPolicy.Trusted,
            SourceRegistryPublisherStatuses.Revoked, RegistryManifestDigest);
        var revoked = await fixture.Trust.EvaluateSourceAsync(
            "agentstration", "sample", "1", RegistryManifestDigest, default);

        Assert.AreEqual(SourceRegistryPublisherStatus.Revoked, revoked.Publisher.EffectiveStatus);
        Assert.AreEqual(SourceVerificationStatus.Revoked, revoked.VersionStatus);
        Assert.HasCount(3, revoked.Evidence);
    }

    [TestMethod]
    public async Task RefreshPolicyRejectsUnboundedAttemptsAndPrematureStaleness()
    {
        using var fixture = new Fixture();
        var attempts = await Assert.ThrowsAsync<SourceRegistryOperationException>(() => fixture.Service.CreateAsync(
            "attempts",
            Registration("https://registry.example/v1/index.json") with
            {
                RefreshPolicy = new SourceRegistryRefreshPolicy { MaximumAttempts = 11 }
            },
            default));
        Assert.AreEqual("source_registry_refresh_policy_invalid", attempts.Code);

        var staleness = await Assert.ThrowsAsync<SourceRegistryOperationException>(() => fixture.Service.CreateAsync(
            "staleness",
            Registration("https://registry.example/v1/index.json") with
            {
                RefreshPolicy = new SourceRegistryRefreshPolicy
                {
                    Interval = TimeSpan.FromHours(2),
                    StaleAfter = TimeSpan.FromHours(1)
                }
            },
            default));
        Assert.AreEqual("source_registry_refresh_policy_invalid", staleness.Code);
    }

    private static SourceRegistryRegistrationProperties Registration(string indexUrl) => new()
    {
        DisplayName = "Test registry",
        IndexUrl = new Uri(indexUrl)
    };

    [TestMethod]
    public async Task SharedSchedulerHonorsManualOnlyBackoffRecoveryAndStaleness()
    {
        var clock = new MutableTimeProvider(DateTimeOffset.Parse(
            "2026-09-10T08:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        using var fixture = new Fixture(clock);
        var created = await fixture.Service.CreateAsync(
            "scheduled",
            Registration("https://registry.example/v1/index.json"),
            default);

        await fixture.Scheduler.RunDueRegistriesAsync(default);
        Assert.IsEmpty(fixture.Documents.Requests);

        var policy = new SourceRegistryRefreshPolicy
        {
            PeriodicEnabled = true,
            Interval = TimeSpan.FromMinutes(10),
            Timeout = TimeSpan.FromSeconds(5),
            MaximumAttempts = 3,
            InitialBackoff = TimeSpan.FromSeconds(30),
            MaximumBackoff = TimeSpan.FromMinutes(2),
            Jitter = TimeSpan.Zero,
            StaleAfter = TimeSpan.FromMinutes(20)
        };
        _ = await fixture.Service.UpdateAsync("scheduled",
            created.Value.Definition with { RefreshPolicy = policy }, created.ETag, default);
        fixture.EnqueueSuccessfulRefresh("https://registry.example/v1");

        await fixture.Scheduler.RunDueRegistriesAsync(default);
        var fresh = await fixture.Service.GetAsync("scheduled", default);
        Assert.AreEqual(SourceRegistryObservedStatus.Fresh, fresh!.Observed.Definition.Status);
        Assert.AreEqual(SourceRegistryRefreshTrigger.Scheduled, fresh.Observed.Definition.LastTrigger);
        Assert.AreEqual(0, fresh.Observed.Definition.ConsecutiveFailures);

        fixture.Documents.Failure = new SourceRetrievalException("source_registry_timeout", "The registry request timed out.");
        clock.Advance(TimeSpan.FromMinutes(10));
        await fixture.Scheduler.RunDueRegistriesAsync(default);
        var failed = await fixture.Service.GetAsync("scheduled", default);
        Assert.AreEqual(SourceRegistryObservedStatus.Stale, failed!.Observed.Definition.Status);
        Assert.AreEqual(1, failed.Observed.Definition.ConsecutiveFailures);
        var requestCount = fixture.Documents.Requests.Count;

        clock.Advance(TimeSpan.FromSeconds(29));
        await fixture.Scheduler.RunDueRegistriesAsync(default);
        Assert.AreEqual(requestCount, fixture.Documents.Requests.Count);

        fixture.Documents.Failure = null;
        fixture.Documents.Enqueue(new(
            new Uri("https://registry.example/v1/index.json"),
            new Uri("https://registry.example/v1/index.json"),
            "index.json", "\"index-v1\"", null, true, null));
        clock.Advance(TimeSpan.FromSeconds(1));
        await fixture.Scheduler.RunDueRegistriesAsync(default);
        var recovered = await fixture.Service.GetAsync("scheduled", default);
        Assert.AreEqual(SourceRegistryObservedStatus.Recovered, recovered!.Observed.Definition.Status);
        Assert.AreEqual(0, recovered.Observed.Definition.ConsecutiveFailures);
        Assert.AreEqual(1, recovered.Observed.Definition.LastRetryCount);
        Assert.IsNotNull(recovered.Observed.Definition.LastRecoveredAt);

        clock.Advance(TimeSpan.FromMinutes(20));
        var stale = await fixture.Service.GetAsync("scheduled", default);
        Assert.AreEqual(SourceRegistryObservedStatus.Stale, stale!.Observed.Definition.Status);
        var history = await fixture.Service.ListRefreshesAsync("scheduled", 10, default);
        Assert.IsTrue(history.All(value => value.Definition.Trigger == SourceRegistryRefreshTrigger.Scheduled));
        Assert.IsTrue(history.All(value => !string.IsNullOrWhiteSpace(value.Definition.CorrelationId)));
    }

    [TestMethod]
    public async Task ScheduledRefreshTimeoutIsBoundedAndRecorded()
    {
        using var fixture = new Fixture();
        _ = await fixture.Service.CreateAsync(
            "timeout",
            Registration("https://registry.example/v1/index.json") with
            {
                RefreshPolicy = new SourceRegistryRefreshPolicy
                {
                    PeriodicEnabled = true,
                    Interval = TimeSpan.FromMinutes(1),
                    Timeout = TimeSpan.FromSeconds(1),
                    Jitter = TimeSpan.Zero
                }
            },
            default);
        fixture.Documents.BlockUntilCancelled = true;

        await fixture.Scheduler.RunDueRegistriesAsync(default);

        var state = await fixture.Service.GetAsync("timeout", default);
        Assert.AreEqual(SourceRegistryObservedStatus.RefreshFailed, state!.Observed.Definition.Status);
        Assert.AreEqual("source_registry_refresh_timeout", state.Observed.Definition.LastErrorCode);
        Assert.AreEqual(1, state.Observed.Definition.ConsecutiveFailures);
        var history = await fixture.Service.ListRefreshesAsync("timeout", 10, default);
        Assert.HasCount(1, history);
        Assert.AreEqual(SourceRegistryRefreshTrigger.Scheduled, history[0].Definition.Trigger);
        Assert.IsGreaterThanOrEqualTo(1000, history[0].Definition.DurationMilliseconds);
    }

    [TestMethod]
    public async Task ScheduledBackoffSurvivesProcessRestart()
    {
        var database = Path.Combine(Path.GetTempPath(), $"agentstration-registry-schedule-{Guid.NewGuid():N}.db");
        var clock = new MutableTimeProvider(DateTimeOffset.Parse(
            "2026-09-10T08:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        try
        {
            await using (var first = await DurableFixture.CreateAsync(database, clock))
            {
                using var system = first.Context.PushSystem();
                _ = await first.Service.CreateAsync(
                    "restart",
                    Registration("https://registry.example/v1/index.json") with
                    {
                        RefreshPolicy = new SourceRegistryRefreshPolicy
                        {
                            PeriodicEnabled = true,
                            Interval = TimeSpan.FromMinutes(10),
                            InitialBackoff = TimeSpan.FromSeconds(30),
                            MaximumBackoff = TimeSpan.FromMinutes(2),
                            Jitter = TimeSpan.Zero
                        }
                    },
                    default);
                first.Documents.Failure = new SourceRetrievalException("source_registry_unavailable", "Unavailable.");
                await first.Scheduler.RunDueRegistriesAsync(default);
                var failed = await first.Service.GetAsync("restart", default);
                Assert.AreEqual(1, failed!.Observed.Definition.ConsecutiveFailures);
            }

            clock.Advance(TimeSpan.FromSeconds(29));
            await using (var restarted = await DurableFixture.CreateAsync(database, clock))
            {
                using var system = restarted.Context.PushSystem();
                restarted.Documents.Failure = new SourceRetrievalException("source_registry_unavailable", "Unavailable.");
                await restarted.Scheduler.RunDueRegistriesAsync(default);
                Assert.IsEmpty(restarted.Documents.Requests);

                clock.Advance(TimeSpan.FromSeconds(1));
                await restarted.Scheduler.RunDueRegistriesAsync(default);
                Assert.HasCount(1, restarted.Documents.Requests);
                var failed = await restarted.Service.GetAsync("restart", default);
                Assert.AreEqual(2, failed!.Observed.Definition.ConsecutiveFailures);
                var history = await restarted.Service.ListRefreshesAsync("restart", 10, default);
                CollectionAssert.AreEqual(new[] { 1, 0 }, history.Select(value => value.Definition.RetryCount).ToArray());
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(database)) File.Delete(database);
        }
    }

    [TestMethod]
    public async Task SuccessfulRefreshRetainsOnlyConfiguredCachedObservations()
    {
        using var fixture = new Fixture();
        _ = await fixture.Service.CreateAsync(
            "retained",
            Registration("https://registry.example/v1/index.json") with
            {
                CachePolicy = new SourceRegistryCachePolicy { RetainedObservations = 1 }
            },
            default);
        fixture.EnqueueSuccessfulRefresh("https://registry.example/v1");
        var first = await fixture.Service.RefreshAsync("retained", default);
        fixture.EnqueueSuccessfulRefresh("https://registry.example/v1", "second");
        var second = await fixture.Service.RefreshAsync("retained", default);

        Assert.IsFalse(fixture.Cache.Contains(first.Observed.Definition.Current!.Id));
        Assert.IsTrue(fixture.Cache.Contains(second.Observed.Definition.Current!.Id));
        Assert.HasCount(2, await fixture.Service.ListRefreshesAsync("retained", 10, default));
    }

    [TestMethod]
    public void RegistryScheduleUsesBoundedRetryWindowAndDeterministicJitter()
    {
        var attemptedAt = DateTimeOffset.Parse(
            "2026-09-10T08:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var policy = new SourceRegistryRefreshPolicy
        {
            Interval = TimeSpan.FromMinutes(10),
            MaximumAttempts = 3,
            InitialBackoff = TimeSpan.FromSeconds(30),
            MaximumBackoff = TimeSpan.FromMinutes(2),
            Jitter = TimeSpan.FromSeconds(15)
        };

        Assert.AreEqual(attemptedAt + TimeSpan.FromSeconds(30),
            SourceRefreshScheduler.GetNextDue(attemptedAt, 1, policy, "registry|one"));
        Assert.AreEqual(attemptedAt + TimeSpan.FromSeconds(60),
            SourceRefreshScheduler.GetNextDue(attemptedAt, 2, policy, "registry|one"));
        var exhausted = SourceRefreshScheduler.GetNextDue(attemptedAt, 3, policy, "registry|one");
        Assert.IsTrue(exhausted >= attemptedAt + policy.Interval);
        Assert.IsTrue(exhausted <= attemptedAt + policy.Interval + policy.Jitter);
        Assert.AreEqual(exhausted,
            SourceRefreshScheduler.GetNextDue(attemptedAt, 3, policy, "registry|one"));
    }

    [TestMethod]
    public async Task ManualAndScheduledRefreshesCannotPublishCompetingObservations()
    {
        using var fixture = new Fixture();
        _ = await fixture.Service.CreateAsync(
            "coordinated",
            Registration("https://registry.example/v1/index.json") with
            {
                RefreshPolicy = new SourceRegistryRefreshPolicy
                {
                    PeriodicEnabled = true,
                    Interval = TimeSpan.FromMinutes(1),
                    Jitter = TimeSpan.Zero
                }
            },
            default);
        fixture.EnqueueSuccessfulRefresh("https://registry.example/v1");
        fixture.Documents.PauseNextRequest = true;

        var manual = fixture.Service.RefreshAsync("coordinated", default);
        await fixture.Documents.RequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var scheduled = fixture.Scheduler.RunDueRegistriesAsync(default);
        fixture.Documents.ReleaseRequest.TrySetResult(true);
        await Task.WhenAll(manual, scheduled);

        Assert.HasCount(2, fixture.Documents.Requests);
        Assert.HasCount(1, await fixture.Service.ListRefreshesAsync("coordinated", 10, default));
        var current = await fixture.Service.GetAsync("coordinated", default);
        Assert.AreEqual(SourceRegistryRefreshTrigger.Manual, current!.Observed.Definition.LastTrigger);
    }

    [TestMethod]
    public async Task CallerCancellationDoesNotPublishARefreshFailure()
    {
        using var fixture = new Fixture();
        _ = await fixture.Service.CreateAsync(
            "cancelled",
            Registration("https://registry.example/v1/index.json") with
            {
                RefreshPolicy = new SourceRegistryRefreshPolicy
                {
                    PeriodicEnabled = true,
                    Interval = TimeSpan.FromMinutes(1),
                    Timeout = TimeSpan.FromMinutes(1),
                    Jitter = TimeSpan.Zero
                }
            },
            default);
        fixture.Documents.PauseNextRequest = true;
        using var cancellation = new CancellationTokenSource();

        var scheduled = fixture.Scheduler.RunDueRegistriesAsync(cancellation.Token);
        await fixture.Documents.RequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cancellation.CancelAsync();
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => scheduled);

        var state = await fixture.Service.GetAsync("cancelled", default);
        Assert.AreEqual(SourceRegistryObservedStatus.NeverFetched, state!.Observed.Definition.Status);
        Assert.IsNull(state.Observed.Definition.LastAttemptedAt);
        Assert.IsEmpty(await fixture.Service.ListRefreshesAsync("cancelled", 10, default));
    }

    [TestMethod]
    public async Task DeletingRegistrationPreservesRefreshHistoryAndCachedObservation()
    {
        using var fixture = new Fixture();
        var created = await fixture.Service.CreateAsync(
            "community-history",
            Registration("https://registry.example/v1/index.json"),
            default);
        var shard = RegistryJson("compatible");
        var digest = new SourceRegistryReader().Read(shard, "registry-compatible.json").RegistryDigest;
        fixture.Documents.Enqueue(Document("https://registry.example/v1/index.json", "index.json", IndexJson(digest)));
        fixture.Documents.Enqueue(Document("https://registry.example/v1/registry-compatible.json", "registry-compatible.json", shard));
        var refreshed = await fixture.Service.RefreshAsync("community-history", default);
        var observationId = refreshed.Observed.Definition.Current!.Id;

        await fixture.Service.DeleteAsync("community-history", created.ETag, default);

        Assert.IsNull(await fixture.Service.GetAsync("community-history", default));
        Assert.IsNotNull(await fixture.Cache.GetAsync(observationId, default));
        var history = await fixture.Store.ListExactAsync<SourceRegistryRefreshRecordResource>(
            ResourceScopeRef.Instance,
            ResourceKinds.SourceRegistryRefreshRecord,
            0,
            100,
            default);
        Assert.IsTrue(history.Any(value => value.Value.Definition.RegistrationUid == created.Value.Uid));
    }

    [TestMethod]
    public async Task OfficialRegistrationIsSeededOnceAndAdministratorOverridesArePreserved()
    {
        using var fixture = new Fixture();

        var seeded = await fixture.Service.EnsureOfficialAsync(default);
        _ = await fixture.Service.UpdateOfficialAsync(
            new Uri("https://registry.example/v1/index.yaml"),
            enabled: false,
            seeded.Registration.ETag,
            default);

        var afterUpgrade = await fixture.Service.EnsureOfficialAsync(default);

        Assert.AreEqual(SourceRegistryWellKnown.OfficialName, afterUpgrade.Registration.Name);
        Assert.AreEqual("https://registry.example/v1/index.yaml", afterUpgrade.Registration.Definition.IndexUrl.AbsoluteUri);
        Assert.IsFalse(afterUpgrade.Registration.Definition.Enabled);
        Assert.AreEqual(SourceRegistryObservedStatus.Disabled, afterUpgrade.Observed.Definition.Status);
        Assert.AreEqual(2, afterUpgrade.Registration.Generation);
    }

    [TestMethod]
    public async Task RefreshSelectsEveryCompatibleShardAndDoesNotFetchOtherDocuments()
    {
        using var fixture = new Fixture();
        await fixture.Service.EnsureOfficialAsync(default);
        var compatibleJson = RegistryJson("compatible");
        var compatibleYaml = RegistryYaml("prerelease");
        var jsonDigest = new SourceRegistryReader().Read(compatibleJson, "registry-compatible.json").RegistryDigest;
        var yamlDigest = new SourceRegistryReader().Read(compatibleYaml, "registry-prerelease.yaml").RegistryDigest;
        var index = IndexYaml(jsonDigest, yamlDigest);
        fixture.Documents.Enqueue(Document(SourceRegistryWellKnown.OfficialIndexUrl, "index.yaml", index, "\"index-v1\""));
        fixture.Documents.Enqueue(Document("https://registry.agentstration.io/v1/registry-compatible.json", "registry-compatible.json", compatibleJson));
        fixture.Documents.Enqueue(Document("https://registry.agentstration.io/v1/registry-prerelease.yaml", "registry-prerelease.yaml", compatibleYaml));

        var refreshed = await fixture.Service.RefreshOfficialAsync(default);

        Assert.AreEqual(SourceRegistryObservedStatus.Fresh, refreshed.Observed.Definition.Status);
        Assert.AreEqual(SourceRegistryRefreshOutcome.Succeeded, refreshed.Observed.Definition.LastOutcome);
        Assert.AreEqual(2, refreshed.Observed.Definition.Current!.Catalogs.Count);
        CollectionAssert.AreEqual(
            new[]
            {
                SourceRegistryWellKnown.OfficialIndexUrl,
                "https://registry.agentstration.io/v1/registry-compatible.json",
                "https://registry.agentstration.io/v1/registry-prerelease.yaml"
            },
            fixture.Documents.Requests.Select(value => value.Url.AbsoluteUri).ToArray());
        var cached = await fixture.Cache.GetAsync(refreshed.Observed.Definition.Current.Id, default);
        Assert.IsNotNull(cached);
        Assert.AreEqual(2, cached.Catalogs.Count);
        Assert.AreEqual(1, (await fixture.Service.ListRefreshesAsync(SourceRegistryWellKnown.OfficialName, 10, default)).Count);
    }

    [TestMethod]
    public async Task FailedReplacementPreservesLastKnownGoodAndRecordsActionableState()
    {
        using var fixture = new Fixture();
        await fixture.Service.EnsureOfficialAsync(default);
        var shard = RegistryJson("compatible");
        var digest = new SourceRegistryReader().Read(shard, "registry-compatible.json").RegistryDigest;
        fixture.Documents.Enqueue(Document(SourceRegistryWellKnown.OfficialIndexUrl, "index.json", IndexJson(digest), "\"index-v1\""));
        fixture.Documents.Enqueue(Document("https://registry.agentstration.io/v1/registry-compatible.json", "registry-compatible.json", shard));
        var first = await fixture.Service.RefreshOfficialAsync(default);
        var observationId = first.Observed.Definition.Current!.Id;
        fixture.Documents.Failure = new SourceRetrievalException("source_registry_timeout", "The registry request timed out.");

        var failure = await Assert.ThrowsAsync<SourceRegistryOperationException>(() => fixture.Service.RefreshOfficialAsync(default));
        var afterFailure = await fixture.Service.GetAsync(SourceRegistryWellKnown.OfficialName, default);

        Assert.AreEqual("source_registry_timeout", failure.Code);
        Assert.AreEqual(SourceRegistryObservedStatus.Stale, afterFailure!.Observed.Definition.Status);
        Assert.AreEqual(SourceRegistryRefreshOutcome.Unavailable, afterFailure.Observed.Definition.LastOutcome);
        Assert.AreEqual("source_registry_timeout", afterFailure.Observed.Definition.LastErrorCode);
        Assert.AreEqual(observationId, afterFailure.Observed.Definition.Current!.Id);
        Assert.IsNotNull(await fixture.Cache.GetAsync(observationId, default));
        Assert.AreEqual(2, (await fixture.Service.ListRefreshesAsync(SourceRegistryWellKnown.OfficialName, 10, default)).Count);
    }

    [TestMethod]
    public async Task NotModifiedSendsValidatorsAndKeepsCatalogueProvenance()
    {
        using var fixture = new Fixture();
        await fixture.Service.EnsureOfficialAsync(default);
        var shard = RegistryJson("compatible");
        var digest = new SourceRegistryReader().Read(shard, "registry-compatible.json").RegistryDigest;
        var lastModified = new DateTimeOffset(2026, 9, 1, 10, 0, 0, TimeSpan.Zero);
        fixture.Documents.Enqueue(Document(SourceRegistryWellKnown.OfficialIndexUrl, "index.json", IndexJson(digest), "\"index-v1\"", lastModified));
        fixture.Documents.Enqueue(Document("https://registry.agentstration.io/v1/registry-compatible.json", "registry-compatible.json", shard));
        var first = await fixture.Service.RefreshOfficialAsync(default);
        fixture.Documents.Enqueue(new(
            new Uri(SourceRegistryWellKnown.OfficialIndexUrl),
            new Uri(SourceRegistryWellKnown.OfficialIndexUrl),
            "index.json",
            "\"index-v1\"",
            lastModified,
            true,
            null));

        var second = await fixture.Service.RefreshOfficialAsync(default);
        var conditional = fixture.Documents.Requests.Last();

        Assert.AreEqual("\"index-v1\"", conditional.ETag);
        Assert.AreEqual(lastModified, conditional.LastModified);
        Assert.AreEqual(SourceRegistryRefreshOutcome.NotModified, second.Observed.Definition.LastOutcome);
        Assert.AreEqual(first.Observed.Definition.Current!.Id, second.Observed.Definition.Current!.Id);
        Assert.AreEqual(first.Observed.Definition.Current.Catalogs[0], second.Observed.Definition.Current.Catalogs[0]);
    }

    [TestMethod]
    public async Task MissingCompatibleShardHasAnExplicitState()
    {
        using var fixture = new Fixture { Version = { CurrentVersion = "2.0.0" } };
        await fixture.Service.EnsureOfficialAsync(default);
        var shard = RegistryJson("compatible");
        var digest = new SourceRegistryReader().Read(shard, "registry-compatible.json").RegistryDigest;
        fixture.Documents.Enqueue(Document(SourceRegistryWellKnown.OfficialIndexUrl, "index.json", IndexJson(digest)));

        var failure = await Assert.ThrowsAsync<SourceRegistryOperationException>(() => fixture.Service.RefreshOfficialAsync(default));
        var state = await fixture.Service.GetAsync(SourceRegistryWellKnown.OfficialName, default);

        Assert.AreEqual("no_compatible_registry_catalog", failure.Code);
        Assert.AreEqual(SourceRegistryObservedStatus.NoCompatibleCatalog, state!.Observed.Definition.Status);
        Assert.AreEqual(1, fixture.Documents.Requests.Count);
    }

    [TestMethod]
    public async Task DigestMismatchCannotBecomeTheCurrentObservation()
    {
        using var fixture = new Fixture();
        await fixture.Service.EnsureOfficialAsync(default);
        var shard = RegistryJson("compatible");
        fixture.Documents.Enqueue(Document(
            SourceRegistryWellKnown.OfficialIndexUrl,
            "index.json",
            IndexJson("sha256:1111111111111111111111111111111111111111111111111111111111111111")));
        fixture.Documents.Enqueue(Document(
            "https://registry.agentstration.io/v1/registry-compatible.json",
            "registry-compatible.json",
            shard));

        var failure = await Assert.ThrowsAsync<SourceRegistryOperationException>(() => fixture.Service.RefreshOfficialAsync(default));
        var state = await fixture.Service.GetAsync(SourceRegistryWellKnown.OfficialName, default);

        Assert.AreEqual("source_registry_catalog_digest_mismatch", failure.Code);
        Assert.AreEqual(SourceRegistryObservedStatus.Invalid, state!.Observed.Definition.Status);
        Assert.IsNull(state.Observed.Definition.Current);
    }

    [TestMethod]
    public async Task DisabledRegistrationDoesNotReachTheNetwork()
    {
        using var fixture = new Fixture();
        var seeded = await fixture.Service.EnsureOfficialAsync(default);
        _ = await fixture.Service.UpdateOfficialAsync(
            seeded.Registration.Definition.IndexUrl,
            enabled: false,
            seeded.Registration.ETag,
            default);

        var failure = await Assert.ThrowsAsync<SourceRegistryOperationException>(() => fixture.Service.RefreshOfficialAsync(default));
        var state = await fixture.Service.GetAsync(SourceRegistryWellKnown.OfficialName, default);

        Assert.AreEqual("source_registry_disabled", failure.Code);
        Assert.AreEqual(SourceRegistryObservedStatus.Disabled, state!.Observed.Definition.Status);
        Assert.IsEmpty(fixture.Documents.Requests);
    }

    [TestMethod]
    public async Task DiscoveryMergesEqualObservationsAndReportsDigestConflicts()
    {
        using var fixture = new Fixture();
        await AddAndRefreshAsync(fixture, "trusted-a", "https://a.example/v1",
            SourceRegistryTrustPolicy.Trusted, SourceRegistryPublisherStatuses.Verified, RegistryManifestDigest);
        await AddAndRefreshAsync(fixture, "trusted-b", "https://b.example/v1",
            SourceRegistryTrustPolicy.Trusted, SourceRegistryPublisherStatuses.Verified, RegistryManifestDigest);

        var merged = await fixture.Discovery.SearchAsync(new(), default);

        Assert.AreEqual(1, merged.Total);
        Assert.AreEqual(2, merged.Value.Single().Versions.Single().Observations.Count);
        Assert.IsFalse(merged.Value.Single().Versions.Single().Conflicted);
        Assert.IsTrue(merged.Value.Single().Versions.Single().Observations.All(value => value.IsCatalogLatest));

        await AddAndRefreshAsync(fixture, "conflicting", "https://c.example/v1",
            SourceRegistryTrustPolicy.Trusted, SourceRegistryPublisherStatuses.Verified,
            "sha256:1111111111111111111111111111111111111111111111111111111111111111");
        var conflict = await fixture.Discovery.SearchAsync(new() { ConflictsOnly = true }, default);

        Assert.AreEqual(1, conflict.Total);
        Assert.IsTrue(conflict.Value.Single().Versions.Single().Conflicted);
        Assert.AreEqual(3, conflict.Value.Single().Versions.Single().Observations.Count);
    }

    [TestMethod]
    public async Task ExactObservationImportIsIdempotentAndRetainsRegistryProvenance()
    {
        using var fixture = new Fixture();
        var manifest = SourceManifest();
        var digest = new SourceManifestReader().Read(manifest).Digest;
        await AddAndRefreshAsync(fixture, "trusted", "https://registry.example/v1",
            SourceRegistryTrustPolicy.Trusted, SourceRegistryPublisherStatuses.Verified, digest);
        var selection = (await fixture.Discovery.SearchAsync(new(), default)).Value
            .Single().Versions.Single().Observations.Single().Selection;
        fixture.Documents.Enqueue(Document(
            "https://registry.example/v1/sources/agentstration/sample/1/source.yaml",
            "source.yaml", manifest, "\"manifest-1\""));

        var imported = await fixture.Discovery.ImportAsync(selection, ResourceScopeRef.Instance, default);

        Assert.AreEqual(SourceImportOutcome.Created, imported.Outcome);
        Assert.AreEqual(selection, imported.Version.Definition.Origin!.Registry!.Selection);
        Assert.IsTrue(string.Equals(digest,
            imported.Version.Definition.Origin.Registry.ExpectedManifestDigest, StringComparison.Ordinal));
        Assert.AreEqual("\"manifest-1\"", imported.Version.Definition.Origin.Registry.ManifestETag);

        fixture.Documents.Enqueue(Document(
            "https://registry.example/v1/sources/agentstration/sample/1/source.yaml",
            "source.yaml", manifest, "\"manifest-1\""));
        var repeated = await fixture.Discovery.ImportAsync(selection, ResourceScopeRef.Instance, default);

        Assert.AreEqual(SourceImportOutcome.Unchanged, repeated.Outcome);
        Assert.AreEqual(imported.Version.Uid, repeated.Version.Uid);
        Assert.AreEqual(imported.Source.Configuration.ETag, repeated.Source.Configuration.ETag);
        Assert.AreEqual(1, (await fixture.Sources.ListVersionsExactAsync(
            ResourceScopeRef.Instance, "agentstration", "sample", default)).Count);
        var refresh = await Assert.ThrowsAsync<SourceValidationException>(() => fixture.Sources.RefreshExactAsync(
            ResourceScopeRef.Instance, "agentstration", "sample", SourceRefreshTrigger.Manual, default));
        Assert.AreEqual("source_registry_origin_requires_exact_import", refresh.Code);
        Assert.AreEqual(2, fixture.Audit.Entries.Count(value =>
            value.Action == SecurityAuditActions.SourceRegistrySourceImported
            && value.Outcome == SecurityAuditOutcome.Succeeded));
    }

    [TestMethod]
    public async Task ExactImportRejectsUntrustedConflictedAndEvictedSelections()
    {
        using var fixture = new Fixture();
        await AddAndRefreshAsync(fixture, "untrusted", "https://untrusted.example/v1",
            SourceRegistryTrustPolicy.Untrusted, SourceRegistryPublisherStatuses.Verified, RegistryManifestDigest);
        var untrusted = (await fixture.Discovery.SearchAsync(new(), default)).Value
            .Single().Versions.Single().Observations.Single().Selection;
        var denied = await Assert.ThrowsAsync<SourceRegistryOperationException>(() =>
            fixture.Discovery.ImportAsync(untrusted, ResourceScopeRef.Instance, default));
        Assert.AreEqual("source_registry_selection_policy_denied", denied.Code);

        using var conflictFixture = new Fixture();
        await AddAndRefreshAsync(conflictFixture, "trusted-a", "https://a.example/v1",
            SourceRegistryTrustPolicy.Trusted, SourceRegistryPublisherStatuses.Verified, RegistryManifestDigest);
        await AddAndRefreshAsync(conflictFixture, "trusted-b", "https://b.example/v1",
            SourceRegistryTrustPolicy.Trusted, SourceRegistryPublisherStatuses.Verified,
            "sha256:1111111111111111111111111111111111111111111111111111111111111111");
        var conflict = (await conflictFixture.Discovery.SearchAsync(new(), default)).Value
            .Single().Versions.Single().Observations.First().Selection;
        var rejected = await Assert.ThrowsAsync<SourceRegistryOperationException>(() =>
            conflictFixture.Discovery.ImportAsync(conflict, ResourceScopeRef.Instance, default));
        Assert.AreEqual("source_registry_selection_conflicted", rejected.Code);

        var observation = (await conflictFixture.Service.GetAsync("trusted-a", default))!.Observed.Definition.Current!;
        await conflictFixture.Cache.RemoveAsync(observation.Id, default);
        var evicted = await Assert.ThrowsAsync<SourceRegistryOperationException>(() =>
            conflictFixture.Discovery.ImportAsync(conflict, ResourceScopeRef.Instance, default));
        Assert.AreEqual("source_registry_selection_evicted", evicted.Code);
    }

    private static RetrievedSourceRegistryDocument Document(
        string url,
        string fileName,
        string content,
        string? etag = null,
        DateTimeOffset? lastModified = null) =>
        new(new(url), new(url), fileName, etag, lastModified, false, content);

    private const string RegistryManifestDigest = "sha256:4b2aa0694ee27bc2b53b49cd52e5c221838f9ceed304fbd15c42ae26e2273b7d";

    private static string SourceManifest() => """
        apiVersion: agentstration.io/v1
        kind: SourceVersion
        metadata:
          name: sample
        definition:
          version: '1'
          displayName: Sample
          publisher:
            name: agentstration
          bindings: []
          channels: []
          catalogs: []
        """;

    private static string RegistryJson(string name) => RegistryJson(name, SourceRegistryPublisherStatuses.Official, RegistryManifestDigest);

    private static string RegistryJson(string name, string publisherStatus, string manifestDigest) => """
        {"apiVersion":"agentstration.io/v1","kind":"SourceRegistry","metadata":{"name":"$NAME$"},"definition":{"publishers":[{"name":"agentstration","status":"Official"}],"sources":[{"publisher":"agentstration","name":"sample","latest":"1","versions":[{"version":"1","manifestUrl":"sources/agentstration/sample/1/source.yaml","manifestDigest":"sha256:4b2aa0694ee27bc2b53b49cd52e5c221838f9ceed304fbd15c42ae26e2273b7d"}]}]}}
        """.Replace("$NAME$", name, StringComparison.Ordinal)
            .Replace("\"status\":\"Official\"", $"\"status\":\"{publisherStatus}\"", StringComparison.Ordinal)
            .Replace(RegistryManifestDigest, manifestDigest, StringComparison.Ordinal);

    private static SourceRegistryRegistrationResource RegistrationResource(string name, string indexUrl) => new()
    {
        Uid = Guid.NewGuid(),
        ApiVersion = ManagementApiVersions.CoreV1,
        Kind = ResourceKinds.SourceRegistryRegistration,
        Metadata = new ResourceMetadata { Name = name },
        ScopeRef = ResourceScopeRef.Instance,
        Definition = Registration(indexUrl)
    };

    private static SourceVersionResource SourceVersion(string digest) => new()
    {
        ApiVersion = ManagementApiVersions.CoreV1,
        Kind = ResourceKinds.SourceVersion,
        Metadata = new ResourceMetadata { Name = "sample-1" },
        Definition = new SourceVersionProperties
        {
            SourceUid = Guid.NewGuid(),
            SourceName = "sample",
            Publisher = "agentstration",
            Version = "1",
            ManifestDigest = digest,
            RawManifest = "test",
            ImportedAt = DateTimeOffset.UnixEpoch,
            PublishedDefinition = new PublishedSourceVersionDefinition
            {
                Version = "1",
                Publisher = new SourcePublisher { Name = "agentstration" }
            }
        }
    };

    private static async Task AddAndRefreshAsync(
        Fixture fixture,
        string name,
        string baseUrl,
        SourceRegistryTrustPolicy policy,
        string publisherStatus,
        string manifestDigest)
    {
        _ = await fixture.Service.CreateAsync(name, Registration($"{baseUrl}/index.json") with { TrustPolicy = policy }, default);
        var shard = RegistryJson(name, publisherStatus, manifestDigest);
        var digest = new SourceRegistryReader().Read(shard, "registry-compatible.json").RegistryDigest;
        fixture.Documents.Enqueue(Document($"{baseUrl}/index.json", "index.json", IndexJson(digest)));
        fixture.Documents.Enqueue(Document($"{baseUrl}/registry-compatible.json", "registry-compatible.json", shard));
        _ = await fixture.Service.RefreshAsync(name, default);
    }

    private static string RegistryYaml(string name) => $$"""
        apiVersion: agentstration.io/v1
        kind: SourceRegistry
        metadata:
          name: {{name}}
        definition:
          publishers:
            - name: agentstration
              status: Official
          sources:
            - publisher: agentstration
              name: sample
              latest: '1'
              versions:
                - version: '1'
                  manifestUrl: sources/agentstration/sample/1/source.yaml
                  manifestDigest: sha256:4b2aa0694ee27bc2b53b49cd52e5c221838f9ceed304fbd15c42ae26e2273b7d
        """;

    private static string IndexJson(string digest) => """
        {"apiVersion":"agentstration.io/v1","kind":"SourceRegistryIndex","metadata":{"name":"official"},"definition":{"catalogs":[{"name":"compatible","compatibility":{"agentstration":{"minVersion":"0.2.0-alpha.1","maxVersionExclusive":"0.3.0"}},"registryUrl":"registry-compatible.json","registryDigest":"$DIGEST$"},{"name":"future","compatibility":{"agentstration":{"minVersion":"1.0.0","maxVersionExclusive":"1.1.0"}},"registryUrl":"registry-future.json","registryDigest":"$DIGEST$"}]}}
        """.Replace("$DIGEST$", digest, StringComparison.Ordinal);

    private static string IndexYaml(string jsonDigest, string yamlDigest) => $$"""
        apiVersion: agentstration.io/v1
        kind: SourceRegistryIndex
        metadata:
          name: official
        definition:
          catalogs:
            - name: compatible
              compatibility:
                agentstration:
                  minVersion: 0.2.0
                  maxVersionExclusive: 0.3.0
              registryUrl: registry-compatible.json
              registryDigest: {{jsonDigest}}
            - name: prerelease
              compatibility:
                agentstration:
                  minVersion: 0.2.0-alpha.1
                  maxVersionExclusive: 0.3.0
              registryUrl: registry-prerelease.yaml
              registryDigest: {{yamlDigest}}
            - name: future
              compatibility:
                agentstration:
                  minVersion: 1.0.0
              registryUrl: registry-future.json
              registryDigest: {{jsonDigest}}
        """;

    private sealed class Fixture : IDisposable
    {
        private readonly IDisposable systemScope;

        public Fixture(TimeProvider? timeProvider = null)
        {
            var context = new CurrentRequestContext();
            systemScope = context.PushSystem();
            Store = new MemoryStore();
            Documents = new FakeDocuments();
            Cache = new MemoryCache();
            Version = new FakeVersion();
            Audit = new FakeAudit();
            var operations = new ResourceScopeOperationService(context, context, null!, null!, null!, new InstanceScopeResolver());
            Service = new(
                Store,
                new SourceRegistryIndexReader(),
                new SourceRegistryReader(),
                new SourceRegistryRuntimeReferenceResolver(),
                Documents,
                Cache,
                Version,
                operations,
                context,
                context,
                Audit,
                timeProvider ?? TimeProvider.System,
                NullLogger<SourceRegistryManagementService>.Instance);
            Trust = new(Store, Cache, new SourceRegistryReader(), timeProvider ?? TimeProvider.System);
            var verification = new SourceVerificationService(new EmptyVerificationIndex(), [Trust]);
            Sources = new(Store, context, new SourceManifestReader(), new NullSourceRetriever(),
                timeProvider ?? TimeProvider.System, operations, verification);
            Discovery = new(Store, Cache, new SourceRegistryIndexReader(), new SourceRegistryReader(), new SourceRegistryRuntimeReferenceResolver(),
                Documents, Version, Trust, Sources, context, Audit, timeProvider ?? TimeProvider.System);
            Scheduler = new SourceRefreshScheduler(null!, null!, context, timeProvider ?? TimeProvider.System, Service);
        }

        public MemoryStore Store { get; }
        public FakeDocuments Documents { get; }
        public MemoryCache Cache { get; }
        public FakeVersion Version { get; }
        public FakeAudit Audit { get; }
        public SourceRegistryManagementService Service { get; }
        public SourceRegistryTrustEvaluationService Trust { get; }
        public SourceManagementService Sources { get; }
        public SourceRegistryDiscoveryService Discovery { get; }
        public SourceRefreshScheduler Scheduler { get; }
        public void EnqueueSuccessfulRefresh(string baseUrl, string name = "compatible")
        {
            var shard = RegistryJson(name);
            var digest = new SourceRegistryReader().Read(shard, $"registry-{name}.json").RegistryDigest;
            Documents.Enqueue(Document($"{baseUrl}/index.json", "index.json", IndexJson(digest), $"\"{name}\""));
            Documents.Enqueue(Document($"{baseUrl}/registry-compatible.json", "registry-compatible.json", shard));
        }
        public void Dispose() => systemScope.Dispose();
    }

    private sealed class NullSourceRetriever : ISourceManifestRetriever
    {
        public Task<RetrievedSourceManifest> RetrieveAsync(Uri source, CancellationToken cancellationToken) =>
            Task.FromException<RetrievedSourceManifest>(new AssertFailedException("The registry import must use the registry retriever."));
    }

    private sealed class FakeDocuments : ISourceRegistryDocumentRetriever
    {
        private readonly Queue<RetrievedSourceRegistryDocument> responses = new();
        public List<(Uri Url, string? ETag, DateTimeOffset? LastModified)> Requests { get; } = [];
        public Exception? Failure { get; set; }
        public bool BlockUntilCancelled { get; set; }
        public bool PauseNextRequest { get; set; }
        public TaskCompletionSource<bool> RequestStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> ReleaseRequest { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Enqueue(RetrievedSourceRegistryDocument response) => responses.Enqueue(response);

        public async Task<RetrievedSourceRegistryDocument> RetrieveAsync(Uri source, string? etag, DateTimeOffset? lastModified, int maximumBytes, SourceRegistryRetrievalContext context, CancellationToken cancellationToken)
        {
            Requests.Add((source, etag, lastModified));
            if (Failure is not null) throw Failure;
            if (PauseNextRequest)
            {
                PauseNextRequest = false;
                RequestStarted.TrySetResult(true);
                await ReleaseRequest.Task.WaitAsync(cancellationToken);
            }
            if (BlockUntilCancelled)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new AssertFailedException("The blocked registry request was not cancelled.");
            }
            var response = responses.Dequeue();
            Assert.AreEqual(source, response.RequestedUrl);
            Assert.IsTrue(Encoding.UTF8.GetByteCount(response.Content ?? string.Empty) <= maximumBytes);
            return response;
        }
    }

    private sealed class MemoryCache : ISourceRegistryCacheStore
    {
        private readonly Dictionary<Guid, SourceRegistryCachedPublication> values = [];
        public Task StoreAsync(SourceRegistryCachedPublication publication, CancellationToken cancellationToken)
        {
            values.Add(publication.ObservationId, publication);
            return Task.CompletedTask;
        }
        public Task<SourceRegistryCachedPublication?> GetAsync(Guid observationId, CancellationToken cancellationToken) =>
            Task.FromResult(values.GetValueOrDefault(observationId));
        public Task RemoveAsync(Guid observationId, CancellationToken cancellationToken)
        {
            values.Remove(observationId);
            return Task.CompletedTask;
        }
        public bool Contains(Guid observationId) => values.ContainsKey(observationId);
    }

    private sealed class MutableTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
        public void Advance(TimeSpan duration) => value += duration;
    }

    private sealed class DurableFixture : IAsyncDisposable
    {
        private readonly ServiceProvider services;

        private DurableFixture(ServiceProvider services, CurrentRequestContext context, SourceRegistryManagementService service,
            SourceRefreshScheduler scheduler, FakeDocuments documents)
        {
            this.services = services;
            Context = context;
            Service = service;
            Scheduler = scheduler;
            Documents = documents;
        }

        public SourceRegistryManagementService Service { get; }
        public SourceRefreshScheduler Scheduler { get; }
        public FakeDocuments Documents { get; }
        public CurrentRequestContext Context { get; }

        public static async Task<DurableFixture> CreateAsync(string database, TimeProvider timeProvider)
        {
            var services = new ServiceCollection()
                .AddSingleton(timeProvider)
                .AddSingleton<CurrentRequestContext>()
                .AddSingleton<ICurrentRequestContext>(provider => provider.GetRequiredService<CurrentRequestContext>())
                .AddSingleton<IRequestContextScopeFactory>(provider => provider.GetRequiredService<CurrentRequestContext>())
                .AddSqliteControlPlane($"Data Source={database}")
                .BuildServiceProvider();
            var context = services.GetRequiredService<CurrentRequestContext>();
            using (context.PushSystem())
                await services.GetRequiredService<IResourceStore>().InitializeAsync(default);
            var documents = new FakeDocuments();
            var service = new SourceRegistryManagementService(
                services.GetRequiredService<IResourceStore>(),
                new SourceRegistryIndexReader(),
                new SourceRegistryReader(),
                new SourceRegistryRuntimeReferenceResolver(),
                documents,
                new MemoryCache(),
                new FakeVersion(),
                new ResourceScopeOperationService(context, context, null!, null!, null!,
                    services.GetRequiredService<IResourceScopeResolver>()),
                context,
                context,
                new FakeAudit(),
                timeProvider,
                NullLogger<SourceRegistryManagementService>.Instance);
            return new DurableFixture(services, context, service,
                new SourceRefreshScheduler(null!, null!, context, timeProvider, service), documents);
        }

        public async ValueTask DisposeAsync()
        {
            await services.DisposeAsync();
        }
    }

    private sealed class FakeVersion : IAgentstrationVersionProvider
    {
        public string? CurrentVersion { get; set; } = "0.2.0";
    }

    private sealed class EmptyVerificationIndex : ISourceVerificationIndexProvider
    {
        public Task<VerifiedSourceIndexManifest?> GetAsync(CancellationToken cancellationToken) => Task.FromResult<VerifiedSourceIndexManifest?>(null);
    }

    private sealed class FakeAudit : ISecurityAuditWriter
    {
        public List<SecurityAuditWrite> Entries { get; } = [];
        public Task WriteAsync(SecurityAuditWrite entry, CancellationToken cancellationToken)
        {
            Entries.Add(entry);
            return Task.CompletedTask;
        }
    }

    private sealed class InstanceScopeResolver : IResourceScopeResolver
    {
        private static readonly ResourceScope Instance = new(1, ResourceScopeRef.Instance, ResourceScopeKind.Instance, "instance", null);
        public Task<ResolvedResourceScope?> ResolveAsync(ResourceScopeRef scopeRef, CancellationToken cancellationToken) =>
            Task.FromResult<ResolvedResourceScope?>(scopeRef == ResourceScopeRef.Instance ? new(Instance, []) : null);
    }

    private sealed class MemoryStore : IResourceStore
    {
        private readonly Dictionary<ScopedResourceAddress, (Resource Value, string ETag, DateTimeOffset At)> values = [];
        private long version;
        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<StoredResource<T>?> GetAsync<T>(ResourceKey key, CancellationToken cancellationToken) where T : Resource => GetExactAsync<T>(key.AtScope(ResourceScopeRef.Instance), cancellationToken);
        public Task<StoredResource<T>?> GetExactAsync<T>(ScopedResourceAddress address, CancellationToken cancellationToken) where T : Resource =>
            Task.FromResult(values.TryGetValue(address, out var entry) && entry.Value is T typed ? new StoredResource<T>(typed, entry.ETag, entry.At) : null);
        public Task<IReadOnlyList<StoredResource<T>>> ListAsync<T>(string kind, int skip, int take, CancellationToken cancellationToken) where T : Resource => ListExactAsync<T>(ResourceScopeRef.Instance, kind, skip, take, cancellationToken);
        public Task<IReadOnlyList<StoredResource<T>>> ListExactAsync<T>(ResourceScopeRef scopeRef, string kind, int skip, int take, CancellationToken cancellationToken) where T : Resource =>
            Task.FromResult<IReadOnlyList<StoredResource<T>>>(values.Where(value => value.Key.ScopeRef == scopeRef && value.Key.Address.Kind == kind && value.Value.Value is T).Select(value => new StoredResource<T>((T)value.Value.Value, value.Value.ETag, value.Value.At)).Skip(skip).Take(take).ToArray());
        public Task<IReadOnlyList<StoredResource<T>>> ListVisibleAsync<T>(ResourceScopeRef targetScopeRef, string kind, int skip, int take, CancellationToken cancellationToken) where T : Resource => ListExactAsync<T>(targetScopeRef, kind, skip, take, cancellationToken);
        public Task<StoredResource<T>> PutAsync<T>(T resource, string? ifMatch, bool ifNoneMatch, CancellationToken cancellationToken) where T : Resource => PutExactAsync(resource.ScopeRef ?? ResourceScopeRef.Instance, resource, ifMatch, ifNoneMatch, cancellationToken);
        public Task<StoredResource<T>> PutExactAsync<T>(ResourceScopeRef scopeRef, T resource, string? ifMatch, bool ifNoneMatch, CancellationToken cancellationToken) where T : Resource
        {
            var key = ScopedResourceAddress.Create(scopeRef, resource.Namespace, resource.Kind, resource.Name);
            if (ifNoneMatch && values.ContainsKey(key)) throw new ResourceConcurrencyException("Already exists.");
            if (ifMatch is not null && (!values.TryGetValue(key, out var current) || current.ETag != ifMatch)) throw new ResourceConcurrencyException("ETag mismatch.");
            var etag = $"\"{Interlocked.Increment(ref version)}\"";
            var value = resource.WithSystemState(resource.Uid == Guid.Empty ? Guid.NewGuid() : resource.Uid, scopeRef, etag);
            values[key] = (value, etag, DateTimeOffset.UnixEpoch);
            return Task.FromResult(new StoredResource<T>((T)value, etag, DateTimeOffset.UnixEpoch));
        }
        public Task<StoredResource<T>> CreateImmutableAsync<T>(T resource, CancellationToken cancellationToken) where T : Resource => PutAsync(resource, null, true, cancellationToken);
        public Task DeleteAsync(ResourceKey key, string? ifMatch, CancellationToken cancellationToken) => DeleteExactAsync(key.AtScope(ResourceScopeRef.Instance), ifMatch, cancellationToken);
        public Task DeleteExactAsync(ScopedResourceAddress address, string? ifMatch, CancellationToken cancellationToken) { values.Remove(address); return Task.CompletedTask; }
    }
}
