using Agentstration.Web.Components.Models;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Components.Tests;

[TestClass]
public sealed class CollectionPresentationTests
{
    [TestMethod]
    public async Task ViewSwitchPreservesIdentityAndInformationAndOmitsInapplicableState()
    {
        using var context = Context();
        RenderFragment<string> identity = item => builder => builder.AddContent(0, item);
        var cut = context.Render<CollectionView<string>>(parameters => parameters
            .Add(value => value.Items, ["First resource", "Second resource"])
            .Add(value => value.Identity, identity)
            .Add(value => value.Scope, _ => builder => builder.AddContent(0, "Instance"))
            .Add(value => value.InformationLabels, ["Runtime"])
            .Add(value => value.Information, item => builder =>
            {
                builder.OpenComponent<CollectionValue>(0);
                builder.AddAttribute(1, nameof(CollectionValue.Label), "Runtime");
                builder.AddAttribute(2, nameof(CollectionValue.ChildContent), (RenderFragment)(content => content.AddContent(0, $"Runtime for {item}")));
                builder.CloseComponent();
            })
            .Add(value => value.Actions, _ => builder => builder.AddContent(0, "Open")));
        CollectionAssert.AreEqual(new[] { "Resource", "Owner scope", "Runtime", "Actions" }, cut.FindAll("th").Select(value => value.TextContent).ToArray());
        Assert.HasCount(2, cut.FindAll("tbody tr"));
        await cut.FindAll(".collection-view-switch button")[1].ClickAsync(new());
        Assert.IsEmpty(cut.FindAll("table"));
        Assert.HasCount(2, cut.FindAll(".collection-card"));
        Assert.AreEqual("Runtime for First resource", cut.FindAll(".collection-value dd")[1].TextContent);
        Assert.AreEqual("true", cut.FindAll(".collection-view-switch button")[1].GetAttribute("aria-pressed"));
        await cut.InvokeAsync(() => cut.Instance.SetNarrow(false));
        Assert.HasCount(2, cut.FindAll(".collection-card"), "A resize must preserve an explicitly selected view.");
    }

    [TestMethod]
    public async Task LongQualifiedIdentityCopiesTheCompleteValueAndKeepsTheExactDestination()
    {
        using var context = Context();
        var module = context.JSInterop.SetupModule("./_content/Agentstration.Web.Components/resource-ui.js");
        var qualified = $"team.{new string('a', 100)}/same-name";
        var copy = module.SetupVoid("copyText", qualified);
        copy.SetVoidResult();
        const string target = "/namespaces/team/agents/same-name?scopeRef=%2Ftenants%2F123&context=%2Ftenants%2F123";
        var cut = context.Render<CollectionIdentity>(parameters => parameters
            .Add(value => value.DisplayName, "Same display name")
            .Add(value => value.Namespace, qualified.Split('/')[0])
            .Add(value => value.Name, "same-name")
            .Add(value => value.Href, target));
        Assert.AreEqual(target, cut.Find("a").GetAttribute("href"));
        Assert.AreEqual(qualified, cut.Find("code").TextContent);
        Assert.IsEmpty(cut.FindAll("a button"), "Copy must be independent from navigation.");
        await cut.Find(".copy-value").ClickAsync(new());
        Assert.HasCount(1, copy.Invocations);
    }

    [TestMethod]
    public void StatusIncludesIconTextAndNeutralToneIndependentlyOfGovernance()
    {
        using var context = Context();
        var status = context.Render<StatusBadge>(parameters => parameters
            .Add(value => value.Text, "Unknown")
            .Add(value => value.Status, UiStatus.Neutral));
        StringAssert.Contains(status.Find(".status-neutral").TextContent, "Unknown");
        Assert.AreEqual("true", status.Find("svg").GetAttribute("aria-hidden"));
        var badge = context.Render<ManagedResourceBadge>(parameters => parameters.Add(value => value.IsManaged, true));
        Assert.IsEmpty(badge.FindAll(".status-badge"));
        StringAssert.Contains(badge.Markup, "Managed / read-only");
    }

