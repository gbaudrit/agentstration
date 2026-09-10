using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Agentstration.Infrastructure.Sources;
using Agentstration.Management.Abstractions;
using Agentstration.Resources;
using Agentstration.Secrets.Abstractions;

namespace Agentstration.Management.Tests;

[TestClass]
public sealed class SourceRegistryInfrastructureTests
{
    [TestMethod]
    public async Task RetrieverSendsValidatorsAndAcceptsExactJsonMediaType()
    {
        var lastModified = new DateTimeOffset(2026, 9, 1, 10, 0, 0, TimeSpan.Zero);
        using var handler = new StubHandler(request =>
        {
            Assert.AreEqual("\"index-v1\"", request.Headers.IfNoneMatch.Single().Tag);
            Assert.AreEqual(lastModified, request.Headers.IfModifiedSince);
            return Response(HttpStatusCode.OK, "{}", "application/json", "utf-8");
        });
        using var client = new HttpClient(handler);
        var retriever = new HttpSourceRegistryDocumentRetriever(client, new SourceRegistryTransportOptions());

        var result = await retriever.RetrieveAsync(
            new Uri("https://registry.example/v1/index.json"),
            "\"index-v1\"",
            lastModified,
            1024,
            Context(),
            default);

        Assert.AreEqual("{}", result.Content);
        Assert.IsFalse(result.NotModified);
    }

    [TestMethod]
    public async Task RetrieverAllowsSameOriginRedirectAndRejectsCrossOriginRedirect()
    {
        var responses = new Queue<HttpResponseMessage>([
            Redirect("/v1/current/index.yaml"),
            Response(HttpStatusCode.OK, "kind: SourceRegistryIndex", "application/yaml", "utf-8")
        ]);
        using var handler = new StubHandler(_ => responses.Dequeue());
        using var client = new HttpClient(handler);
        var retriever = new HttpSourceRegistryDocumentRetriever(client, new SourceRegistryTransportOptions());

        var result = await retriever.RetrieveAsync(new Uri("https://registry.example/v1/index.yaml"), null, null, 1024, Context(), default);

        Assert.AreEqual("https://registry.example/v1/current/index.yaml", result.FinalUrl.AbsoluteUri);

        using var crossOriginHandler = new StubHandler(_ => Redirect("https://other.example/v1/index.json"));
        using var crossOriginClient = new HttpClient(crossOriginHandler);
        var crossOrigin = new HttpSourceRegistryDocumentRetriever(crossOriginClient, new SourceRegistryTransportOptions());
        var failure = await Assert.ThrowsAsync<SourceRetrievalException>(() =>
            crossOrigin.RetrieveAsync(new Uri("https://registry.example/v1/index.json"), null, null, 1024, Context(), default));
        Assert.AreEqual("source_registry_redirect_origin_invalid", failure.Code);
    }

    [TestMethod]
    public async Task RetrieverRejectsInvalidMediaOversizedBodiesAndTimeouts()
    {
        using var invalidMediaHandler = new StubHandler(_ => Response(HttpStatusCode.OK, "{}", "text/plain", "utf-8"));
        using var invalidMediaClient = new HttpClient(invalidMediaHandler);
        var invalidMedia = new HttpSourceRegistryDocumentRetriever(invalidMediaClient, new SourceRegistryTransportOptions());
        var mediaFailure = await Assert.ThrowsAsync<SourceRetrievalException>(() =>
            invalidMedia.RetrieveAsync(new Uri("https://registry.example/v1/index.json"), null, null, 1024, Context(), default));
        Assert.AreEqual("source_registry_media_type_invalid", mediaFailure.Code);

        using var oversizedHandler = new StubHandler(_ => Response(HttpStatusCode.OK, new string('x', 32), "application/json", "utf-8"));
        using var oversizedClient = new HttpClient(oversizedHandler);
        var oversized = new HttpSourceRegistryDocumentRetriever(oversizedClient, new SourceRegistryTransportOptions());
        var sizeFailure = await Assert.ThrowsAsync<SourceRetrievalException>(() =>
            oversized.RetrieveAsync(new Uri("https://registry.example/v1/index.json"), null, null, 16, Context(), default));
        Assert.AreEqual("source_registry_size_limit", sizeFailure.Code);

        using var timeoutHandler = new StubHandler(_ => throw new TaskCanceledException("timeout"));
        using var timeoutClient = new HttpClient(timeoutHandler);
        var timeout = new HttpSourceRegistryDocumentRetriever(timeoutClient, new SourceRegistryTransportOptions());
        var timeoutFailure = await Assert.ThrowsAsync<SourceRetrievalException>(() =>
            timeout.RetrieveAsync(new Uri("https://registry.example/v1/index.json"), null, null, 1024, Context(), default));
        Assert.AreEqual("source_registry_timeout", timeoutFailure.Code);

        using var blockedHandler = new StubHandler(_ => throw new AssertFailedException("Blocked addresses must not reach HTTP."));
        using var blockedClient = new HttpClient(blockedHandler);
        var blocked = new HttpSourceRegistryDocumentRetriever(blockedClient, new SourceRegistryTransportOptions());
        var addressFailure = await Assert.ThrowsAsync<SourceRetrievalException>(() =>
            blocked.RetrieveAsync(new Uri("https://[::ffff:127.0.0.1]/v1/index.json"), null, null, 1024, Context(), default));
        Assert.AreEqual("source_registry_endpoint_policy_denied", addressFailure.Code);
    }

