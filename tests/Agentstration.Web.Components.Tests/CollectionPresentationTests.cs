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

    private static BunitContext Context()
    {
        var context = new BunitContext();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        return context;
    }
}
