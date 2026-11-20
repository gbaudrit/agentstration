using Agentstration.Infrastructure;
using Agentstration.Web.Console;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class BootstrapStartupProgressStateTests
{
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
