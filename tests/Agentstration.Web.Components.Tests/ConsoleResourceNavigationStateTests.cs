using Agentstration.Web.Components.State;

namespace Agentstration.Web.Components.Tests;

[TestClass]
public sealed class ConsoleResourceNavigationStateTests
{
    private const string TargetScope = "/workspaces/aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
    private static readonly Uri Source = new("https://console.test/arbitrary/same?namespace=team&context=%2Finstance#access");
    private static readonly Uri Target = new("https://console.test/arbitrary/same?namespace=team&scopeRef=%2Finstance&context=" + Uri.EscapeDataString(TargetScope) + "#access");
    private static readonly ResourceLoadState Ready = new(ResourceLoadStatus.Ready) { HasContent = true };
    private static ConsoleResourceLocation Resource(string kind = "Flow") => new(Source, "/instance", Guid.NewGuid(), "/instance", "team", kind, "same");

    [TestMethod]
    [DataRow("Agent")]
    [DataRow("Flow")]
    [DataRow("ModelProfile")]
    [DataRow("Storage")]
    [DataRow("Secret")]
    [DataRow("CustomFamily")]
    public void TheNormalLoadAcceptsTheSameSavedResourceForEveryFamily(string kind)
    {
        var state = new ConsoleResourceNavigationState();
        var resource = Resource(kind);
        var owner = new object();
        state.Observe(owner, Source, "/instance", resource, Ready);
        Assert.AreSame(resource, state.GetCurrent(Source, "/instance"));
        state.BeginSwitch(Source, Target, "/collection", resource);
        var loaded = resource with { Location = Target, ContextScopeRef = TargetScope };
        Assert.IsFalse(state.Observe(new object(), Target, TargetScope, loaded, Ready));
        Assert.AreSame(loaded, state.GetCurrent(Target, TargetScope));
        state.Release(owner);
        Assert.AreSame(loaded, state.Current, "Disposing the previous frame cannot erase the new one.");
    }

    [TestMethod]
    [DataRow(ResourceLoadStatus.NotFound)]
    [DataRow(ResourceLoadStatus.Forbidden)]
    public void UnavailableLoadsSuppressTheFrameAndRequestOneFallback(ResourceLoadStatus status)
    {
        var state = new ConsoleResourceNavigationState();
        var failures = new List<ConsoleResourceSwitchFailure>();
        state.ResourceUnavailable += failures.Add;
        state.BeginSwitch(Source, Target, "/collection", Resource());
        var owner = new object();
        Assert.IsTrue(state.Observe(owner, Target, TargetScope, null, new(status)));
        Assert.IsTrue(state.Observe(owner, Target, TargetScope, null, new(status)));
        Assert.HasCount(1, failures);
        Assert.AreEqual(Source.AbsoluteUri, failures[0].SourceLocation);
        Assert.AreEqual("/collection", failures[0].FallbackLocation);
    }

    [TestMethod]
    [DataRow("uid")]
    [DataRow("owner")]
    [DataRow("namespace")]
    [DataRow("kind")]
    [DataRow("name")]
    public void AReplacementOrHomonymCannotBecomeTheRetainedResource(string changed)
    {
        var state = new ConsoleResourceNavigationState();
        var resource = Resource();
        var destination = resource with { Location = Target, ContextScopeRef = TargetScope };
        destination = changed switch
        {
            "uid" => destination with { Uid = Guid.NewGuid() },
            "owner" => destination with { OwnerScopeRef = TargetScope },
            "namespace" => destination with { Namespace = "other" },
            "kind" => destination with { Kind = "Other" },
            _ => destination with { Name = "other" }
        };
        state.BeginSwitch(Source, Target, "/collection", resource);
        Assert.IsTrue(state.Observe(new object(), Target, TargetScope, destination, Ready));
    }

    [TestMethod]
    public void TechnicalFailureRemainsRetryableAndASectionFallbackDoesNotChangeResourceIdentity()
    {
        var state = new ConsoleResourceNavigationState();
        var resource = Resource();
        var failures = 0;
        state.ResourceUnavailable += _ => failures++;
        state.BeginSwitch(Source, Target, "/collection", resource);
        Assert.IsFalse(state.Observe(new object(), Target, TargetScope, null, new(ResourceLoadStatus.Failed)));
        var overview = new Uri(Target.GetLeftPart(UriPartial.Query) + "#overview");
        state.LocationChanged(overview);
        Assert.IsFalse(state.Observe(new object(), overview, TargetScope,
            resource with { Location = overview, ContextScopeRef = TargetScope }, Ready));
        Assert.AreEqual(0, failures);
    }

    [TestMethod]
    public void ANewerNavigationPreventsALateFailureFromRedirectingIt()
    {
        var state = new ConsoleResourceNavigationState();
        var failures = 0;
        state.ResourceUnavailable += _ => failures++;
        state.BeginSwitch(Source, Target, "/collection", Resource());
        state.LocationChanged(new Uri("https://console.test/other?context=" + Uri.EscapeDataString(TargetScope)));
        Assert.IsFalse(state.Observe(new object(), Target, TargetScope, null, new(ResourceLoadStatus.Forbidden)));
        Assert.AreEqual(0, failures);
    }

    [TestMethod]
    public void ALoadingFrameDoesNotOfferAnUnresolvedOwnerAndOldFramesCannotRegisterTheNewContext()
    {
        var state = new ConsoleResourceNavigationState();
        var owner = new object();
        state.Observe(owner, Source, "/instance", null, new(ResourceLoadStatus.Loading));
        Assert.IsTrue(state.IsLoading);
        Assert.IsNull(state.GetCurrent(Source, "/instance"));
        state.Observe(owner, Target, "/instance", Resource(), Ready);
        Assert.IsTrue(state.IsLoading);
        state.Release(owner);
        Assert.IsFalse(state.IsLoading);
    }
}
