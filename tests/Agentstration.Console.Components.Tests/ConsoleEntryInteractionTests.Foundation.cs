using System.Net;
using Agentstration.Web.Components;
using Agentstration.Web.Components.Models;
using Agentstration.Web.Components.Pages;
using Agentstration.Web.Console;
using Agentstration.Work;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Console.Components.Tests;

public sealed partial class ConsoleEntryInteractionTests
{
    [TestMethod]
    public async Task RequestInputSurvivesRefreshAndChangedDefinitionRequiresExplicitDiscard()
    {
        using var context = CreateContext(out var entries, out _); var cut = Render(context);
        cut.WaitForElement("textarea").Input("Private request before blur");
        var request = cut.FindComponent<Agentstration.Workplace.Components.PromptEntry>().Instance;
        await Refresh(cut);
        Assert.AreSame(request, cut.FindComponent<Agentstration.Workplace.Components.PromptEntry>().Instance);
        Assert.AreEqual("Private request before blur", cut.Find("textarea").GetAttribute("value"));
        entries.Available = [Entry(true) with { Version = 2 }]; await Refresh(cut);
        Assert.IsTrue(cut.FindComponent<ResourceDetailsTemplate>().Instance.HasDraft);
        Assert.IsTrue(cut.Find("[data-testid='workplace-prompt-submit']").HasAttribute("disabled"));
        Assert.AreEqual("Private request before blur", cut.Find("textarea").GetAttribute("value"));
        await cut.Find(".unsaved-changes-bar button").ClickAsync(new());
        Assert.AreEqual("", cut.Find("textarea").GetAttribute("value"));
        Assert.IsFalse(cut.FindComponent<ResourceDetailsTemplate>().Instance.HasDraft);
    }
    [TestMethod]
    public async Task InputDuringAReadAndRecoverableFailureDoesNotGetReplacedOrAutomaticallySubmitted()
    {
        using var context = CreateContext(out var entries, out _); var cut = Render(context);
        entries.NextDiscovery = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var refresh = Refresh(cut); cut.WaitForAssertion(() => Assert.AreEqual(ResourceLoadStatus.Refreshing, State(cut).Status));
        cut.Find("textarea").Input("Typed while refresh was pending");
        entries.NextDiscovery.SetResult(entries.Available); await refresh; entries.NextDiscovery = null;
        entries.DiscoveryError = new HttpRequestException("offline"); await Refresh(cut);
        Assert.AreEqual(ResourceLoadStatus.Failed, State(cut).Status); Assert.IsTrue(State(cut).HasContent);
        Assert.AreEqual("Typed while refresh was pending", cut.Find("textarea").GetAttribute("value")); Assert.IsNull(entries.LastSubmission);
    }
    [TestMethod]
    public async Task RejectedInitialRequestRemainsEditableAndIsNeverRetriedByRefresh()
    {
        using var context = CreateContext(out var entries, out _); var cut = Render(context);
        entries.SubmitError = new AgentstrationApiException("refused", "fixture", HttpStatusCode.BadRequest);
        cut.Find("textarea").Input("Retained rejected request"); await cut.Find("form").SubmitAsync();
        Assert.AreEqual("Retained rejected request", cut.Find("textarea").GetAttribute("value"));
        Assert.IsTrue(cut.FindComponent<ResourceDetailsTemplate>().Instance.HasDraft);
        entries.SubmitError = null; await Refresh(cut);
        Assert.IsEmpty(cut.FindAll("[data-testid='workplace-interaction']"));
        Assert.AreEqual("Retained rejected request", cut.Find("textarea").GetAttribute("value"));
    }
    [TestMethod]
    public async Task DeniedReadsAndReplacedIdentityClearPrivateRequestsAndDraftActions()
    {
        foreach (var replacement in new[] { false, true })
        {
            using var context = CreateContext(out var entries, out _); var cut = Render(context);
            cut.Find("textarea").Input("Must disappear");
            if (replacement) entries.Available = [Entry(true) with { Id = "replacement-entry" }];
            else entries.DiscoveryError = new AgentstrationApiException("denied", "fixture", HttpStatusCode.Forbidden);
            await Refresh(cut); Assert.IsEmpty(cut.FindAll("textarea")); Assert.DoesNotContain("Must disappear", cut.Markup);
            Assert.IsFalse(cut.FindComponent<ResourceDetailsTemplate>().Instance.HasDraft);
            Assert.AreEqual(replacement ? ResourceLoadStatus.NotFound : ResourceLoadStatus.Forbidden, State(cut).Status);
        }
    }
    [TestMethod]
    public async Task FailedConversationSendRetainsMessageAndForeignReplayPurgesIt()
    {
        using var context = CreateContext(out _, out var interactions); var cut = Render(context, InteractionId);
        cut.WaitForElement("#conversation-message").Input("Independent unsent message");
        interactions.ContinueError = new HttpRequestException("offline"); await cut.Find(".conversation-composer form").SubmitAsync();
        Assert.AreEqual("Independent unsent message", cut.Find("#conversation-message").GetAttribute("value"));
        await Refresh(cut); Assert.AreEqual("Independent unsent message", cut.Find("#conversation-message").GetAttribute("value"));
        interactions.Interaction = interactions.Interaction with { WorkspaceId = Guid.NewGuid() }; await Refresh(cut);
        Assert.AreEqual(ResourceLoadStatus.NotFound, State(cut).Status); Assert.IsEmpty(cut.FindAll("textarea")); Assert.DoesNotContain("Independent unsent message", cut.Markup);
    }
    [TestMethod]
    public async Task LateReadFromPreviousEntryCannotRestoreOldPrivateInput()
    {
        using var context = CreateContext(out var entries, out _); var cut = Render(context);
        cut.Find("textarea").Input("Old private input"); entries.NextDiscovery = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var old = entries.NextDiscovery; var refresh = Refresh(cut);
        cut.WaitForAssertion(() => Assert.AreEqual(ResourceLoadStatus.Refreshing, State(cut).Status));
        entries.NextDiscovery = null; entries.Available = [Entry(true, "new-entry")]; cut.Render(p => p.Add(value => value.EntryName, "new-entry"));
        cut.WaitForAssertion(() => Assert.AreEqual("new-entry", cut.Find("[data-entry-name]").GetAttribute("data-entry-name")));
        old.SetResult([Entry(true)]); await refresh;
        Assert.AreEqual("new-entry", cut.Find("[data-entry-name]").GetAttribute("data-entry-name")); Assert.DoesNotContain("Old private input", cut.Markup);
    }
    [TestMethod]
    public async Task InspectionRetainsItsDraftButRevokedInvocationDisablesConversationCommands()
    {
        using var context = CreateContext(out var entries, out _); var cut = Render(context, InteractionId);
        cut.WaitForElement("#conversation-message").Input("Inspect without invoking"); entries.Available = [Entry(false)]; await Refresh(cut);
        Assert.AreEqual(ResourceLoadStatus.Ready, State(cut).Status);
        Assert.AreEqual("Inspect without invoking", cut.Find("#conversation-message").GetAttribute("value"));
        Assert.IsTrue(cut.Find("#conversation-message").HasAttribute("disabled"));
        Assert.IsTrue(cut.Find("[data-testid='workplace-composer-submit']").HasAttribute("disabled"));
    }
    private static Task Refresh(IRenderedComponent<ConsoleEntryInteraction> cut) => cut.InvokeAsync(() => cut.FindComponent<ResourceDetailsTemplate>().Instance.OnRefresh.InvokeAsync());
    private static ResourceLoadState State(IRenderedComponent<ConsoleEntryInteraction> cut) => cut.FindComponent<ResourceDetailsTemplate>().Instance.LoadState!;
}
