using System.Net;
using System.Text.Json;
using Agentstration.Agents;
using Agentstration.Extensions.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.Models;
using Agentstration.ResourceManagement;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;
using Agentstration.SystemGraph.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Management.Tests;

[TestClass]
public sealed class SystemGraphModelChainApiTests : ModelManagementApiTestBase
{
    [TestMethod]
    [DataRow("full")]
    [DataRow("extension-hidden")]
    [DataRow("provider-discover-only")]
    [DataRow("provider-hidden")]
    public async Task ModelReferencesExposeOnlyTheAuthorizedProviderExtensionChainAcrossOwnersAndNamespaces(string scenario)
    {
        await using var factory = Factory();
        var current = await GetBootstrapContextAsync(factory);
        var scopes = factory.Services.GetRequiredService<IRequestContextScopeFactory>();
        var store = factory.Services.GetRequiredService<IResourceStore>();
        var policies = factory.Services.GetRequiredService<IResourceAccessPolicyService>();
        var actor = ResourceScopeRef.Workspace(current.WorkspaceId);
        var consumers = new ResourceNamespace("model-graph-consumers");
        var providers = new ResourceNamespace("model-graph-providers");
        var extensions = new ResourceNamespace("model-graph-extensions");
        Guid agentUid, profileUid, providerUid, extensionUid, shadowUid;
        using (scopes.PushSystem())
        {
            var extension = await store.PutExactAsync(ResourceScopeRef.Instance, new ExtensionRegistrationResource
            {
                ApiVersion = ResourceApiVersions.CoreV1,
                Kind = ExtensionKinds.ExtensionRegistration,
                ScopeRef = ResourceScopeRef.Instance,
                Metadata = new() { Name = "linked-extension", Namespace = extensions },
                Definition = new()
                {
                    DisplayName = "Linked model extension",
                    Endpoint = new("https://private-extension.invalid/never-read"),
                    Enabled = false,
                    Credential = new("private-extension-credential", ResourceScopeRef.Instance)
                }
            }, null, true, default);
            extensionUid = extension.Value.Uid;
            var provider = await store.PutExactAsync(ResourceScopeRef.Instance, new ModelProviderResource
            {
                ApiVersion = ResourceApiVersions.CoreV1,
                Kind = ModelResourceKinds.ModelProvider,
                ScopeRef = ResourceScopeRef.Instance,
                Metadata = new() { Name = "provider", Namespace = providers },
                Definition = new()
                {
                    DisplayName = "Visible provider",
                    Extension = new("linked-extension", ResourceScopeRef.Instance, extensions),
                    ContributionId = "metadata-only",
                    ValueBindings = [ModelProviderValueBinding.FromLiteral("endpoint", JsonSerializer.SerializeToElement("private-provider-value"))]
                }
            }, null, true, default);
            providerUid = provider.Value.Uid;
            var shadow = await store.PutExactAsync(actor, provider.Value with { Uid = Guid.Empty, ScopeRef = actor }, null, true, default);
            shadowUid = shadow.Value.Uid;
            var profile = await store.PutExactAsync(actor, new ModelProfileResource
            {
                ApiVersion = ResourceApiVersions.CoreV1,
                Kind = ModelResourceKinds.ModelProfile,
                ScopeRef = actor,
                Metadata = new() { Name = "profile", Namespace = consumers },
                Definition = new() { DisplayName = "Visible profile", Provider = new("provider", ResourceScopeRef.Instance, providers), Model = new() { Name = "metadata-only" } }
            }, null, true, default);
            profileUid = profile.Value.Uid;
            // An unresolved/ambiguous peer must not abort the healthy chain or select a same-name provider.
            _ = await store.PutExactAsync(actor, profile.Value with
            {
                Uid = Guid.Empty,
                Metadata = profile.Value.Metadata with { Name = "ambiguous-peer" },
                Definition = profile.Value.Definition with { Provider = new("provider", null, providers) }
            }, null, true, default);
            var agent = await store.PutExactAsync(actor, new AgentResource
            {
                ApiVersion = ResourceApiVersions.CoreV1,
                Kind = AgentResourceKinds.Agent,
                ScopeRef = actor,
                Metadata = new() { Name = "consumer", Namespace = consumers },
                Definition = new() { DisplayName = "Consumer", Instructions = "private-agent-instructions", ModelProfile = new("profile", actor, consumers) }
            }, null, true, default);
            agentUid = agent.Value.Uid;
            if (scenario != "provider-hidden")
                await policies.PutAsync(new(new(providerUid, ResourceScopeRef.Instance, providers, ModelResourceKinds.ModelProvider),
                    [new(actor, scenario == "provider-discover-only" ? [ResourceAccessAction.Discover] : [ResourceAccessAction.Discover, ResourceAccessAction.Use])]), null, default);
            if (scenario != "extension-hidden")
                await policies.PutAsync(new(new(extensionUid, ResourceScopeRef.Instance, extensions, ExtensionKinds.ExtensionRegistration),
                    [new(actor, [ResourceAccessAction.Discover, ResourceAccessAction.Use])]), null, default);
        }
        using var client = factory.CreateClient(); SelectConsoleContext(client, actor);
        using var lifetime = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        using var response = await client.GetAsync("/api/system-graph/stream?direction=dependencies&namespace=" + consumers.Value
            + "&root=" + Uri.EscapeDataString(SystemGraphIds.Definition(agentUid)), HttpCompletionOption.ResponseHeadersRead, lifetime.Token);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        await using var body = await response.Content.ReadAsStreamAsync(lifetime.Token);
        using var reader = new StreamReader(body); var frames = new List<SystemGraphStreamFrame>();
        while (await reader.ReadLineAsync(lifetime.Token) is { } line) frames.Add(JsonSerializer.Deserialize<SystemGraphStreamFrame>(line, JsonSerializerOptions.Web)!);
        Assert.AreEqual("complete", frames[^1].Kind);
        var nodes = frames.SelectMany(frame => frame.Nodes).ToArray();
        var edges = frames.SelectMany(frame => frame.Relations).ToArray();
        Assert.IsTrue(nodes.Any(node => node.Identity.ResourceUid == agentUid));
        Assert.IsTrue(nodes.Any(node => node.Identity.ResourceUid == profileUid));
        // Focused edges require binding inspection at both endpoints, beyond catalogue Discover.
        Assert.AreEqual(scenario is "full" or "extension-hidden", nodes.Any(node => node.Identity.ResourceUid == providerUid));
        Assert.IsFalse(nodes.Any(node => node.Identity.ResourceUid == shadowUid), "An explicit owner must not fall back to a visible homonym.");
        Assert.AreEqual(scenario == "full", nodes.Any(node => node.Identity.ResourceUid == extensionUid));
        if (scenario == "full")
        {
            Assert.AreEqual("Visible provider", nodes.Single(node => node.Identity.ResourceUid == providerUid).DisplayName);
            Assert.AreEqual("Linked model extension", nodes.Single(node => node.Identity.ResourceUid == extensionUid).DisplayName);
            Assert.IsTrue(edges.Any(edge => edge.Source == SystemGraphIds.Definition(profileUid) && edge.Target == SystemGraphIds.Definition(providerUid)
                && edge.SourceField == "definition.provider" && edge.Kind == "declared-reference" && edge.Resolution == "exact-owner"));
            Assert.IsTrue(edges.Any(edge => edge.Source == SystemGraphIds.Definition(providerUid) && edge.Target == SystemGraphIds.Definition(extensionUid)
                && edge.SourceField == "definition.extension" && edge.Kind == "declared-reference"));
        }
        var json = JsonSerializer.Serialize(frames);
        foreach (var secret in new[] { "private-provider-value", "private-extension-credential", "private-extension.invalid", "private-agent-instructions" })
            Assert.DoesNotContain(secret, json);
        if (scenario != "full") Assert.DoesNotContain("Linked model extension", json);
    }
}