    [TestMethod]
    public async Task RetrieverResolvesBearerPerRequestWithoutLeakingItInFailures()
    {
        const string token = "private-registry-token";
        using var handler = new StubHandler(request =>
        {
            Assert.AreEqual("Bearer", request.Headers.Authorization?.Scheme);
            Assert.AreEqual(token, request.Headers.Authorization?.Parameter);
            return new HttpResponseMessage(HttpStatusCode.Unauthorized);
        });
        using var client = new HttpClient(handler);
        var resolver = new FakeSecretResolver(token);
        var retriever = new HttpSourceRegistryDocumentRetriever(client, new SourceRegistryTransportOptions(), resolver);
        var context = new SourceRegistryRetrievalContext(
            ResourceScopeRef.Instance,
            ResourceAddress.Create(ResourceNamespace.Default, ResourceKinds.SourceRegistryRegistration, "private"),
            new SourceRegistryEndpointPolicy(),
            SourceRegistryAuthenticationMode.StaticBearer,
            new ResourceReference("registry-token", ResourceScopeRef.Instance));

        var failure = await Assert.ThrowsAsync<SourceRetrievalException>(() => retriever.RetrieveAsync(
            new Uri("https://registry.example/v1/index.json"), null, null, 1024, context, default));

        Assert.AreEqual("source_registry_http_error", failure.Code);
        Assert.DoesNotContain(token, failure.Message, StringComparison.Ordinal);
        Assert.AreEqual(1, resolver.Resolutions);
    }

    [TestMethod]
    public async Task ExplicitPrivateHttpPolicyStillBlocksLinkLocalAddresses()
    {
        using var handler = new StubHandler(_ => Response(HttpStatusCode.OK, "{}", "application/json", "utf-8"));
        using var client = new HttpClient(handler);
        var retriever = new HttpSourceRegistryDocumentRetriever(client, new SourceRegistryTransportOptions());
        var policy = new SourceRegistryEndpointPolicy { AllowHttp = true, AllowPrivateNetwork = true };

        var loopback = await retriever.RetrieveAsync(
            new Uri("http://127.0.0.1/v1/index.json"), null, null, 1024, Context(policy), default);
        Assert.AreEqual("{}", loopback.Content);

        var failure = await Assert.ThrowsAsync<SourceRetrievalException>(() => retriever.RetrieveAsync(
            new Uri("http://169.254.169.254/v1/index.json"), null, null, 1024, Context(policy), default));
        Assert.AreEqual("source_registry_endpoint_policy_denied", failure.Code);
    }

    [TestMethod]
    public async Task ConnectionTimeDnsPolicyReportsDeniedInternalHost()
    {
        var options = new SourceRegistryTransportOptions { ConnectTimeoutSeconds = 2 };
        using var client = new HttpClient(HttpSourceRegistryDocumentRetriever.CreatePrimaryHandler(options));
        var retriever = new HttpSourceRegistryDocumentRetriever(client, options);

        var failure = await Assert.ThrowsAsync<SourceRetrievalException>(() => retriever.RetrieveAsync(
            new Uri("https://localhost:9443/v1/index.json"), null, null, 1024, Context(), default));

        Assert.AreEqual("source_registry_endpoint_policy_denied", failure.Code);
    }

    [TestMethod]
    public async Task FileCachePublishesCompleteObservationsAndLeavesExistingOnFailure()
    {
        var root = Path.Combine(Path.GetTempPath(), $"agentstration-registry-cache-{Guid.NewGuid():N}");
        try
        {
            var cache = new FileSystemSourceRegistryCacheStore(root);
            var first = new SourceRegistryCachedPublication(
                Guid.NewGuid(),
                "index"u8.ToArray(),
                new Dictionary<string, byte[]> { ["registry.json"] = "registry"u8.ToArray() });
            await cache.StoreAsync(first, default);

            var invalid = new SourceRegistryCachedPublication(
                Guid.NewGuid(),
                "index"u8.ToArray(),
                new Dictionary<string, byte[]> { ["../registry.json"] = "invalid"u8.ToArray() });
            await Assert.ThrowsAsync<SourceValidationException>(() => cache.StoreAsync(invalid, default));

            var preserved = await cache.GetAsync(first.ObservationId, default);
            Assert.IsNotNull(preserved);
            CollectionAssert.AreEqual("index"u8.ToArray(), preserved.Index);
            CollectionAssert.AreEqual("registry"u8.ToArray(), preserved.Catalogs["registry.json"]);

            await cache.RemoveAsync(first.ObservationId, default);
            Assert.IsNull(await cache.GetAsync(first.ObservationId, default));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static HttpResponseMessage Response(HttpStatusCode status, string content, string mediaType, string? charset)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(content, Encoding.UTF8) };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue(mediaType) { CharSet = charset };
        return response;
    }

    private static HttpResponseMessage Redirect(string location) => new(HttpStatusCode.Redirect)
    {
        Headers = { Location = new Uri(location, UriKind.RelativeOrAbsolute) }
    };

    private static SourceRegistryRetrievalContext Context(SourceRegistryEndpointPolicy? policy = null) =>
        new(
            ResourceScopeRef.Instance,
            Agentstration.Resources.ResourceAddress.Create(Agentstration.Resources.ResourceNamespace.Default, ResourceKinds.SourceRegistryRegistration, "test"),
            policy ?? new SourceRegistryEndpointPolicy(),
            SourceRegistryAuthenticationMode.None,
            null);

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(response(request));
    }

    private sealed class FakeSecretResolver(string token) : ISecretResolver
    {
        public int Resolutions { get; private set; }

        public Task<ResolvedSecret?> ResolveAsync(
            SecretReference secret,
            SecretResolutionContext context,
            CancellationToken cancellationToken = default)
        {
            Resolutions++;
            return Task.FromResult<ResolvedSecret?>(new(
                secret.Address,
                ResourceAddress.Create(ResourceNamespace.Default, ResourceKinds.Vault, "registry-vault"),
                new SecretValue(Encoding.UTF8.GetBytes(token))));
        }
    }
}
