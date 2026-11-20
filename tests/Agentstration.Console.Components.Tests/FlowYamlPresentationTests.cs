using System.Text.Json;
using Agentstration.Flows;
using Agentstration.Flows.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Web.Console;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class FlowYamlPresentationTests
{
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public void ManifestRetainsTheSavedIdentityDefinitionMetadataAndOptionalGraph(bool orchestration, bool includeGraph)
    {
        var owner = ResourceScopeRef.Tenant(Guid.Parse("33333333-3333-3333-3333-333333333333"));
        FlowDefinition spec = orchestration
            ? new OrchestrationFlowDefinition([new(FlowTargetKind.Agent, "reviewer")], new SequentialOrchestrationPattern())
            : new DirectFlowDefinition(new(FlowTargetKind.Agent, "reviewer"));
        var graph = new FlowGraphDefinition
        {
            EntryStep = "receive",
            Steps = [new InputFlowStepDefinition { Name = "receive", Schema = JsonSerializer.SerializeToElement(new { type = "object", properties = new { text = new { type = "string" } } }) }]
        };
        var timestamp = DateTimeOffset.Parse("2026-11-20T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var resource = new FlowResponse("review", "Legacy display name", "Full saved description.\nSecond line.", "2.0.0", false, "1.0.0", spec,
            new Dictionary<string, string> { [FlowMetadataKeys.Contract] = "review/v1", ["literal"] = "true" }, timestamp, timestamp, includeGraph ? graph : null)
        {
            Namespace = new("team.reviews"),
            ScopeRef = owner,
            DisplayName = "Saved review"
        };

        var document = ResourceManifestSerializer.FromYaml<JsonElement>(FlowYamlPresentation.ToYaml(resource));
        Assert.AreEqual(ResourceApiVersions.CoreV1, document.GetProperty("apiVersion").GetString());
        Assert.AreEqual(FlowResourceKinds.Flow, document.GetProperty("kind").GetString());
        Assert.AreEqual("review", document.GetProperty("metadata").GetProperty("name").GetString());
        Assert.AreEqual("team.reviews", document.GetProperty("metadata").GetProperty("namespace").GetString());
        Assert.AreEqual(owner.Value, document.GetProperty("scopeRef").GetString());
        var definition = document.GetProperty("definition");
        Assert.AreEqual("Saved review", definition.GetProperty("displayName").GetString());
        Assert.AreEqual(resource.Description, definition.GetProperty("description").GetString());
        Assert.AreEqual("2.0.0", definition.GetProperty("version").GetString());
        Assert.IsFalse(definition.GetProperty("enabled").GetBoolean());
        Assert.AreEqual("review/v1", definition.GetProperty("metadata").GetProperty(FlowMetadataKeys.Contract).GetString());
        Assert.AreEqual("true", definition.GetProperty("metadata").GetProperty("literal").GetString());
        Assert.AreEqual(spec.GetType(), ResourceManifestSerializer.FromJson<FlowDefinition>(definition.GetProperty("spec").GetRawText()).GetType());
        if (includeGraph)
        {
            var restored = ResourceManifestSerializer.FromJson<FlowGraphDefinition>(definition.GetProperty("graph").GetRawText());
            Assert.AreEqual("receive", restored.EntryStep);
            var input = (InputFlowStepDefinition)restored.Steps[0];
            Assert.AreEqual("receive", input.Name);
            Assert.AreEqual("object", input.Schema!.Value.GetProperty("type").GetString());
            Assert.AreEqual("string", input.Schema.Value.GetProperty("properties").GetProperty("text").GetProperty("type").GetString());
        }
        else Assert.AreEqual(JsonValueKind.Null, definition.GetProperty("graph").ValueKind);
    }
}
