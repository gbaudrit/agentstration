using Agentstration.Infrastructure;
using Agentstration.Web.Console;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class BootstrapStartupProgressStateTests
{
    [TestMethod]
    public void DiscoveryRunsAreInspectableDuringBootstrapWhileExecutionCommandsStayFenced()
    {
        Assert.IsTrue(BootstrapStartupNavigation.IsInspectionPath("/flow-runs/discovery"));
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        context.Request.Path = "/api/flowRuns/discovery";
        context.Request.Method = "GET";
        Assert.IsTrue(Agentstration.Web.Hosting.BootstrapAdministrationAccess.IsInspectionRead(context.Request));
        context.Request.Method = "POST";
        context.Request.Path = "/api/flowRuns/discovery/cancel";
        Assert.IsFalse(Agentstration.Web.Hosting.BootstrapAdministrationAccess.IsInspectionRead(context.Request));
        Assert.IsFalse(Agentstration.Web.Hosting.BootstrapAdministrationAccess.IsAdministrationPath(context.Request.Path));
    }
    [TestMethod]
    public void WorkerAdmissionIsAttemptBoundAndDoesNotReleaseOrdinaryWorkersBeforeReadiness()
    {
        var startup = new BootstrapStartupState();
        startup.Begin();
        CollectionAssert.AreEqual(new[] { "topology", "workers", "builtins", "extensions", "resources", "finalization" }, startup.Read().Steps.Select(step => step.Id).ToArray());
        Assert.IsFalse(startup.WorkerCommunicationOpen);
        Assert.HasCount(0, startup.ClaimableAssignmentIds!);
        var attempt = startup.Read().AttemptId;
        startup.MoveTo("workers"); startup.OpenWorkerCommunication();
        Assert.IsFalse(startup.IsReady);
        var id = new Agentstration.Runtime.Abstractions.RuntimeAssignmentId(Guid.NewGuid());
        startup.AdmitAssignment(attempt, id);
        Assert.Contains(id, startup.ClaimableAssignmentIds!);
        startup.Fail("bootstrap_discovery_timeout");
        Assert.HasCount(0, startup.ClaimableAssignmentIds!);
        startup.BeginRetry(); startup.OpenWorkerCommunication();
        startup.AdmitAssignment(startup.Read().AttemptId, id);
        Assert.Contains(id, startup.ClaimableAssignmentIds!);
        Assert.ThrowsExactly<InvalidOperationException>(() => startup.AdmitAssignment(attempt, id));
        startup.Complete();
        Assert.IsNull(startup.ClaimableAssignmentIds);
    }
    [TestMethod]
    public void BootstrapEntryPreservesAValidatedInspectionScopeSeparatelyFromItsReturnDestination()
    {
        var url = BootstrapStartupNavigation.ProgressUrl("/?context=%2Finstance");
        var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(new Uri("https://console.invalid" + url).Query);
        Assert.AreEqual("/instance", query["context"].ToString());
        Assert.AreEqual("/?context=%2Finstance", query["ReturnUrl"].ToString());
        var invalid = BootstrapStartupNavigation.ProgressUrl("/?context=https%3A%2F%2Fexample.invalid");
        Assert.IsFalse(Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(new Uri("https://console.invalid" + invalid).Query).ContainsKey("context"));
    }
    [TestMethod]
    public void SnapshotIsStableSanitizedAndDoesNotExposeMutableState()
    {
        var state = new BootstrapStartupState();
        state.Begin(); state.EnableAdministration(); state.MoveTo("extensions");
        state.ObserveContributionWait("ToolSet", "tools", "public.extension", "search");
        var waiting = state.Read();
        Assert.AreEqual("waiting", waiting.Steps.Single(x => x.Id == "extensions").State);
        state.MoveTo("resources"); state.ObserveResource("Agent", "assistant", "created");
        state.ObserveModelWait("profile", "provider", "model:exact", "provider_unavailable");
        var current = state.Read();
        state.ObserveModelWait("profile", "provider", "model:exact", "provider_unavailable");
        Assert.AreEqual(current.Revision, state.Read().Revision);
        Assert.AreEqual(1, current.Created);
        state.ObserveResource("ModelProfile", "profile", "failed");
        state.Fail("credential value from a failing transport");
        var failed = state.Read();
        Assert.AreEqual("failed", failed.State);
        Assert.AreEqual("model:exact", failed.Activity?.ModelName);
        Assert.AreEqual(1, failed.Failed);
        Assert.IsFalse(System.Text.Json.JsonSerializer.Serialize(failed).Contains("credential value", StringComparison.Ordinal));
        state.Complete();
        Assert.IsFalse(state.IsReady, "A late completion cannot clear a terminal failure.");
        Assert.AreEqual("waiting", waiting.Steps.Single(x => x.Id == "extensions").State, "Previously read snapshots are immutable observations.");
        state.Begin();
        Assert.AreNotEqual(current.AttemptId, state.Read().AttemptId);
        Assert.AreEqual(0, state.Read().Created);
    }

    [TestMethod]
    [DataRow("https://example.test", "/")]
    [DataRow("//example.test", "/")]
    [DataRow("/\\example.test", "/")]
    [DataRow("/startup/progress?ReturnUrl=%2F", "/")]
    [DataRow("/agents/assistant?tab=overview", "/agents/assistant?tab=overview")]
    public void ProgressReturnDestinationsRemainLocalAndAvoidLoops(string input, string expected) =>
        Assert.AreEqual(expected, BootstrapStartupNavigation.LocalReturnUrl(input));
}
