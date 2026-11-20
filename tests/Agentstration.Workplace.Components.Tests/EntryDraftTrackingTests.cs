using System.Text.Json;
using Agentstration.Resources;
using Agentstration.Work;
using Agentstration.Workplace.Components;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Workplace.Components.Tests;

[TestClass]
public sealed class EntryDraftTrackingTests
{
    [TestMethod]
    public void PromptTrackingIncludesTypingClearAndSuggestionsWithoutSubmitting()
    {
        using var context = Context(); var changes = new List<bool>(); var submissions = 0;
        var page = context.Render<EntryRenderer>(p => p.Add(value => value.Definition, Definition()).Add(value => value.OnSubmit, _ => ++submissions).Add(value => value.DraftChanged, value => changes.Add(value)));
        page.Find("textarea").Input("Before blur"); Assert.AreEqual(true, changes[^1]); page.Find("textarea").Input(""); Assert.AreEqual(false, changes[^1]);
        page.FindAll("button").Single(value => value.TextContent.Contains("Suggestion", StringComparison.Ordinal)).Click(); Assert.AreEqual(true, changes[^1]); Assert.AreEqual(0, submissions);
    }
    [TestMethod]
    public async Task InvalidFormValuesRemainPendingAndDoNotInvokeTheSubmission()
    {
        using var context = Context(); var dirty = false; var submits = 0; var definition = Definition() with { Presentation = new() { Kind = EntryPresentationKind.Form, Fields = [new() { Name = "request", Type = EntryFieldType.Text, Required = true }, new() { Name = "missing", Type = EntryFieldType.Text, Required = true }] } };
        var page = context.Render<EntryRenderer>(p => p.Add(value => value.Definition, definition).Add(value => value.OnSubmit, _ => ++submits).Add(value => value.DraftChanged, value => dirty = value));
        page.FindAll("input")[0].Input("Retained input"); Assert.IsTrue(dirty); await page.Find("form").SubmitAsync(); Assert.AreEqual(0, submits); Assert.IsTrue(dirty); Assert.AreEqual("Retained input", page.FindAll("input")[0].GetAttribute("value"));
    }
    [TestMethod]
    public async Task AcknowledgedComposerClearsOnlyAnAcceptedMessageAndDefaultSubmissionStillClears()
    {
        using var context = Context(); var accepted = false; var dirty = false; var messages = new List<string>();
        var page = context.Render<ConversationComposer>(p => p.Add(value => value.TrySubmit, content => { messages.Add(content); return Task.FromResult(accepted); }).Add(value => value.DraftChanged, value => dirty = value));
        page.Find("textarea").Input("Retain rejected message"); await page.Find("form").SubmitAsync(); Assert.IsTrue(dirty); Assert.AreEqual("Retain rejected message", page.Find("textarea").GetAttribute("value"));
        accepted = true; await page.Find("form").SubmitAsync(); Assert.IsFalse(dirty); Assert.AreEqual("", page.Find("textarea").GetAttribute("value")); CollectionAssert.AreEqual(new[] { "Retain rejected message", "Retain rejected message" }, messages);
        var legacy = context.Render<ConversationComposer>(p => p.Add(value => value.OnSubmit, _ => Task.CompletedTask)); legacy.Find("textarea").Input("Existing callback"); await legacy.Find("form").SubmitAsync(); Assert.AreEqual("", legacy.Find("textarea").GetAttribute("value"));
    }
    [TestMethod]
    public async Task ADisposedComposerDoesNotClearOrFocusOnALateAcknowledgement()
    {
        using var context = Context(); var pending = new TaskCompletionSource<bool>(); var page = context.Render<ConversationComposer>(p => p.Add(value => value.TrySubmit, _ => pending.Task)); page.Find("textarea").Input("Old context");
        var submission = page.Find("form").SubmitAsync(); page.Instance.Dispose(); await page.InvokeAsync(() => pending.SetResult(true)); await submission;
        Assert.IsEmpty(context.JSInterop.Invocations.Where(value => value.Identifier.Contains("focus", StringComparison.OrdinalIgnoreCase)));
    }
    [TestMethod]
    public void PendingResponseAcknowledgementPreservesAnIndependentComposerDraft()
    {
        using var context = Context(); var changes = new List<bool>(); var page = context.Render<InteractionView>(p => p.Add(value => value.ArtifactContentUrl, _ => "/fixture").Add(value => value.DraftChanged, value => changes.Add(value)));
        page.Find("#conversation-message").Input("Independent message"); Assert.IsTrue(page.Instance.HasMessageDraft);
        var action = new RequestInputAction("Response", null, [new() { Name = "answer", Type = EntryFieldType.Textarea }], PendingActionId.New(), "public-fixture"); page.Render(p => p.Add(value => value.Action, action));
        page.Find(".pending-conversation textarea").Input("Pending response"); Assert.IsTrue(page.Instance.HasPendingDraft); page.Render(p => p.Add(value => value.ResponseAcknowledgement, 1));
        Assert.IsFalse(page.Instance.HasPendingDraft); Assert.IsTrue(page.Instance.HasMessageDraft); Assert.AreEqual("Independent message", page.Find("#conversation-message").GetAttribute("value")); Assert.AreEqual(true, changes[^1]);
    }
    private static BunitContext Context() { var context = new BunitContext(); context.JSInterop.Mode = JSRuntimeMode.Loose; context.Services.AddLocalization(options => options.ResourcesPath = "Resources"); return context; }
    private static EntryResource Definition() => new() { WorkspaceId = new(Guid.Parse("22222222-2222-2222-2222-222222222222")), Id = new EntryId("fixture"), Name = "fixture", DisplayName = "Fixture", Presentation = new() { Kind = EntryPresentationKind.Prompt, Fields = [new() { Name = "request", Type = EntryFieldType.Prompt, Role = EntryFieldRole.PrimaryInput }], Suggestions = [new("Suggestion", "Suggested request")] }, ResolvedTarget = new("fixture-flow", "1.0.0"), Behavior = new(), ApiVersion = ResourceApiVersions.CoreV1, Type = WorkResourceTypes.Entries };
}
