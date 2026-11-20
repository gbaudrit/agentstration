using System.Net;
using Agentstration.Artifacts.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.Web.Components;
using Agentstration.Web.Components.State;
using Agentstration.Web.Console;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

public sealed partial class ArtifactAdministrationComponentTests
{
    private static IRenderedComponent<Agentstration.Web.Components.Pages.StagedArtifactDetails> StagedDetails(BunitContext context, ArtifactClient client)
    {
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton(TimeProvider.System);
        context.Services.AddSingleton<IArtifactsClient>(client);
        return context.Render<Agentstration.Web.Components.Pages.StagedArtifactDetails>(p => p.Add(v => v.Id, client.Artifact.ArtifactId.ToString()));
    }
    private static string StagedDate(DateTimeOffset date) => date.ToLocalTime().ToString("yyyy-MM-ddTHH:mm", System.Globalization.CultureInfo.InvariantCulture);

    [TestMethod]
    public async Task StagedRefreshRetainsTheRetentionDraftAndLoadedContentUntilExplicitDiscard()
    {
        using var culture = new TestCultureScope("en-US");
        using var context = CreateContext();
        var client = new ArtifactClient("Retained private staged content");
        var cut = StagedDetails(context, client);
        var template = cut.FindComponent<ResourceDetailsTemplate>().Instance;
        Assert.HasCount(3, cut.FindAll(".resource-configuration-summary tbody tr"));
        SelectSection(context, "retention");
        var draft = StagedDate(client.Artifact.ExpiresAt.AddHours(2));
        cut.Find("input[type=datetime-local]").Change(draft);
        SelectSection(context, "content");
        cut.Find("[data-testid=staged-artifact-read-full]").Click();
        var content = cut.FindComponent<Agentstration.Web.Components.Pages.StagedArtifactContent>().Instance;
        client.Artifact = client.Artifact with { ExpiresAt = client.Artifact.ExpiresAt.AddHours(3) };
        await cut.InvokeAsync(() => template.OnRefresh.InvokeAsync());
        Assert.AreSame(content, cut.FindComponent<Agentstration.Web.Components.Pages.StagedArtifactContent>().Instance);
        StringAssert.Contains(cut.Markup, "Retained private staged content");
        Assert.AreEqual(1, client.ContentReadCount);
        Assert.AreEqual(draft, cut.Find("input[type=datetime-local]").GetAttribute("value"));
        StringAssert.Contains(cut.Markup, "Updated metadata is available");
        SelectSection(context, "retention");
        cut.Find(".unsaved-changes-bar button").Click();
        Assert.AreEqual(StagedDate(client.Artifact.ExpiresAt), cut.Find("input[type=datetime-local]").GetAttribute("value"));
        Assert.IsEmpty(cut.FindAll(".unsaved-changes-bar"));
        Assert.AreEqual(0, client.RetentionWriteCount);
    }

    [TestMethod]
    [DataRow(HttpStatusCode.ServiceUnavailable, true)]
    [DataRow(HttpStatusCode.Forbidden, false)]
    [DataRow(HttpStatusCode.Unauthorized, false)]
    [DataRow(HttpStatusCode.NotFound, false)]
    public async Task StagedRefreshKeepsRecoverableInputAndClearsContentOnLostAccess(HttpStatusCode status, bool keep)
    {
        using var context = CreateContext();
        var client = new ArtifactClient("Private staged bytes");
        var cut = StagedDetails(context, client);
        var template = cut.FindComponent<ResourceDetailsTemplate>().Instance;
        SelectSection(context, "retention");
        var draft = StagedDate(client.Artifact.ExpiresAt.AddHours(2));
        cut.Find("input[type=datetime-local]").Change(draft);
        SelectSection(context, "content");
        cut.Find("[data-testid=staged-artifact-read-full]").Click();
        client.StagedReadError = new AgentstrationApiException("Private backend detail", "staged-read", status);
        await cut.InvokeAsync(() => template.OnRefresh.InvokeAsync());
        Assert.AreEqual(keep, cut.FindAll("input[type=datetime-local]").Count > 0);
        if (keep) { Assert.AreEqual(draft, cut.Find("input[type=datetime-local]").GetAttribute("value")); StringAssert.Contains(cut.Markup, "Private staged bytes"); }
        else { Assert.DoesNotContain("Private staged bytes", cut.Markup); Assert.DoesNotContain("index.html", cut.Markup); }
        Assert.DoesNotContain("Private backend detail", cut.Markup);
    }

