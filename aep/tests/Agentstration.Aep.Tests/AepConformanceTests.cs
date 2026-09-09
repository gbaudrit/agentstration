using System.IO.Pipelines;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentstration.Aep.Abstractions;
using Agentstration.Aep.AspNetCore;
using Agentstration.Aep.Client;
using Agentstration.Aep.Inspector;
using Agentstration.Aep.Validation;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agentstration.Aep.Tests;

[TestClass]
public sealed class AepConformanceTests
{
    private static readonly string WorkloadToken = AepStaticBearerCredentials.Generate("aep-conformance-tests").AccessToken;

    [TestMethod]
    public async Task CanonicalClientDiscoversCapabilitiesAndHealth()
    {
        await using var factory = new WebApplicationFactory<global::Program>();
        using var httpClient = factory.CreateClient();
        var client = new AepClient(httpClient);

        var manifest = await client.GetManifestAsync();
        var capabilities = await client.GetCapabilitiesAsync();
        var health = await client.GetHealthAsync();

        Assert.AreEqual(AepProtocol.Version, manifest.ProtocolVersion);
        Assert.AreEqual("sample.hello", manifest.Extension.Id);
        Assert.AreEqual("1.0", capabilities[AepCapabilityNames.Health].Version);
        Assert.AreEqual("available", health.Status);
    }

    [TestMethod]
    public async Task LegacyDiscoveryAliasReturnsTheCanonicalManifest()
    {
        await using var factory = new WebApplicationFactory<global::Program>();
        using var httpClient = factory.CreateClient();

        var canonical = await httpClient.GetStringAsync(AepProtocol.DiscoveryPath);
        var legacy = await httpClient.GetStringAsync(AepProtocol.LegacyDiscoveryPath);

        Assert.AreEqual(canonical, legacy);
    }

    [TestMethod]
    public async Task StaticBearerProtectsEveryProtocolEndpointButNotPlatformHealth()
    {
        await using var factory = AuthenticatedFactory(AepAuthenticationDefaults.InvokePermission);
        using var anonymous = factory.CreateClient();
        using var authenticatedHttp = factory.CreateClient();
        var authenticated = new AepClient(authenticatedHttp, new StaticAepAccessTokenProvider(WorkloadToken));

        using var discovery = await anonymous.GetAsync(AepProtocol.DiscoveryPath);
        using var protocolHealth = await anonymous.GetAsync(AepProtocol.HealthPath);
        using var platformHealth = await anonymous.GetAsync("/health");
        var manifest = await authenticated.GetManifestAsync();

        Assert.AreEqual(HttpStatusCode.Unauthorized, discovery.StatusCode);
        Assert.AreEqual(HttpStatusCode.Unauthorized, protocolHealth.StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, platformHealth.StatusCode);
        Assert.AreEqual("sample.hello", manifest.Extension.Id);
    }

    [TestMethod]
    public async Task StaticBearerReturnsForbiddenWhenWorkloadLacksInvokePermission()
    {
        await using var factory = AuthenticatedFactory("aep.observe");
        using var httpClient = factory.CreateClient();
        var client = new AepClient(httpClient, new StaticAepAccessTokenProvider(WorkloadToken));

        var exception = await Assert.ThrowsAsync<AepProtocolException>(() => client.GetManifestAsync());

        Assert.AreEqual(HttpStatusCode.Forbidden, exception.StatusCode);
        Assert.AreEqual("authorization_denied", exception.Code);
    }

    [TestMethod]
    public void StaticBearerGeneratorCreatesRedacted256BitCredential()
    {
        var credential = AepStaticBearerCredentials.Generate("agentstration-test");

        Assert.AreEqual("agentstration-test", credential.ClientId);
        Assert.AreEqual(32, credential.TokenId.Length);
        Assert.IsGreaterThanOrEqualTo(43, credential.AccessToken.Length);
        Assert.AreEqual("***", credential.ToString());
    }

