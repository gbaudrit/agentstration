using System.Text.Json;
using Agentstration.Bootstrap.Contracts;
using Agentstration.DataSources;
using Agentstration.Infrastructure;
using Agentstration.Infrastructure.Bootstrap;

namespace Agentstration.Infrastructure.Tests;

[TestClass]
public sealed class BootstrapFailureDiagnosticsTests
{
    [TestMethod]
    public void NativeReasonCodesSurviveWithoutExceptionTextAndRemainLinkedToStartupResources()
    {
        var diagnostic = BootstrapFailureDiagnostics.Describe(new DataSourceValidationException("data_source_profile_not_published", "PRIVATE-PARAMETER-VALUE"));
        Assert.AreEqual("data_source_profile_not_published", diagnostic.Code);
        Assert.IsTrue(Guid.TryParseExact(diagnostic.DiagnosticId, "N", out _));
        Assert.IsFalse(JsonSerializer.Serialize(diagnostic).Contains("PRIVATE", StringComparison.Ordinal));
        var state = new BootstrapStartupState(); state.Begin(); state.MoveTo("resources");
        var id = state.RegisterResource("/instance", "example", "profile.yaml", "resources", "DataSourceProfile", "profile");
        state.StartResource("/instance", "example", "profile.yaml", "DataSourceProfile", "profile", "default");
        state.FinishResource(id, "failed", diagnostic);
        state.Fail(new InvalidOperationException("PRIVATE-TRANSPORT-BODY"));
        Assert.AreEqual(diagnostic, state.Read().Failure);
        Assert.AreEqual(diagnostic, state.ReadResources(new(state.Read().AttemptId))!.Items.Single().Failure);
        Assert.IsFalse(JsonSerializer.Serialize(state.Read()).Contains("PRIVATE", StringComparison.Ordinal));
        var publication = BootstrapFailureDiagnostics.Describe(new DataSourceAcquisitionException("data_source_acquisition_flow_unavailable", "PRIVATE-FLOW-BINDING"));
        Assert.AreEqual("data_source_acquisition_flow_unavailable", publication.Code);
        Assert.IsFalse(JsonSerializer.Serialize(publication).Contains("PRIVATE", StringComparison.Ordinal));
        var invalidCode = BootstrapFailureDiagnostics.Describe(new DataSourceValidationException("untrusted\nvalue", "PRIVATE"));
        Assert.AreEqual("bootstrap_resource_failed", invalidCode.Code);
    }

    [TestMethod]
    public void FollowResourceReturnsOnlyItsBoundedPageAndRespectsTheSelectedGroupAndFilter()
    {
        var state = new BootstrapStartupState(); state.Begin(); state.MoveTo("resources");
        var ids = Enumerable.Range(0, 100).Select(index => state.RegisterResource("/instance", "large", index + ".yaml", "resources", "Parameter", "item-" + index)).ToArray();
        var attempt = state.Read().AttemptId;
        var page = state.ReadResources(new(attempt, "resources", "Parameter", Limit: 25, FollowResourceId: ids[82]))!;
        Assert.AreEqual(75, page.Offset);
        Assert.HasCount(25, page.Items);
        Assert.IsTrue(page.Items.Any(row => row.Id == ids[82]));
        var filtered = state.ReadResources(new(attempt, "resources", "Parameter", Search: "item-1", Limit: 25, FollowResourceId: ids[82]))!;
        Assert.AreEqual(0, filtered.Offset);
        Assert.IsFalse(filtered.Items.Any(row => row.Id == ids[82]));
        var hidden = state.ReadResources(new(attempt, "resources", "Flow", FollowResourceId: ids[82]))!;
        Assert.AreEqual(0, hidden.Total);
        Assert.Throws<ArgumentOutOfRangeException>(() => state.ReadResources(new(attempt, FollowResourceId: new string('x', 129))));
    }
}