    [TestMethod]
    public async Task StagedRefreshKeepsInputTypedWhileTheReadIsPending()
    {
        using var context = CreateContext();
        var client = new ArtifactClient("Local bytes");
        var cut = StagedDetails(context, client);
        var template = cut.FindComponent<ResourceDetailsTemplate>().Instance;
        SelectSection(context, "retention");
        var pending = new TaskCompletionSource<StagedArtifactView?>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.NextStagedRead = pending;
        var refresh = cut.InvokeAsync(() => template.OnRefresh.InvokeAsync());
        cut.WaitForAssertion(() => Assert.IsTrue(cut.Find("[data-testid=staged-artifact-refresh]").HasAttribute("disabled")));
        Assert.IsFalse(cut.Find("input[type=datetime-local]").HasAttribute("disabled"));
        var draft = StagedDate(client.Artifact.ExpiresAt.AddHours(2));
        cut.Find("input[type=datetime-local]").Change(draft);
        pending.SetResult(client.Artifact with { ExpiresAt = client.Artifact.ExpiresAt.AddHours(3) });
        await refresh;
        Assert.AreEqual(draft, cut.Find("input[type=datetime-local]").GetAttribute("value"));
    }

    [TestMethod]
    public async Task StagedSupersededReadCannotRestoreAnotherArtifactOrItsDraft()
    {
        using var context = CreateContext();
        var client = new ArtifactClient("Local bytes");
        var original = client.Artifact;
        var cut = StagedDetails(context, client);
        var template = cut.FindComponent<ResourceDetailsTemplate>().Instance;
        var pending = new TaskCompletionSource<StagedArtifactView?>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.NextStagedRead = pending;
        var refresh = cut.InvokeAsync(() => template.OnRefresh.InvokeAsync());
        client.Artifact = original with { ArtifactId = StagedArtifactId.New(), FileName = "accepted-new-copy.txt" };
        cut.Render(p => p.Add(v => v.Id, client.Artifact.ArtifactId.ToString()));
        pending.SetResult(original);
        await refresh;
        StringAssert.Contains(cut.Find(".resource-identity-summary").TextContent, "accepted-new-copy.txt");
        Assert.DoesNotContain(original.ArtifactId.ToString(), cut.Markup);
    }

    [TestMethod]
    public async Task StagedPurgedMetadataClearsTheCachedContentWhileKeepingRetentionInput()
    {
        using var context = CreateContext();
        var client = new ArtifactClient("Old cached bytes");
        var cut = StagedDetails(context, client);
        var template = cut.FindComponent<ResourceDetailsTemplate>().Instance;
        SelectSection(context, "retention");
        var draft = StagedDate(client.Artifact.ExpiresAt.AddHours(2));
        cut.Find("input[type=datetime-local]").Change(draft);
        SelectSection(context, "content");
        cut.Find("[data-testid=staged-artifact-read-full]").Click();
        var before = cut.FindComponent<Agentstration.Web.Components.Pages.StagedArtifactContent>().Instance;
        client.Artifact = client.Artifact with { Status = StagedArtifactStatus.Purged };
        await cut.InvokeAsync(() => template.OnRefresh.InvokeAsync());
        Assert.AreNotSame(before, cut.FindComponent<Agentstration.Web.Components.Pages.StagedArtifactContent>().Instance);
        Assert.DoesNotContain("Old cached bytes", cut.Markup);
        Assert.AreEqual(draft, cut.Find("input[type=datetime-local]").GetAttribute("value"));
        Assert.AreEqual(1, client.ContentReadCount);
    }

    [TestMethod]
    [DataRow(true, true, false, false, true, false)]
    [DataRow(false, false, true, true, false, true)]
    [DataRow(true, false, true, false, false, false)]
    [DataRow(false, true, false, true, false, false)]
    public async Task StagedRetentionAndPurgeUseTheirIndependentPermissions(bool write, bool retention, bool delete, bool purge, bool canExtend, bool canDelete)
    {
        using var context = CreateContext();
        var permissions = new HashSet<string> { AuthorizationPermissions.ResourcesRead };
        if (write) permissions.Add(AuthorizationPermissions.ResourcesWrite);
        if (delete) permissions.Add(AuthorizationPermissions.ResourcesDelete);
        if (retention) permissions.Add(AuthorizationPermissions.ArtifactsManageRetention);
        if (purge) permissions.Add(AuthorizationPermissions.ArtifactsPurge);
        var state = new ConsoleContextState(new ResourceListContextProvider { Permissions = permissions });
        await state.EnsureLoadedAsync(CancellationToken.None);
        context.Services.AddSingleton(state);
        var cut = StagedDetails(context, new ArtifactClient("Local bytes"));
        Assert.AreEqual(!canDelete, cut.Find("[data-testid=staged-artifact-delete]").HasAttribute("disabled"));
        SelectSection(context, "retention");
        Assert.AreEqual(!canExtend, cut.Find("input[type=datetime-local]").HasAttribute("disabled"));
    }
}