    [TestMethod]
    public async Task ReadOnlyOperationalCollectionOmitsInventedColumnsAndPreservesCardMetadata()
    {
        using var context = Context();
        var cut = context.Render<CollectionView<string>>(parameters => parameters
            .Add(value => value.Items, ["event-1"])
            .Add(value => value.IdentityLabel, "Event")
            .Add(value => value.Identity, item => builder => builder.AddContent(0, item))
            .Add(value => value.InformationLabels, ["Occurred"])
            .Add(value => value.Information, _ => builder =>
            {
                builder.OpenComponent<CollectionValue>(0);
                builder.AddAttribute(1, nameof(CollectionValue.Label), "Occurred");
                builder.AddAttribute(2, nameof(CollectionValue.ChildContent), (RenderFragment)(content => content.AddContent(0, "2026-11-10")));
                builder.CloseComponent();
            })
            .Add(value => value.ItemAttributes, item => new Dictionary<string, object> { ["class"] = "selected-event", ["data-event-id"] = item }));
        CollectionAssert.AreEqual(new[] { "Event", "Occurred" }, cut.FindAll("th").Select(value => value.TextContent).ToArray());
        await cut.FindAll(".collection-view-switch button")[1].ClickAsync(new());
        Assert.AreEqual("event-1", cut.Find(".collection-card.selected-event").GetAttribute("data-event-id"));
        Assert.IsEmpty(cut.FindAll(".collection-actions, .status-badge, .resource-owner-scope"));
    }

    [TestMethod]
    public async Task ExpandedDetailsRemainAttachedToTheCorrectItemAcrossPresentations()
    {
        using var context = Context();
        var cut = context.Render<CollectionView<string>>(parameters => parameters
            .Add(value => value.Items, ["first", "second"])
            .Add(value => value.Identity, item => builder => builder.AddContent(0, item))
            .Add(value => value.InformationLabels, [])
            .Add(value => value.Information, _ => _ => { })
            .Add(value => value.IsExpanded, item => item == "second")
            .Add(value => value.Details, item => builder => builder.AddContent(0, $"Artifacts of {item}")));
        Assert.AreEqual("1", cut.Find(".collection-expanded td").GetAttribute("colspan"));
        Assert.AreEqual("Artifacts of second", cut.Find(".collection-expanded").TextContent);
        await cut.FindAll(".collection-view-switch button")[1].ClickAsync(new());
        Assert.IsEmpty(cut.FindAll(".collection-card")[0].QuerySelectorAll(".collection-expanded"));
        Assert.AreEqual("Artifacts of second", cut.FindAll(".collection-card")[1].QuerySelector(".collection-expanded")!.TextContent);
    }

    [TestMethod]
    public async Task InPageInspectionAndUnqualifiedIdentifiersDoNotInventResourceRoutes()
    {
        using var context = Context();
        var opened = 0;
        var cut = context.Render<CollectionIdentity>(parameters => parameters
            .Add(value => value.DisplayName, "Build 1")
            .Add(value => value.Name, "build-id")
            .Add(value => value.OnOpen, () => opened++));
        Assert.IsEmpty(cut.FindAll("a"));
        Assert.AreEqual("build-id", cut.Find("code").TextContent);
        await cut.Find(".collection-identity-button").ClickAsync(new());
        Assert.AreEqual(1, opened);
    }

    [TestMethod]
    public async Task VersionMetadataStaysWithIdentityInBothViewsWithoutChangingCopyOrNavigation()
    {
        using var context = Context();
        var cut = context.Render<CollectionView<string>>(parameters => parameters
            .Add(value => value.Items, ["profile"])
            .Add(value => value.InformationLabels, [])
            .Add(value => value.Information, _ => _ => { })
            .Add(value => value.Identity, _ => builder =>
            {
                builder.OpenComponent<CollectionIdentity>(0);
                builder.AddAttribute(1, nameof(CollectionIdentity.DisplayName), "Profile");
                builder.AddAttribute(2, nameof(CollectionIdentity.Namespace), "team");
                builder.AddAttribute(3, nameof(CollectionIdentity.Name), "profile");
                builder.AddAttribute(4, nameof(CollectionIdentity.Href), "/exact-profile?scopeRef=owner");
                builder.AddAttribute(5, nameof(CollectionIdentity.Versions), (RenderFragment)(versions =>
                {
                    versions.OpenComponent<CollectionVersion>(0);
                    versions.AddAttribute(1, nameof(CollectionVersion.Label), "Active version");
                    versions.AddAttribute(2, nameof(CollectionVersion.Value), "1.0.0");
                    versions.CloseComponent();
                    versions.OpenComponent<CollectionVersion>(3);
                    versions.AddAttribute(4, nameof(CollectionVersion.Label), "Draft version");
                    versions.AddAttribute(5, nameof(CollectionVersion.Value), "2.0.0-preview+" + new string('a', 120));
                    versions.CloseComponent();
                }));
                builder.CloseComponent();
            }));
        foreach (var card in new[] { false, true })
        {
            if (card) await cut.FindAll(".collection-view-switch button")[1].ClickAsync(new());
            Assert.HasCount(2, cut.FindAll(".collection-identity-text .collection-version"));
            Assert.AreEqual("Active version", cut.FindAll(".collection-version-label")[0].TextContent);
            Assert.AreEqual("Draft version", cut.FindAll(".collection-version-label")[1].TextContent);
            Assert.AreEqual("2.0.0-preview+" + new string('a', 120), cut.FindAll(".collection-version code")[1].TextContent);
            Assert.AreEqual("team/profile", cut.Find(".collection-qualified-name code").TextContent);
            Assert.AreEqual("/exact-profile?scopeRef=owner", cut.Find("a").GetAttribute("href"));
            Assert.IsEmpty(cut.FindAll("a .collection-version"));
        }
    }

