using System.Text.Json;
using Agentstration.Flows;
using Agentstration.Flows.Application;
using Agentstration.Resources;

namespace Agentstration.Application.Tests;

public sealed partial class FlowTests
{
    [TestMethod]
    public async Task OwnerSideDefinitionAuthorizationRequiresInternalCompositionAndCannotBeDeserialized()
    {
        await using var fixture = await FlowFixture.CreateAsync();
        var command = new EnsureRootFlowRunCommand(
            "owner-definition", new FlowId("private-adapter"), "1.0.0", "local",
            FlowRunTrigger.Api, FlowInvocationOrigin.Api, "test", null, "owner-definition", "correlation",
            JsonSerializer.SerializeToElement(new { }), Guid.NewGuid().ToString("D"), false,
            null, null, null, null, TestScope)
        { DefinitionUseScopeRef = ResourceScopeRef.Instance };
        var rejected = await Assert.ThrowsExactlyAsync<FlowValidationException>(() =>
            Service(fixture, new TestFlowRunQueue()).EnsureRootAsync(command, default));
        Assert.AreEqual("flow_definition_authorization_invalid", rejected.Code);

        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var serialized = JsonSerializer.Serialize(command, options);
        Assert.DoesNotContain("definitionUseScopeRef", serialized, StringComparison.Ordinal);
        var injected = serialized[..^1] + ",\"definitionUseScopeRef\":\"/instance\"}";
        Assert.IsNull(JsonSerializer.Deserialize<EnsureRootFlowRunCommand>(injected, options)!.DefinitionUseScopeRef);
    }
}
