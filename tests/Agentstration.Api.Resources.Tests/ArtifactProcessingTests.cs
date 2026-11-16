using System.Text;
using Agentstration.Schemas;
using System.Text.Json;
using Agentstration.Artifacts;
using Agentstration.Artifacts.Contracts;
using Agentstration.Flows;
using Agentstration.Flows.Storage.Abstractions;
using Agentstration.Identity.Contracts;
using Agentstration.Infrastructure.Artifacts;
using Agentstration.Infrastructure.Flows;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Management.Tests;

[TestClass]
public sealed class ArtifactProcessingTests : ResourceManagementApiTestBase
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ForeachArtifactsPreparesAnExplodedManifestBeforeRegisteringAnyChild(bool transient)
    {
        await using var factory = Factory();
        var current = await GetBootstrapContextAsync(factory);
        using var scope = factory.Services.GetRequiredService<IRequestContextScopeFactory>().Push(current);
        var artifacts = factory.Services.GetRequiredService<ArtifactManagementService>();
        var processor = factory.Services.GetRequiredService<ArtifactProcessingService>();
        var (owner, step, _, item) = await SetupAsync(factory.Services, current, ArtifactProcessingMode.Content,
            "{\"results\":[{\"id\":\"a\"},{\"id\":\"b\"}]}", "application/json", transient: transient);
        await processor.ExecuteOwnerAsync(owner, step, 0, "open", item, default);
        var function = factory.Services.GetRequiredService<Agentstration.Functions.NativeFunctionRegistry>().Resolve("artifact.explode", "1.0.0");
        var manifest = await function.ExecuteAsync(Element(new { value = new { content = Element(new { results = new[] { new { id = "a" }, new { id = "b" } } }) }, path = "/results" }), default);
        var prepared = await processor.ExecuteOwnerAsync(owner, step, 0, "prepare", manifest, default);
        Assert.AreEqual(2, prepared.GetProperty("outputCount").GetInt32());
        Assert.IsEmpty(await artifacts.ListFlowRunArtifactsAsync(default));
        var output = await processor.ExecuteOwnerAsync(owner, step, 0, "finish", Element(new { }), default);
        Assert.AreEqual(2, output.GetProperty("artifacts").GetArrayLength());
        Assert.IsTrue(JsonElement.DeepEquals(output, await processor.ExecuteOwnerAsync(owner, step, 0, "finish", Element(new { }), default)));
        if (transient)
        {
            Assert.IsEmpty(await artifacts.ListStagedAsync(default));
            Assert.IsEmpty(await artifacts.ListFlowRunArtifactsAsync(default));
            Assert.IsEmpty(await factory.Services.GetRequiredService<IRuntimeExecutionStateStore>().ListAsync(owner.WorkspaceId, owner.Id, "artifact-processing-v1", null, default));
        }
        else
        {
            var children = await artifacts.ListFlowRunArtifactsAsync(default);
            Assert.HasCount(2, children);
            Assert.IsTrue(children.All(child => child.Value.Receipt.Provenance["inputArtifactId"] == item.GetProperty("artifactId").GetString()));
        }
    }

    private static readonly JsonSerializerOptions ContractJson = new(JsonSerializerDefaults.Web);
    private static JsonElement Element<T>(T value) => JsonSerializer.SerializeToElement(value);

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task AccessProcessingPinsInputAndSupportsReadSearchWriteCorrectionAndVerifiedPublication(bool transient)
    {
        await using var factory = Factory();
        var current = await GetBootstrapContextAsync(factory);
        using var scope = factory.Services.GetRequiredService<IRequestContextScopeFactory>().Push(current);
        var artifacts = factory.Services.GetRequiredService<ArtifactManagementService>();
        var processor = factory.Services.GetRequiredService<ArtifactProcessingService>();
        var (owner, step, child, item) = await SetupAsync(factory.Services, current, ArtifactProcessingMode.Access, "Alice 😀 alice@example.test", transient: transient);
        var opened = await processor.ExecuteOwnerAsync(owner, step, 0, "open", item, default);
        Assert.AreEqual(JsonValueKind.Null, opened.GetProperty("content").ValueKind);
        Assert.AreEqual("Alice 😀 alice@example.test", (await processor.ExecuteToolAsync(child, "input_read", Element(new { }), default)).GetProperty("content").GetString());
        var search = await processor.ExecuteToolAsync(child, "input_search_regex", Element(new { pattern = "alice@[^ ]+" }), default);
        Assert.AreEqual(1, search.GetProperty("matches").GetArrayLength());
        var first = Element(new { content = "Alice 😀", expectedRevision = 0, requestId = "first" });
        Assert.AreEqual(1, (await processor.ExecuteToolAsync(child, "output_write", first, default)).GetProperty("revision").GetInt32());
        Assert.AreEqual(1, (await processor.ExecuteToolAsync(child, "output_write", first, default)).GetProperty("revision").GetInt32());
        var partial = await processor.ExecuteToolAsync(child, "output_read", Element(new { length = 3 }), default);
        var oldCursor = partial.GetProperty("cursor").GetString();
        await processor.ExecuteToolAsync(child, "output_complete", Element(new { expectedRevision = 1 }), default);
        await processor.ExecuteToolAsync(child, "output_write", Element(new { content = "PERSON_1", expectedRevision = 1, requestId = "correct", offset = 0, deleteLength = 5 }), default);
        Assert.IsFalse((await processor.ExecuteToolAsync(child, "output_info", Element(new { }), default)).GetProperty("completed").GetBoolean());
        var stale = await Assert.ThrowsExactlyAsync<RuntimeGovernedOperationException>(() => processor.ExecuteToolAsync(child, "output_read", Element(new { cursor = oldCursor }), default));
        Assert.AreEqual("artifact_processing_cursor_invalid", stale.Code);
        Assert.AreEqual("PERSON_1 😀", (await processor.ExecuteToolAsync(child, "output_read", Element(new { }), default)).GetProperty("content").GetString());
        Assert.AreEqual(0, (await processor.ExecuteToolAsync(child, "output_search_regex", Element(new { pattern = "Alice|@" }), default)).GetProperty("matches").GetArrayLength());
        await processor.ExecuteToolAsync(child, "output_complete", Element(new { expectedRevision = 2 }), default);
        await processor.ExecuteOwnerAsync(owner, step, 0, "prepare", Element(new { }), default);
        Assert.IsEmpty(await artifacts.ListFlowRunArtifactsAsync(default));
        var result = await processor.ExecuteOwnerAsync(owner, step, 0, "finish", Element(new { }), default);
        var replay = await processor.ExecuteOwnerAsync(owner, step, 0, "finish", Element(new { }), default);
        Assert.IsTrue(JsonElement.DeepEquals(result, replay));
        if (transient)
        {
            Assert.AreEqual("inline", result.GetProperty("kind").GetString());
            Assert.AreEqual("PERSON_1 😀", Encoding.UTF8.GetString(Convert.FromBase64String(result.GetProperty("contentBase64").GetString()!)));
            Assert.IsEmpty(await artifacts.ListStagedAsync(default));
            Assert.IsEmpty(await artifacts.ListFlowRunArtifactsAsync(default));
            Assert.IsEmpty(await factory.Services.GetRequiredService<IRuntimeExecutionStateStore>().ListAsync(owner.WorkspaceId, owner.Id, "artifact-processing-v1", null, default));
        }
        else
        {
            var output = (await artifacts.GetFlowRunArtifactAsync(FlowRunArtifactId.Parse(result.GetProperty("artifactId").GetString()!), default))!.Value;
            Assert.AreEqual(owner.Id, output.ProducerFlowRunId);
            Assert.AreEqual(step.Name, output.ProducerFlowStepId);
            Assert.AreEqual(item.GetProperty("artifactId").GetString(), output.Receipt.Provenance["inputArtifactId"]);
            var bytes = await factory.Services.GetRequiredService<IArtifactDurableStore>().ReadAsync(owner.WorkspaceId, output.Receipt.OpaqueReference, 0, 1024, default);
            Assert.AreEqual("PERSON_1 😀", Encoding.UTF8.GetString(bytes.Span));
            Assert.AreEqual("Alice 😀 alice@example.test", (await artifacts.ReadAsync(StagedArtifactId.Parse(item.GetProperty("artifactId").GetString()!), 0, 1024, null, default)).ContentBase64 is var encoded ? Encoding.UTF8.GetString(Convert.FromBase64String(encoded)) : null);
            Assert.IsFalse(string.IsNullOrWhiteSpace(output.Receipt.Sha256));
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task AccessProcessingRefusesForeignCursorsStaleWritesConflictingReplayAndUncompletedOutput(bool transient)
    {
        await using var factory = Factory();
        var current = await GetBootstrapContextAsync(factory);
        using var scope = factory.Services.GetRequiredService<IRequestContextScopeFactory>().Push(current);
        var processor = factory.Services.GetRequiredService<ArtifactProcessingService>();
        var (owner, step, child, item) = await SetupAsync(factory.Services, current, ArtifactProcessingMode.Access, "private content", transient: transient);
        await processor.ExecuteOwnerAsync(owner, step, 0, "open", item, default);
        await processor.ExecuteOwnerAsync(owner, step, 1, "open", item, default);
        var read = await processor.ExecuteToolAsync(child, "input_read", Element(new { length = 3 }), default);
        var other = child with { ArtifactProcessing = child.ArtifactProcessing! with { Index = 1 } };
        var foreign = await Assert.ThrowsExactlyAsync<RuntimeGovernedOperationException>(() => processor.ExecuteToolAsync(other, "input_read", Element(new { cursor = read.GetProperty("cursor").GetString() }), default));
        Assert.AreEqual("artifact_processing_cursor_invalid", foreign.Code);
        var write = Element(new { content = "redacted", expectedRevision = 0, requestId = "write" });
        await processor.ExecuteToolAsync(child, "output_write", write, default);
        var stale = await Assert.ThrowsExactlyAsync<RuntimeGovernedOperationException>(() => processor.ExecuteToolAsync(child, "output_write", Element(new { content = "stale", expectedRevision = 0, requestId = "stale" }), default));
        Assert.AreEqual("artifact_processing_revision_conflict", stale.Code);
        var conflict = await Assert.ThrowsExactlyAsync<RuntimeGovernedOperationException>(() => processor.ExecuteToolAsync(child, "output_write", Element(new { content = "changed", expectedRevision = 0, requestId = "write" }), default));
        Assert.AreEqual("artifact_processing_write_replay_conflict", conflict.Code);
        var incomplete = await Assert.ThrowsExactlyAsync<RuntimeGovernedOperationException>(() => processor.ExecuteOwnerAsync(owner, step, 0, "prepare", Element(new { }), default));
        Assert.AreEqual("artifact_processing_output_incomplete", incomplete.Code);
        var unrelated = child with { RootFlowRunId = "unrelated-root" };
        var denied = await Assert.ThrowsExactlyAsync<RuntimeGovernedOperationException>(() => processor.ExecuteToolAsync(unrelated, "input_read", Element(new { }), default));
        Assert.AreEqual("artifact_processing_scope_invalid", denied.Code);
        await processor.ExecuteOwnerAsync(owner, step, 0, "abort", Element(new { }), default);
        var closed = await Assert.ThrowsExactlyAsync<RuntimeGovernedOperationException>(() => processor.ExecuteToolAsync(child, "input_read", Element(new { }), default));
        Assert.AreEqual("artifact_processing_expired", closed.Code);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ContentProcessingCapturesStructuredResultsAndRefusesOversizedInput(bool transient)
    {
        await using var factory = Factory();
        var current = await GetBootstrapContextAsync(factory);
        using var scope = factory.Services.GetRequiredService<IRequestContextScopeFactory>().Push(current);
        var processor = factory.Services.GetRequiredService<ArtifactProcessingService>();
        var (owner, step, _, item) = await SetupAsync(factory.Services, current, ArtifactProcessingMode.Content, "{\"name\":\"Alice\"}", "application/json", transient: transient);
        var opened = await processor.ExecuteOwnerAsync(owner, step, 0, "open", item, default);
        Assert.AreEqual("Alice", opened.GetProperty("content").GetProperty("name").GetString());
        await processor.ExecuteOwnerAsync(owner, step, 0, "prepare", Element(new { name = "PERSON_1" }), default);
        var result = await processor.ExecuteOwnerAsync(owner, step, 0, "finish", Element(new { }), default);
        Assert.AreEqual(transient ? "inline" : "durable", result.GetProperty("kind").GetString());
        var tooSmall = step with { MaximumContentBytes = 4 };
        var failure = await Assert.ThrowsExactlyAsync<RuntimeGovernedOperationException>(() => processor.ExecuteOwnerAsync(owner, tooSmall, 1, "open", item, default));
        Assert.AreEqual("artifact_processing_content_too_large", failure.Code);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ProcessingContextsDoNotGrantCrossWorkspaceArtifactAccess(bool transient)
    {
        await using var factory = Factory();
        var current = await GetBootstrapContextAsync(factory);
        var scopes = factory.Services.GetRequiredService<IRequestContextScopeFactory>();
        using var scope = scopes.Push(current);
        var processor = factory.Services.GetRequiredService<ArtifactProcessingService>();
        var (owner, step, child, item) = await SetupAsync(factory.Services, current, ArtifactProcessingMode.Access, "workspace secret", transient: transient);
        await processor.ExecuteOwnerAsync(owner, step, 0, "open", item, default);
        var foreignWorkspace = Guid.NewGuid();
        var foreign = child with { Scope = child.Scope with { WorkspaceId = new(foreignWorkspace) } };
        var rejected = await Assert.ThrowsExactlyAsync<RuntimeGovernedOperationException>(() => processor.ExecuteToolAsync(foreign, "input_read", Element(new { }), default));
        Assert.AreEqual("artifact_processing_scope_invalid", rejected.Code);
        using var deniedPrincipal = scopes.Push(new RequestContext(Guid.NewGuid(), current.TenantId, current.WorkspaceId));
        var denied = await Assert.ThrowsExactlyAsync<RuntimeGovernedOperationException>(() => processor.ExecuteToolAsync(child, "input_read", Element(new { }), default));
        Assert.AreEqual("artifact_processing_access_denied", denied.Code);
    }

    [TestMethod]
    public async Task DirectProcessingExpiresWithItsOwnerAndAbortCannotRecreateADraft()
    {
        await using var factory = Factory();
        var current = await GetBootstrapContextAsync(factory);
        using var scope = factory.Services.GetRequiredService<IRequestContextScopeFactory>().Push(current);
        var processor = factory.Services.GetRequiredService<ArtifactProcessingService>();
        var (owner, step, child, item) = await SetupAsync(factory.Services, current, ArtifactProcessingMode.Access, "private content", transient: true);
        await processor.ExecuteOwnerAsync(owner, step, 0, "open", item, default);
        await processor.ExecuteOwnerAsync(owner, step, 0, "abort", Element(new { }), default);
        var aborted = await Assert.ThrowsExactlyAsync<RuntimeGovernedOperationException>(() => processor.ExecuteOwnerAsync(owner, step, 0, "open", item, default));
        Assert.AreEqual("artifact_processing_expired", aborted.Code);
        foreach (var unavailable in new[] { owner with { TransientPayloadsAvailable = false }, owner with { CreatedAt = owner.CreatedAt - DirectExecutionPayloadPersistence.Lifetime } })
        {
            var error = await Assert.ThrowsExactlyAsync<RuntimeGovernedOperationException>(() => processor.ExecuteOwnerAsync(unavailable, step, 1, "open", item, default));
            Assert.AreEqual("direct_content_unavailable", error.Code);
        }
        Assert.IsEmpty(await factory.Services.GetRequiredService<ArtifactManagementService>().ListStagedAsync(default));
    }

    [TestMethod]
    [DataRow(ResourceScopeKind.Instance)]
    [DataRow(ResourceScopeKind.Tenant)]
    public async Task AdministrativeProcessingUsesItsOperationalWorkspaceAndRestoresTheLogicalContext(ResourceScopeKind kind)
    {
        await using var factory = Factory();
        var bootstrap = await GetBootstrapContextAsync(factory);
        var scopes = factory.Services.GetRequiredService<IRequestContextScopeFactory>();
        var current = factory.Services.GetRequiredService<ICurrentRequestContext>();
        var topology = factory.Services.GetRequiredService<IOperationalTopologyProvisioner>();
        var workspace = kind == ResourceScopeKind.Instance ? await topology.GetInstanceOperationsWorkspaceAsync(default)
            : await topology.GetTenantOperationsWorkspaceAsync(bootstrap.TenantId, default);
        var context = new RequestContext(bootstrap.PrincipalId, workspace.TenantId, workspace.Id);
        var ownerScope = kind == ResourceScopeKind.Instance ? ResourceScopeRef.Instance : ResourceScopeRef.Tenant(context.TenantId);
        (FlowRun Owner, ForeachArtifactsFlowStepDefinition Step, FlowRun Child, JsonElement Item) fixture;
        using (scopes.Push(context)) fixture = await SetupAsync(factory.Services, context, ArtifactProcessingMode.Access, "Alice", logicalScope: ownerScope);
        var owner = fixture.Owner;
        var child = fixture.Child;
        IDisposable Enter(RequestContext value) => kind == ResourceScopeKind.Instance ? scopes.PushInstance(value) : scopes.PushTenantAdministration(value);
        using var administrative = Enter(context);
        var originalMode = current.AccessMode;
        var processor = factory.Services.GetRequiredService<ArtifactProcessingService>();
        await processor.ExecuteOwnerAsync(owner, fixture.Step, 0, "open", fixture.Item, default);
        Assert.AreEqual(originalMode, current.AccessMode);
        Assert.AreEqual("Alice", (await processor.ExecuteToolAsync(child, "input_read", Element(new { }), default)).GetProperty("content").GetString());
        await processor.ExecuteToolAsync(child, "output_write", Element(new { content = "PERSON_1", expectedRevision = 0, requestId = "anonymize" }), default);
        var incomplete = await Assert.ThrowsExactlyAsync<RuntimeGovernedOperationException>(() => processor.ExecuteOwnerAsync(owner, fixture.Step, 0, "prepare", Element(new { }), default));
        Assert.AreEqual("artifact_processing_output_incomplete", incomplete.Code);
        Assert.AreEqual(originalMode, current.AccessMode);
        await processor.ExecuteToolAsync(child, "output_complete", Element(new { expectedRevision = 1 }), default);
        await processor.ExecuteOwnerAsync(owner, fixture.Step, 0, "prepare", Element(new { }), default);
        var result = await processor.ExecuteOwnerAsync(owner, fixture.Step, 0, "finish", Element(new { }), default);
        Assert.AreEqual(originalMode, current.AccessMode);
        using (Enter(context with { Restriction = new AuthorizationRestriction(Guid.NewGuid(), context.WorkspaceId, new HashSet<string>(StringComparer.Ordinal)) }))
        {
            var denied = await Assert.ThrowsExactlyAsync<RuntimeGovernedOperationException>(() => processor.ExecuteOwnerAsync(owner, fixture.Step, 1, "open", fixture.Item, default));
            Assert.AreEqual("artifact_processing_access_denied", denied.Code);
        }
        Assert.AreEqual(originalMode, current.AccessMode);
        var artifacts = factory.Services.GetRequiredService<ArtifactManagementService>();
        using (scopes.Push(context))
        {
            var output = (await artifacts.GetFlowRunArtifactAsync(FlowRunArtifactId.Parse(result.GetProperty("artifactId").GetString()!), default))!.Value;
            Assert.AreEqual(context.WorkspaceId, output.WorkspaceId.Value);
            Assert.AreEqual(owner.Id, output.ProducerFlowRunId);
        }
        using (scopes.Push(bootstrap))
            Assert.IsNull(await artifacts.GetFlowRunArtifactAsync(FlowRunArtifactId.Parse(result.GetProperty("artifactId").GetString()!), default));
        Assert.AreEqual(originalMode, current.AccessMode);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task JsonOutputFailureExplainsTheFormatMismatchWithoutEchoingContent(bool transient)
    {
        await using var factory = Factory();
        var current = await GetBootstrapContextAsync(factory);
        using var scope = factory.Services.GetRequiredService<IRequestContextScopeFactory>().Push(current);
        var processor = factory.Services.GetRequiredService<ArtifactProcessingService>();
        var (owner, step, _, item) = await SetupAsync(factory.Services, current, ArtifactProcessingMode.Content,
            "{}", "application/json", transient);
        await processor.ExecuteOwnerAsync(owner, step, 0, "open", item, default);
        var error = await Assert.ThrowsAsync<DataContractException>(() =>
            processor.ExecuteOwnerAsync(owner, step, 0, "prepare", Element("private-output-text"), default));
        Assert.AreEqual("data_contract_json_invalid", error.Code);
        Assert.AreEqual("artifact-output", error.Failure.Boundary);
        Assert.AreEqual(step.Name, error.Failure.StepName);
        Assert.Contains("valid JSON", error.Message);
        Assert.Contains("text/plain", error.Message);
        Assert.DoesNotContain("private-output-text", error.Message);
        // A valid replacement can be retried without re-acquiring or publishing partial content.
        await processor.ExecuteOwnerAsync(owner, step, 0, "prepare", Element("{\"redacted\":true}"), default);
    }

    [TestMethod]
    public async Task InvalidJsonInputIsDistinguishedFromInvalidTransformationOutput()
    {
        await using var factory = Factory();
        var current = await GetBootstrapContextAsync(factory);
        using var scope = factory.Services.GetRequiredService<IRequestContextScopeFactory>().Push(current);
        var processor = factory.Services.GetRequiredService<ArtifactProcessingService>();
        var (owner, step, _, item) = await SetupAsync(factory.Services, current, ArtifactProcessingMode.Content,
            "private-invalid-input", "application/json", true);
        var error = await Assert.ThrowsAsync<RuntimeGovernedOperationException>(() =>
            processor.ExecuteOwnerAsync(owner, step, 0, "open", item, default));
        Assert.AreEqual("artifact_processing_input_json_invalid", error.Code);
        Assert.Contains("input Artifact", error.Message);
        Assert.DoesNotContain("private-invalid-input", error.Message);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task SelectedContentContractIsValidatedAndRetainedAcrossArtifactProcessing(bool transient)
    {
        await using var factory = Factory();
        var current = await GetBootstrapContextAsync(factory);
        using var scope = factory.Services.GetRequiredService<IRequestContextScopeFactory>().Push(current);
        var processor = factory.Services.GetRequiredService<ArtifactProcessingService>();
        var schema = Element(new { type = "object", required = new[] { "masked" }, properties = new { masked = new { type = "boolean" } }, additionalProperties = false });
        var (owner, step, _, item) = await SetupAsync(factory.Services, current, ArtifactProcessingMode.Content,
            "{}", "application/json", transient, contract: new() { Schema = schema, MediaType = "application/json" });
        await processor.ExecuteOwnerAsync(owner, step, 0, "open", item, default);
        var invalid = await Assert.ThrowsExactlyAsync<DataContractException>(() => processor.ExecuteOwnerAsync(owner, step, 0,
            "prepare", Element(new { masked = "synthetic-private-value" }), default));
        Assert.AreEqual("$.masked", invalid.Failure.Path);
        Assert.DoesNotContain("synthetic-private-value", invalid.Message);
        await processor.ExecuteOwnerAsync(owner, step, 0, "prepare", Element(new { masked = true }), default);
        var manifest = await processor.ExecuteOwnerAsync(owner, step, 0, "finish", Element(new { }), default);
        var metadata = manifest.GetProperty("dataContract").Deserialize<ResolvedDataContract>(ContractJson)!;
        Assert.IsTrue(metadata.Validated);
        Assert.IsTrue(JsonElement.DeepEquals(schema, metadata.Schema!.Value));
        var (nextOwner, nextStep, _, _) = await SetupAsync(factory.Services, current, ArtifactProcessingMode.Content,
            "{}", "application/json", transient, contract: new() { Mode = DataContractMode.Dynamic, Source = "input" });
        await processor.ExecuteOwnerAsync(nextOwner, nextStep, 0, "open", manifest, default);
        var context = await processor.ContractsAsync(nextOwner, nextStep, 0, default);
        Assert.AreEqual(metadata.Digest, context.ExpectedOutput?.Digest);
        Assert.AreEqual(DataContractMode.Dynamic, context.ExpectedOutput?.DeclaredMode);
    }

    [TestMethod]
    public async Task AccessContractValidatesCompletedDraftSeparatelyFromChatAcknowledgement()
    {
        await using var factory = Factory();
        var current = await GetBootstrapContextAsync(factory);
        using var scope = factory.Services.GetRequiredService<IRequestContextScopeFactory>().Push(current);
        var processor = factory.Services.GetRequiredService<ArtifactProcessingService>();
        var (owner, step, child, item) = await SetupAsync(factory.Services, current, ArtifactProcessingMode.Access,
            "{}", "application/json", transient: true, contract: new() { Schema = Element(new { type = "object" }) });
        await processor.ExecuteOwnerAsync(owner, step, 0, "open", item, default);
        await processor.ExecuteToolAsync(child, "output_write", Element(new { content = "not JSON", expectedRevision = 0, requestId = "invalid" }), default);
        var failure = await Assert.ThrowsExactlyAsync<DataContractException>(() => processor.ExecuteToolAsync(child, "output_complete", Element(new { expectedRevision = 1 }), default));
        Assert.AreEqual("data_contract_json_invalid", failure.Code);
        await processor.ExecuteToolAsync(child, "output_write", Element(new { content = "{}", expectedRevision = 1, requestId = "correct", offset = 0, deleteLength = 8 }), default);
        await processor.ExecuteToolAsync(child, "output_complete", Element(new { expectedRevision = 2 }), default);
        await processor.ExecuteOwnerAsync(owner, step, 0, "prepare", Element("Draft completed."), default);
        var result = await processor.ExecuteOwnerAsync(owner, step, 0, "finish", Element(new { }), default);
        Assert.IsTrue(result.GetProperty("dataContract").GetProperty("validated").GetBoolean());
    }

    private static async Task<(FlowRun Owner, ForeachArtifactsFlowStepDefinition Step, FlowRun Child, JsonElement Item)> SetupAsync(
        IServiceProvider services, RequestContext current, ArtifactProcessingMode mode, string content, string media = "text/plain", bool transient = false, ResourceScopeRef? logicalScope = null, DataContractDefinition? contract = null)
    {
        var artifacts = services.GetRequiredService<ArtifactManagementService>();
        JsonElement item;
        if (transient)
            item = Element(new { artifactId = "direct-input", kind = "inline", name = "source.txt", mediaType = media,
                contentBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(content)) });
        else
        {
            var staged = await artifacts.CreateStagedAsync(new("source.txt", media,
                new() { Kind = ArtifactProducerKind.FlowRun, Id = "input-" + Guid.NewGuid().ToString("N") }), default);
            await artifacts.WriteAsync(staged.Value.ArtifactId, 0, Encoding.UTF8.GetBytes(content), default);
            await artifacts.SealAsync(staged.Value.ArtifactId, default);
            item = Element(new { artifactId = staged.Value.ArtifactId.ToString(), kind = "staged" });
        }
        var step = new ForeachArtifactsFlowStepDefinition { Name = "anonymize", Mode = mode, OutputContract = contract };
        var graph = new FlowGraphDefinition { EntryStep = "input", Steps = [new InputFlowStepDefinition { Name = "input" }, step] };
        var flowId = new FlowId("artifact-processing-test");
        var version = new FlowVersion(logicalScope ?? ResourceScopeRef.Workspace(current.WorkspaceId), flowId, "1.0.0", null,
            new DirectFlowDefinition(new(FlowTargetKind.Agent, "unused")), new Dictionary<string, string>(), DateTimeOffset.UtcNow, graph, FlowDefinitionHash.Compute(graph));
        var owner = new FlowRun { TransientPayloads = transient, Id = "flowrun-" + Guid.NewGuid().ToString("N"), WorkspaceId = new(current.WorkspaceId),
            FlowId = flowId, FlowVersion = version.Version, DefinitionSnapshot = version, DefinitionHash = version.DefinitionHash,
            Scope = new(current.TenantId, new(current.WorkspaceId), current.PrincipalId) { LogicalScopeRef = logicalScope }, Input = Element(new { }),
            CreatedAt = DateTimeOffset.UtcNow, Steps = [new FlowStepRun { StepName = step.Name, StepType = "foreachArtifacts", Attempt = 1 }] };
        await services.GetRequiredService<IFlowRepository>().CreateRunAsync(owner, default);
        var child = owner with { Id = "flowrun-processing-" + Guid.NewGuid().ToString("N"), ParentFlowRunId = owner.Id,
            RootFlowRunId = owner.Id, ArtifactProcessing = new(owner.Id, step.Name, 1, 0) };
        return (owner, step, child, item);
    }
}