    [TestMethod]
    public void MissingVersionRetainsItsMeaningfulLabelAndUnversionedIdentityHasNoEmptyMetadata()
    {
        using var context = Context();
        var version = context.Render<CollectionVersion>(parameters => parameters
            .Add(value => value.Label, "Version active")
            .Add(value => value.Value, (string?)null));
        Assert.AreEqual("Version active", version.Find(".collection-version-label").TextContent);
        Assert.AreEqual("—", version.Find("code").TextContent);
        var identity = context.Render<CollectionIdentity>(parameters => parameters.Add(value => value.DisplayName, "Unversioned"));
        Assert.IsEmpty(identity.FindAll(".collection-identity-versions"));
    }

    [TestMethod]
    public async Task MultiplePrimaryActionsStayVisibleAndOnlySecondaryActionsEnterTheMenu()
    {
        using var context = Context();
        var chosen = 0;
        RenderFragment primary = builder =>
        {
            builder.OpenElement(0, "button");
            builder.AddAttribute(1, "onclick", EventCallback.Factory.Create(this, () => chosen++));
            builder.AddContent(2, "Choose");
            builder.CloseElement();
            builder.OpenElement(3, "button");
            builder.AddContent(4, "Enroll");
            builder.CloseElement();
        };
        var cut = context.Render<CollectionActions>(parameters => parameters
            .Add(value => value.ResourceName, "Profile")
            .Add(value => value.Primary, primary)
            .Add(value => value.Secondary, builder => builder.AddMarkupContent(0, "<button>Revoke</button>")));
        Assert.HasCount(2, cut.FindAll(".collection-primary-actions > button"));
        Assert.AreEqual("Revoke", cut.Find(".collection-actions-panel").TextContent);
        Assert.IsFalse(cut.Find("details").HasAttribute("open"));
        await cut.Find(".collection-primary-actions button").ClickAsync(new());
        Assert.AreEqual(1, chosen);
        cut.Render(parameters => parameters
            .Add(value => value.ResourceName, "Profile")
            .Add(value => value.Primary, primary)
            .Add(value => value.Secondary, (RenderFragment?)null));
        Assert.HasCount(2, cut.FindAll(".collection-primary-actions > button"));
        Assert.IsEmpty(cut.FindAll("details"), "No secondary command means no empty Actions menu.");
        cut.Render(parameters => parameters
            .Add(value => value.ResourceName, "Profile")
            .Add(value => value.Primary, primary)
            .Add(value => value.Secondary, builder => builder.AddMarkupContent(0, "<button>Revoke</button>")));
        Assert.AreEqual("Revoke", cut.Find(".collection-actions-panel").TextContent);

    }

    [TestMethod]
    public void ExistingUnnamedCommandsRemainSecondaryWithoutInventingPrimaryActions()
    {
        using var context = Context();
        var cut = context.Render<CollectionActions>(parameters => parameters
            .Add(value => value.ResourceName, "Profile")
            .AddChildContent("<button>Inspect</button>"));
        Assert.AreEqual("Inspect", cut.Find(".collection-actions-panel").TextContent);
        Assert.IsEmpty(cut.FindAll(".collection-primary-actions"));
    }

    private static BunitContext Context()
    {
        var context = new BunitContext();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        return context;
    }
}