    [TestMethod]
    public void SharedKeyFileAcceptsOnlyBoundedSingleLineUtf8Tokens()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"aep-shared-key-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var valid = Path.Combine(directory, "valid.key");
            File.WriteAllText(valid, WorkloadToken + "\n");
            Assert.AreEqual(WorkloadToken, AepSharedKeyFile.Read(valid));
            var shortToken = Path.Combine(directory, "short.key");
            File.WriteAllText(shortToken, "too-short\n");
            Assert.ThrowsExactly<InvalidDataException>(() => AepSharedKeyFile.Read(shortToken));
            var multiline = Path.Combine(directory, "multiline.key");
            File.WriteAllText(multiline, WorkloadToken + "\nsecond-line\n");
            Assert.ThrowsExactly<InvalidDataException>(() => AepSharedKeyFile.Read(multiline));
            Assert.ThrowsExactly<InvalidOperationException>(() => AepSharedKeyFile.Read(Path.Combine(directory, "missing.key")));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task PairingCodeKeepsProtocolClosedAndPersistsItsInstanceIdentity()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"aep-pairing-{Guid.NewGuid():N}");
        var stateFile = Path.Combine(directory, "state.json");
        Directory.CreateDirectory(directory);
        try
        {
            string instanceId;
            await using (var factory = PairingFactory(stateFile))
            {
                using var client = factory.CreateClient();
                using var discovery = await client.GetAsync(AepProtocol.DiscoveryPath);
                using var pairing = await client.GetAsync(AepEnrollmentProtocol.PairingPath);
                var html = await pairing.Content.ReadAsStringAsync();

                Assert.AreEqual(HttpStatusCode.Unauthorized, discovery.StatusCode);
                Assert.AreEqual(HttpStatusCode.OK, pairing.StatusCode);
                StringAssert.Contains(html, "name=\"code\"");
                Assert.IsFalse(html.Contains("?code=", StringComparison.Ordinal));
                using var state = JsonDocument.Parse(await File.ReadAllTextAsync(stateFile));
                instanceId = state.RootElement.GetProperty("InstanceId").GetString()!;
            }

            await using (var restarted = PairingFactory(stateFile))
            {
                using var client = restarted.CreateClient();
                using var pairing = await client.GetAsync(AepEnrollmentProtocol.PairingPath);
                Assert.AreEqual(HttpStatusCode.OK, pairing.StatusCode);
                using var state = JsonDocument.Parse(await File.ReadAllTextAsync(stateFile));
                Assert.AreEqual(instanceId, state.RootElement.GetProperty("InstanceId").GetString());
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task PairingCredentialRotationOverlapsThenRevokesWithoutReopeningEnrollment()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"aep-pairing-lifecycle-{Guid.NewGuid():N}");
        var stateFile = Path.Combine(directory, "state.json");
        var instanceId = Guid.NewGuid();
        var clientId = "agentstration:lifecycle-test";
        var original = AepStaticBearerCredentials.Generate(clientId).AccessToken;
        var replacement = AepStaticBearerCredentials.Generate(clientId).AccessToken;
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(stateFile, JsonSerializer.Serialize(new
        {
            InstanceId = instanceId,
            Status = "paired",
            ClientId = clientId,
            TokenDigest = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(original)))
        }));
        try
        {
            await using (var factory = PairingFactory(stateFile))
            {
                using var client = factory.CreateClient();
                client.DefaultRequestHeaders.Authorization = new("Bearer", original);
                using var rotated = await client.PostAsJsonAsync(AepEnrollmentProtocol.CredentialRotationPath,
                    new AepCredentialRotation(instanceId, clientId, replacement), AepProtocol.JsonOptions);
                Assert.AreEqual(HttpStatusCode.OK, rotated.StatusCode);

                using var oldStillValid = await client.GetAsync(AepProtocol.DiscoveryPath);
                Assert.AreEqual(HttpStatusCode.OK, oldStillValid.StatusCode);
                using var replacementClient = factory.CreateClient();
                replacementClient.DefaultRequestHeaders.Authorization = new("Bearer", replacement);
                Assert.AreEqual(HttpStatusCode.OK, (await replacementClient.GetAsync(AepProtocol.DiscoveryPath)).StatusCode);

                Assert.AreEqual(HttpStatusCode.OK,
                    (await replacementClient.PostAsync(AepEnrollmentProtocol.PreviousCredentialRevocationPath, null)).StatusCode);
                using (var lifecycleState = JsonDocument.Parse(await File.ReadAllTextAsync(stateFile)))
                    Assert.AreEqual(JsonValueKind.Null, lifecycleState.RootElement.GetProperty("PreviousTokenDigest").ValueKind);
                using var revokedOriginalClient = factory.CreateClient();
                revokedOriginalClient.DefaultRequestHeaders.Authorization = new("Bearer", original);
                Assert.AreEqual(HttpStatusCode.Unauthorized, (await revokedOriginalClient.GetAsync(AepProtocol.DiscoveryPath)).StatusCode);
                Assert.AreEqual(HttpStatusCode.OK,
                    (await replacementClient.PostAsync(AepEnrollmentProtocol.CredentialRevocationPath, null)).StatusCode);
                using var revokedReplacementClient = factory.CreateClient();
                revokedReplacementClient.DefaultRequestHeaders.Authorization = new("Bearer", replacement);
                Assert.AreEqual(HttpStatusCode.Unauthorized, (await revokedReplacementClient.GetAsync(AepProtocol.DiscoveryPath)).StatusCode);
            }

            await using (var restarted = PairingFactory(stateFile))
            {
                using var client = restarted.CreateClient();
                using var closed = await client.PostAsync(AepEnrollmentProtocol.PairingPath,
                    new FormUrlEncodedContent(new Dictionary<string, string> { ["code"] = "123456789" }));
                Assert.AreEqual(HttpStatusCode.Gone, closed.StatusCode);
            }

            AepPairingLifecycle.ResetToUnpaired(stateFile);
            using var state = JsonDocument.Parse(await File.ReadAllTextAsync(stateFile));
            Assert.AreEqual(instanceId.ToString("D"), state.RootElement.GetProperty("InstanceId").GetString());
            Assert.AreEqual("unpaired", state.RootElement.GetProperty("Status").GetString());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task SharedKeyFileEnrollmentAuthenticatesDiscovery()
    {
        var path = Path.GetTempFileName();
        await File.WriteAllTextAsync(path, WorkloadToken + "\n");
        try
        {
            await using var factory = new WebApplicationFactory<global::Program>().WithWebHostBuilder(builder =>
            {
                builder.UseSetting("Aep:EnrollmentMode", "SharedKeyFile");
                builder.UseSetting("Aep:SharedKeyFile:Path", path);
                builder.ConfigureServices((context, services) => services.AddAepEnrollmentAuthentication(context.Configuration));
            });
            using var anonymous = factory.CreateClient();
            using var authenticated = factory.CreateClient();
            authenticated.DefaultRequestHeaders.Authorization = new("Bearer", WorkloadToken);

            Assert.AreEqual(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(AepProtocol.DiscoveryPath)).StatusCode);
            Assert.AreEqual(HttpStatusCode.OK, (await authenticated.GetAsync(AepProtocol.DiscoveryPath)).StatusCode);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public async Task AccessTokensAreAppliedPerRequestWithoutMutatingDefaultHeaders()
    {
        using var firstHandler = new CapturingAuthorizationHandler();
        using var secondHandler = new CapturingAuthorizationHandler();
        using var firstHttp = new HttpClient(firstHandler) { BaseAddress = new Uri("http://first-extension") };
        using var secondHttp = new HttpClient(secondHandler) { BaseAddress = new Uri("http://second-extension") };
        var first = new AepClient(firstHttp, new StaticAepAccessTokenProvider(WorkloadToken));
        var second = new AepClient(secondHttp, new StaticAepAccessTokenProvider(new string('x', 32)));

        await Task.WhenAll(first.GetManifestAsync(), second.GetManifestAsync());

        Assert.AreEqual(WorkloadToken, firstHandler.Token);
        Assert.AreEqual(new string('x', 32), secondHandler.Token);
        Assert.IsNull(firstHttp.DefaultRequestHeaders.Authorization);
        Assert.IsNull(secondHttp.DefaultRequestHeaders.Authorization);
    }

    [TestMethod]
    public async Task StaticBearerAuthenticatesStreamingCalls()
    {
        await using var factory = new WebApplicationFactory<global::Aep.Samples.ModelProvider.Program>()
            .WithWebHostBuilder(builder => builder.ConfigureServices(services =>
                services.AddAepStaticBearerAuthentication(options =>
                    options.AddToken("test-token", "agentstration-test", WorkloadToken))));
        using var httpClient = factory.CreateClient();
        var provider = new AepClient(httpClient, new StaticAepAccessTokenProvider(WorkloadToken)).CreateModelProvider("echo");
        var request = new AepChatRequest("echo-1", [new AepMessage(AepRole.User, [AepContent.FromText("secured")])]);
        var updates = new List<AepChatUpdate>();

        await foreach (var update in provider.ChatStreamingAsync(request)) updates.Add(update);

        Assert.AreEqual(AepFinishReason.Stop, updates.Last().FinishReason);
        Assert.AreEqual("Echo: secured", string.Concat(updates.SelectMany(value => value.Contents).Select(value => value.Text)));
    }

    [TestMethod]
    public void TransportPolicyRequiresHttpsAndBlocksMetadataAndPrivateAddresses()
    {
        var options = new AepTransportSecurityOptions();

        AepTransportSecurity.ValidateEndpoint(new Uri("https://extension.example/aep"), options);
        Assert.ThrowsExactly<AepTransportSecurityException>(() =>
            AepTransportSecurity.ValidateEndpoint(new Uri("http://extension.example/aep"), options));
        Assert.ThrowsExactly<AepTransportSecurityException>(() =>
            AepTransportSecurity.ValidateEndpoint(new Uri("https://169.254.169.254/latest/meta-data"), options));
        Assert.ThrowsExactly<AepTransportSecurityException>(() =>
            AepTransportSecurity.ValidateEndpoint(new Uri("https://10.0.0.8/aep"), options));

        options.AllowedHttpHosts.Add("extension");
        options.AllowedPrivateNetworkHosts.Add("extension");
        AepTransportSecurity.ValidateEndpoint(new Uri("http://extension/aep"), options);
    }

    [TestMethod]
    public async Task DiscoveredCapabilityCannotMoveBearerToAnotherOrigin()
    {
        using var handler = new CrossOriginCapabilityHandler();
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://extension.example/") };
        var client = new AepClient(httpClient, new StaticAepAccessTokenProvider(WorkloadToken));

        var exception = await Assert.ThrowsAsync<AepProtocolException>(() => client.GetConfigurationAsync());

        Assert.AreEqual("endpoint_origin_mismatch", exception.Code);
        Assert.AreEqual(1, handler.RequestCount);
        Assert.AreEqual(WorkloadToken, handler.Token);
    }

    [TestMethod]
    public async Task ClientRejectsOversizedUnaryResponseBeforeDeserialization()
    {
        using var httpClient = new HttpClient(new OversizedResponseHandler()) { BaseAddress = new Uri("https://extension.example/") };
        var options = new AepTransportSecurityOptions { MaximumResponseBytes = 1024 };
        var client = new AepClient(httpClient, transportOptions: options);

        var exception = await Assert.ThrowsAsync<AepProtocolException>(() => client.GetManifestAsync());

        Assert.AreEqual("response_too_large", exception.Code);
    }

    [TestMethod]
    public async Task ValidatorAcceptsTheGenericSample()
    {
        await using var factory = new WebApplicationFactory<global::Program>();
        using var httpClient = factory.CreateClient();

        var result = await new AepValidator().ValidateAsync(new AepClient(httpClient));

        Assert.IsTrue(result.IsValid);
        Assert.IsEmpty(result.Issues);
    }

    [TestMethod]
    public async Task TracingRedactsSensitiveHeadersAndJsonValues()
    {
        var sink = new MemoryTraceSink();
        using var handler = new AepTracingHandler(sink) { InnerHandler = new StaticHandler() };
        using var client = new HttpClient(handler);
        using var request = new HttpRequestMessage(HttpMethod.Post, "http://extension/test") { Content = new StringContent("{\"apiKey\":\"secret-value\"}", Encoding.UTF8, "application/json") };
        request.Headers.Authorization = new("Bearer", "secret-token");

        using var response = await client.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("***", sink.Trace!.RequestHeaders["Authorization"]);
        StringAssert.Contains(sink.Trace.RequestBody, "\"apiKey\":\"***\"");
        Assert.IsFalse(sink.Trace.RequestBody!.Contains("secret-value", StringComparison.Ordinal));
    }

    [TestMethod]
    public void CoreAssembliesDoNotReferenceAgentstrationApplicationProjects()
    {
        var forbidden = new[]
        {
            "Agentstration.Management",
            "Agentstration.Runtime",
            "Agentstration.Infrastructure",
            "Agentstration.Web",
            "Agentstration.Workplace"
        };
        var assemblies = new[] { typeof(AepProtocol).Assembly, typeof(AepClient).Assembly, typeof(AepValidator).Assembly };
        var references = assemblies.SelectMany(value => value.GetReferencedAssemblies()).Select(value => value.Name ?? "").ToArray();

        Assert.IsFalse(references.Any(reference => forbidden.Any(value => reference.Contains(value, StringComparison.Ordinal))));
    }

    [TestMethod]
    public async Task InspectorSessionExercisesGenericModelProviderWithStreamingAndTraces()
    {
        await using var factory = new WebApplicationFactory<global::Aep.Samples.ModelProvider.Program>();
        _ = factory.CreateClient();
        await using var session = new InspectorSession(NullLoggerFactory.Instance, () => factory.Server.CreateHandler());

        var snapshot = await session.ConnectAsync("http://extension");
        var provider = await session.InspectProviderAsync("echo");
        var updates = new List<string>();
        var response = await session.ChatAsync("echo", "echo-1", "hello", null, 0.2f, 128, true, value => { updates.Add(value); return Task.CompletedTask; });

        Assert.IsTrue(snapshot.Manifest.Capabilities.ContainsKey(AepCapabilityNames.ModelProvider));
        Assert.AreEqual("available", provider.Health.Status);
        Assert.AreEqual("echo-1", provider.Models.Single().Id);
        Assert.AreEqual("Echo: hello", response.Text);
        Assert.AreEqual("Echo: hello", updates.Last());
        Assert.IsTrue(session.Traces.Any(value => value.Url?.AbsolutePath.EndsWith("/chat/stream", StringComparison.Ordinal) == true));
    }

    [TestMethod]
    public async Task CanonicalClientStopsReadingAfterTerminalStreamingUpdate()
    {
        using var handler = new TerminalStreamingHandler();
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://extension") };
        var client = new AepClient(httpClient).CreateModelProvider("test");
        var request = new AepChatRequest("test-model", [new AepMessage(AepRole.User, [AepContent.FromText("hello")])]);

        var readTask = ReadAllAsync(client.ChatStreamingAsync(request));
        var updates = await readTask.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.HasCount(1, updates);
        Assert.AreEqual(AepFinishReason.Stop, updates[0].FinishReason);

        static async Task<List<AepChatUpdate>> ReadAllAsync(IAsyncEnumerable<AepChatUpdate> source)
        {
            var updates = new List<AepChatUpdate>();
            await foreach (var update in source) updates.Add(update);
            return updates;
        }
    }

    [TestMethod]
    public async Task AepServerStopsEnumeratingAfterTerminalStreamingUpdate()
    {
        await using var factory = new WebApplicationFactory<global::Aep.Samples.ModelProvider.Program>()
            .WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            {
                services.RemoveAll<IAepModelProvider>();
                services.AddSingleton<IAepModelProvider, TerminalThenTrailingProvider>();
            }));
        using var httpClient = factory.CreateClient();
        var request = new AepChatRequest("test-model", [new AepMessage(AepRole.User, [AepContent.FromText("hello")])]);

        using var response = await httpClient.PostAsJsonAsync($"{AepProtocol.ModelProvidersPath}/terminal/chat/stream", request, AepProtocol.JsonOptions);
        var payload = await response.Content.ReadAsStringAsync();

        response.EnsureSuccessStatusCode();
        Assert.AreEqual(1, payload.Split("data:", StringSplitOptions.None).Length - 1);
        StringAssert.Contains(payload, "\"finishReason\":\"stop\"");
        Assert.IsFalse(payload.Contains("must-not-be-written", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task InspectorSessionDiscoversAndInvokesGenericMcpTool()
    {
        await using var factory = new WebApplicationFactory<global::Aep.Samples.Tools.Program>();
        _ = factory.CreateClient();
        await using var session = new InspectorSession(NullLoggerFactory.Instance, () => factory.Server.CreateHandler());

        var snapshot = await session.ConnectAsync("http://extension");
        var tools = await session.LoadToolsAsync();
        var result = await session.InvokeToolAsync("text.repeat", "{\"text\":\"hi\",\"count\":2}");

        Assert.IsTrue(snapshot.Manifest.Capabilities.ContainsKey(AepCapabilityNames.Tools));
        Assert.AreEqual("text.repeat", tools.Single().Id);
        StringAssert.Contains(result.RawResult, "hi hi");
        Assert.IsTrue(session.Traces.Any(value => value.Url?.AbsolutePath == "/mcp"));
    }

    [TestMethod]
    public async Task SourceProviderResolvesAndMaterializesExactRevisionOffline()
    {
        await using var factory = new WebApplicationFactory<global::Aep.Samples.SourceProvider.Program>();
        using var httpClient = factory.CreateClient();
        var client = new AepClient(httpClient);
        var provider = client.CreateSourceProvider("deterministic");
        var configuration = await SourceConfigurationAsync(client, "main");

        var resolved = await provider.ResolveAsync(new(configuration));
        var materialized = await provider.MaterializeAsync(new(
            configuration,
            resolved.Revision,
            new(1024, 10, 4096, 5)));

        Assert.IsTrue((await client.GetManifestAsync()).Capabilities.ContainsKey(AepCapabilityNames.SourceProvider));
        Assert.AreEqual("deterministic", (await client.ListSourceProvidersAsync()).Single().Id);
        var optionSet = (await client.GetConfigurationAsync()).OptionSets.Single();
        Assert.AreEqual(AepContributionKinds.SourceProvider, optionSet.ContributionKind);
        Assert.AreEqual(AepOptionScopes.SourceChannel, optionSet.Scope);
        Assert.AreEqual(64, resolved.Revision.Length);
        Assert.AreEqual(resolved.Revision, materialized.Revision);
        Assert.AreEqual("application/zip", materialized.Archive.MediaType);
        Assert.AreEqual(AepContentIntegrity.Sha256(materialized.Archive.Content), materialized.Archive.Integrity);
    }

    [TestMethod]
    public async Task SourceProviderRejectsSchemaInvalidChannelOptionsBeforeInvocation()
    {
        await using var factory = new WebApplicationFactory<global::Aep.Samples.SourceProvider.Program>();
        using var httpClient = factory.CreateClient();
        var client = new AepClient(httpClient);
        var configuration = await SourceConfigurationAsync(client, "main");
        configuration = configuration with { Values = JsonSerializer.SerializeToElement(new { unsupported = true }) };

        var exception = await Assert.ThrowsAsync<AepProtocolException>(() =>
            client.CreateSourceProvider("deterministic").ResolveAsync(new(configuration)));

        Assert.AreEqual("invalid_options", exception.Code);
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, exception.StatusCode);
    }

    [TestMethod]
    public async Task SourceProviderFailsClosedOnRevisionMismatch()
    {
        await using var factory = SourceFactory<InvalidSourceProvider>();
        using var httpClient = factory.CreateClient();
        var client = new AepClient(httpClient);
        var configuration = await SourceConfigurationAsync(client, "main");
        var provider = client.CreateSourceProvider("deterministic");

        var revision = (await provider.ResolveAsync(new(configuration))).Revision;
        var exception = await Assert.ThrowsAsync<AepProtocolException>(() => provider.MaterializeAsync(new(
            configuration,
            revision,
            new(1024, 10, 4096, 5))));

        Assert.AreEqual("source_revision_mismatch", exception.Code);
        Assert.AreEqual(HttpStatusCode.BadGateway, exception.StatusCode);
    }

    [TestMethod]
    public async Task SourceProviderFailsClosedOnIntegrityMismatch()
    {
        await using var factory = SourceFactory<InvalidIntegritySourceProvider>();
        using var httpClient = factory.CreateClient();
        var client = new AepClient(httpClient);
        var configuration = await SourceConfigurationAsync(client, "main");
        var provider = client.CreateSourceProvider("deterministic");
        var revision = (await provider.ResolveAsync(new(configuration))).Revision;

        var exception = await Assert.ThrowsAsync<AepProtocolException>(() => provider.MaterializeAsync(new(
            configuration,
            revision,
            new(1024, 10, 4096, 5))));

        Assert.AreEqual("source_integrity_mismatch", exception.Code);
        Assert.AreEqual(HttpStatusCode.BadGateway, exception.StatusCode);
    }

    [TestMethod]
    public async Task SourceProviderEnforcesArchiveLimits()
    {
        await using var factory = SourceFactory<OversizedSourceProvider>();
        using var httpClient = factory.CreateClient();
        var client = new AepClient(httpClient);
        var configuration = await SourceConfigurationAsync(client, "main");
        var provider = client.CreateSourceProvider("deterministic");
        var revision = (await provider.ResolveAsync(new(configuration))).Revision;

        var exception = await Assert.ThrowsAsync<AepProtocolException>(() => provider.MaterializeAsync(new(
            configuration,
            revision,
            new(4, 1, 4, 5))));

        Assert.AreEqual("source_archive_too_large", exception.Code);
        Assert.AreEqual(HttpStatusCode.RequestEntityTooLarge, exception.StatusCode);
    }

    [TestMethod]
    public async Task SourceProviderPropagatesCallerCancellation()
    {
        await using var factory = SourceFactory<WaitingSourceProvider>();
        using var httpClient = factory.CreateClient();
        var client = new AepClient(httpClient);
        var configuration = await SourceConfigurationAsync(client, "cancel");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAsync<TaskCanceledException>(() =>
            client.CreateSourceProvider("deterministic").ResolveAsync(new(configuration), cancellation.Token));
    }

    [TestMethod]
    public async Task SourceProviderMaterializationTimesOutSafely()
    {
        await using var factory = SourceFactory<WaitingSourceProvider>();
        using var httpClient = factory.CreateClient();
        var client = new AepClient(httpClient);
        var configuration = await SourceConfigurationAsync(client, "main");
        var provider = client.CreateSourceProvider("deterministic");
        var revision = (await provider.ResolveAsync(new(configuration))).Revision;

        var exception = await Assert.ThrowsAsync<AepProtocolException>(() => provider.MaterializeAsync(new(
            configuration,
            revision,
            new(1024, 10, 4096, 1))));

        Assert.AreEqual("source_provider_timeout", exception.Code);
        Assert.AreEqual(HttpStatusCode.GatewayTimeout, exception.StatusCode);
    }

    [TestMethod]
    public async Task SourceProviderClientRejectsMalformedResponses()
    {
        using var httpClient = new HttpClient(new MalformedSourceHandler()) { BaseAddress = new Uri("http://extension") };
        var configuration = new AepVersionedOptions("test/source-channel", "1.0", $"sha256:{new string('0', 64)}", JsonSerializer.SerializeToElement(new { }));

        var exception = await Assert.ThrowsAsync<AepProtocolException>(() =>
            new AepClient(httpClient).CreateSourceProvider("test").ResolveAsync(new(configuration)));

        Assert.AreEqual("invalid_response", exception.Code);
    }

    private static async Task<AepVersionedOptions> SourceConfigurationAsync(AepClient client, string selector)
    {
        var optionSet = (await client.GetConfigurationAsync()).OptionSets.Single();
        var version = optionSet.Versions.Single(value => value.Version == optionSet.PreferredVersion);
        return new(optionSet.Id, version.Version, version.SchemaDigest, JsonSerializer.SerializeToElement(new { selector }));
    }

    private static WebApplicationFactory<global::Aep.Samples.SourceProvider.Program> SourceFactory<TProvider>()
        where TProvider : class, IAepSourceProvider =>
        new WebApplicationFactory<global::Aep.Samples.SourceProvider.Program>().WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IAepSourceProvider>();
                services.AddSingleton<IAepSourceProvider, TProvider>();
            }));

    private static WebApplicationFactory<global::Program> AuthenticatedFactory(params string[] permissions) =>
        new WebApplicationFactory<global::Program>().WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddAepStaticBearerAuthentication(options =>
                options.AddToken("test-token", "agentstration-test", WorkloadToken, permissions))));

    private static WebApplicationFactory<global::Program> PairingFactory(string stateFile)
    {
        var values = new Dictionary<string, string?>
        {
            ["Aep:EnrollmentMode"] = "PairingCode",
            ["Aep:PairingCode:AuthorityUrl"] = "http://127.0.0.1:1/",
            ["Aep:PairingCode:AllowInsecureHttp"] = "true",
            ["Aep:PairingCode:PublicEndpoint"] = "https://extension.example/",
            ["Aep:PairingCode:PairingUri"] = "https://extension.example/aep/enrollment/pair",
            ["Aep:PairingCode:TenantId"] = Guid.NewGuid().ToString("D"),
            ["Aep:PairingCode:WorkspaceId"] = Guid.NewGuid().ToString("D"),
            ["Aep:PairingCode:StateFile"] = stateFile
        };
        return new WebApplicationFactory<global::Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(values));
            builder.ConfigureServices((context, services) => services.AddAepEnrollmentAuthentication(context.Configuration));
        });
    }

    private sealed class MemoryTraceSink : IAepHttpTraceSink
    {
        public AepHttpTrace? Trace { get; private set; }
        public ValueTask RecordAsync(AepHttpTrace trace, CancellationToken cancellationToken = default) { Trace = trace; return ValueTask.CompletedTask; }
    }

    private sealed class StaticHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { token = "response-secret" }), Encoding.UTF8, "application/json")
        });
    }

    private sealed class CapturingAuthorizationHandler : HttpMessageHandler
    {
        public string? Token { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Token = request.Headers.Authorization?.Parameter;
            var manifest = new AepManifest(
                AepProtocol.Version,
                new AepExtensionIdentity("test", "Test", "1.0.0"),
                new Dictionary<string, AepCapabilityDescriptor>(),
                new AepContributions([], []));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(manifest, options: AepProtocol.JsonOptions)
            });
        }
    }

    private sealed class CrossOriginCapabilityHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        public string? Token { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            Token = request.Headers.Authorization?.Parameter;
            var manifest = new AepManifest(
                AepProtocol.Version,
                new AepExtensionIdentity("test", "Test", "1.0.0"),
                new Dictionary<string, AepCapabilityDescriptor>
                {
                    [AepCapabilityNames.Configuration] = new("1.0", "https://attacker.example/aep/configuration")
                },
                new AepContributions([], []));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(manifest, options: AepProtocol.JsonOptions)
            });
        }
    }

    private sealed class OversizedResponseHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(new string('x', 2048))
            });
    }

    private sealed class TerminalStreamingHandler : HttpMessageHandler
    {
        private readonly List<Pipe> _openStreams = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath == AepProtocol.DiscoveryPath)
            {
                var manifest = new AepManifest(
                    AepProtocol.Version,
                    new AepExtensionIdentity("test", "Test", "1.0.0"),
                    new Dictionary<string, AepCapabilityDescriptor>
                    {
                        [AepCapabilityNames.ModelProvider] = new("1.0")
                    },
                    new AepContributions([]));
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(manifest, options: AepProtocol.JsonOptions) };
            }

            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath.EndsWith("/chat/stream", StringComparison.Ordinal) == true)
            {
                var pipe = new Pipe();
                _openStreams.Add(pipe);
                var terminalUpdate = new AepChatUpdate([], AepRole.Assistant, "test-model", AepFinishReason.Stop);
                var payload = $"data: {JsonSerializer.Serialize(terminalUpdate, AepProtocol.JsonOptions)}\n\n";
                await pipe.Writer.WriteAsync(Encoding.UTF8.GetBytes(payload), cancellationToken);
                var content = new StreamContent(pipe.Reader.AsStream());
                content.Headers.ContentType = new("text/event-stream");
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    private sealed class TerminalThenTrailingProvider : IAepModelProvider
    {
        public AepModelProviderDescriptor Descriptor { get; } = new(
            "terminal",
            "Terminal provider",
            new AepModelProviderCapabilities(),
            [new AepModelDescriptor("test-model", "Test model")]);

        public Task<AepChatResponse> ChatAsync(AepChatRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new AepChatResponse([], FinishReason: AepFinishReason.Stop));

        public async IAsyncEnumerable<AepChatUpdate> ChatStreamingAsync(
            AepChatRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new AepChatUpdate([], AepRole.Assistant, "test-model", AepFinishReason.Stop);
            yield return new AepChatUpdate([AepContent.FromText("must-not-be-written")], AepRole.Assistant, "test-model");
            await Task.CompletedTask;
        }
    }

    private sealed class MalformedSourceHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath == AepProtocol.DiscoveryPath)
            {
                var manifest = new AepManifest(
                    AepProtocol.Version,
                    new("test", "Test", "1.0.0"),
                    new Dictionary<string, AepCapabilityDescriptor> { [AepCapabilityNames.SourceProvider] = new("1.0", AepProtocol.SourceProvidersPath) },
                    new([], SourceProviders: [new("test", "Test source")]));
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(manifest, options: AepProtocol.JsonOptions) });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{ malformed", Encoding.UTF8, "application/json") });
        }
    }

    public abstract class TestSourceProvider : IAepSourceProvider
    {
        public AepSourceProviderDescriptor Descriptor { get; } = new("deterministic", "Test source provider");

        public virtual Task<AepSourceResolveResponse> ResolveAsync(AepSourceResolveRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new AepSourceResolveResponse(new string('a', 64), new("sha256", new string('a', 64))));

        public abstract Task<AepSourceMaterializeResponse> MaterializeAsync(AepSourceMaterializeRequest request, CancellationToken cancellationToken);
    }

    public sealed class InvalidSourceProvider : TestSourceProvider
    {
        public override Task<AepSourceMaterializeResponse> MaterializeAsync(AepSourceMaterializeRequest request, CancellationToken cancellationToken)
        {
            var content = Encoding.UTF8.GetBytes("fixture");
            return Task.FromResult(new AepSourceMaterializeResponse("different", new("application/zip", content, content.Length, 1, AepContentIntegrity.Sha256(content))));
        }
    }

    public sealed class OversizedSourceProvider : TestSourceProvider
    {
        public override Task<AepSourceMaterializeResponse> MaterializeAsync(AepSourceMaterializeRequest request, CancellationToken cancellationToken)
        {
            var content = Encoding.UTF8.GetBytes("oversized");
            return Task.FromResult(new AepSourceMaterializeResponse(request.Revision, new("application/zip", content, content.Length, 1, AepContentIntegrity.Sha256(content))));
        }
    }

    public sealed class InvalidIntegritySourceProvider : TestSourceProvider
    {
        public override Task<AepSourceMaterializeResponse> MaterializeAsync(AepSourceMaterializeRequest request, CancellationToken cancellationToken)
        {
            var content = Encoding.UTF8.GetBytes("fixture");
            return Task.FromResult(new AepSourceMaterializeResponse(request.Revision, new("application/zip", content, content.Length, 1, new("sha256", new string('0', 64)))));
        }
    }

    public sealed class WaitingSourceProvider : TestSourceProvider
    {
        public override async Task<AepSourceResolveResponse> ResolveAsync(AepSourceResolveRequest request, CancellationToken cancellationToken)
        {
            if (request.Configuration.Values.TryGetProperty("selector", out var selector)
                && string.Equals(selector.GetString(), "cancel", StringComparison.Ordinal))
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException();
            }
            return await base.ResolveAsync(request, cancellationToken);
        }

        public override async Task<AepSourceMaterializeResponse> MaterializeAsync(AepSourceMaterializeRequest request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException();
        }
    }
}
