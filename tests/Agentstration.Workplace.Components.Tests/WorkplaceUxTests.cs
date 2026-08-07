using System.Text.Json;
using Agentstration.Work;
using Agentstration.Workplace.Components;
using Bunit;
using Microsoft.AspNetCore.Components;

namespace Agentstration.Workplace.Components.Tests;

[TestClass]
public sealed class WorkplaceUxTests
{
    [TestMethod]
    public async Task PromptSuggestionFillsComposerAndSubmissionStaysStructured()
    {
        using var context = new BunitContext();
        IReadOnlyDictionary<string, JsonElement>? submitted = null;
        var rendered = context.Render<PromptEntry>(parameters => parameters
            .Add(value => value.Definition, PromptDefinition())
            .Add(value => value.OnSubmit, EventCallback.Factory.Create<IReadOnlyDictionary<string, JsonElement>>(this, value => submitted = value)));

        await rendered.FindAll("button").Single(value => value.TextContent == "Monthly report").ClickAsync(new());
        Assert.IsNull(submitted, "A suggestion fills the composer but must not submit without user confirmation.");
        Assert.AreEqual("Prepare my monthly report", rendered.Find("textarea").GetAttribute("value"));

        await rendered.Find("form").SubmitAsync();
        Assert.AreEqual("Prepare my monthly report", submitted?["request"].GetString());
    }

    [TestMethod]
    public void PrimaryContainerAddsEmphasisWithoutChangingTheGenericRenderer()
    {
        using var context = new BunitContext();
        var primary = context.Render<PrimaryEntryContainer>(parameters => parameters.AddChildContent<EntryRenderer>(entry => entry
            .Add(value => value.Definition, PromptDefinition())
            .Add(value => value.Role, WorkspaceEntryRole.Primary)
            .Add(value => value.OnSubmit, _ => Task.CompletedTask)));
        var standard = context.Render<EntryRenderer>(parameters => parameters
            .Add(value => value.Definition, PromptDefinition())
            .Add(value => value.Role, WorkspaceEntryRole.Standard)
            .Add(value => value.OnSubmit, _ => Task.CompletedTask));

        Assert.IsTrue(primary.Markup.Contains("What would you like to accomplish?", StringComparison.Ordinal));
        Assert.AreEqual(1, primary.FindAll(".prompt-composer").Count);
        Assert.AreEqual(1, standard.FindAll(".prompt-composer").Count);
    }

    [TestMethod]
    public void PendingActionIsRenderedInsideTheConversationFlow()
    {
        using var context = new BunitContext();
        var action = new RequestConfirmationAction("Generate the report?", "A task will be created.", PendingActionId.New(), "browser-only-token");
        var rendered = context.Render<InteractionView>(parameters => parameters
            .Add(value => value.Action, action)
            .Add(value => value.ArtifactContentUrl, _ => "/content"));

        Assert.AreEqual(1, rendered.FindAll(".conversation-thread .pending-conversation").Count);
        Assert.IsTrue(rendered.Markup.Contains("Action required", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ComposerRemainsVisibleWhenConversationIsIdleAfterWorkCompletes()
    {
        using var context = new BunitContext();
        var rendered = context.Render<InteractionView>(parameters => parameters
            .Add(value => value.Status, InteractionStatus.Idle)
            .Add(value => value.ArtifactContentUrl, _ => "/content"));

        Assert.AreEqual(1, rendered.FindAll(".conversation-composer").Count);
        Assert.IsFalse(rendered.Find("#conversation-message").HasAttribute("disabled"));
    }

    [TestMethod]
    public void BlockingQuestionExplainsWhyPermanentComposerIsDisabled()
    {
        using var context = new BunitContext();
        var action = new RequestChoiceAction("Which style?", null, [new("concise", "Concise")], PendingActionId.New(), "token", "style");
        var rendered = context.Render<InteractionView>(parameters => parameters
            .Add(value => value.Status, InteractionStatus.WaitingForUser)
            .Add(value => value.Action, action)
            .Add(value => value.ArtifactContentUrl, _ => "/content"));

        Assert.IsTrue(rendered.Find("#conversation-message").HasAttribute("disabled"));
        Assert.IsTrue(rendered.Markup.Contains("Answer the question above to continue.", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ResolvedPendingAnswerAndSuccessiveOutputsRemainReadableInTheThread()
    {
        using var context = new BunitContext();
        var workspaceId = new WorkplaceWorkspaceId("/resourceGroups/default/providers/Agentstration.Work/workspaces/personal");
        var interactionId = InteractionId.New();
        var taskId = new WorkTaskId(Guid.NewGuid());
        var answer = new ConversationMessage(Guid.NewGuid(), workspaceId, interactionId, taskId, ConversationRole.User, "style: concise", DateTimeOffset.UtcNow, PendingActionId: PendingActionId.New());
        var results = new[]
        {
            new WorkTaskResult(WorkTaskResultId.New(), workspaceId, taskId, "run-1", WorkTaskResultKind.Text, "Initial report", JsonSerializer.SerializeToElement("Initial"), DateTimeOffset.UtcNow, 1),
            new WorkTaskResult(WorkTaskResultId.New(), workspaceId, taskId, "run-2", WorkTaskResultKind.Text, "Executive version", JsonSerializer.SerializeToElement("Executive"), DateTimeOffset.UtcNow.AddSeconds(1), 2)
        };
        var artifacts = new[]
        {
            new WorkTaskArtifact(WorkTaskArtifactId.New(), workspaceId, taskId, "run-1", "report.txt", "text/plain", 10, "hidden-1", DateTimeOffset.UtcNow, 1),
            new WorkTaskArtifact(WorkTaskArtifactId.New(), workspaceId, taskId, "run-2", "executive.txt", "text/plain", 8, "hidden-2", DateTimeOffset.UtcNow.AddSeconds(1), 2)
        };
        var rendered = context.Render<InteractionView>(parameters => parameters
            .Add(value => value.Messages, [answer])
            .Add(value => value.Status, InteractionStatus.Idle)
            .Add(value => value.Results, results)
            .Add(value => value.Artifacts, artifacts)
            .Add(value => value.ArtifactContentUrl, value => $"/artifacts/{value.Id}"));

        Assert.IsTrue(rendered.Markup.Contains("style: concise", StringComparison.Ordinal));
        Assert.AreEqual(2, rendered.FindAll(".task-result").Count);
        Assert.AreEqual(2, rendered.FindAll(".artifact-card").Count);
        Assert.IsFalse(rendered.Markup.Contains("hidden-1", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ProgressAndArtifactsExposeFunctionalInformationWithoutStorageDetails()
    {
        using var context = new BunitContext();
        var workspaceId = new WorkplaceWorkspaceId("/resourceGroups/default/providers/Agentstration.Work/workspaces/personal");
        var taskId = new WorkTaskId(Guid.NewGuid());
        var activity = new WorkTaskActivity(WorkTaskActivityId.New(), workspaceId, taskId, WorkTaskActivityType.TaskStarted, "Generating report", "Building the requested content.", DateTimeOffset.UtcNow, WorkActorKind.System);
        var progress = context.Render<TaskProgressTimeline>(parameters => parameters.Add(value => value.Activities, [activity]).Add(value => value.Status, WorkTaskStatus.Running));
        Assert.IsTrue(progress.Markup.Contains("Generating report", StringComparison.Ordinal));
        Assert.IsTrue(progress.Markup.Contains("In progress", StringComparison.Ordinal));

        var artifact = new WorkTaskArtifact(WorkTaskArtifactId.New(), workspaceId, taskId, null, "report.md", "text/markdown", 2048, "private/storage/key", DateTimeOffset.UtcNow);
        var card = context.Render<ArtifactCard>(parameters => parameters.Add(value => value.Artifact, artifact).Add(value => value.ContentUrl, "/download"));
        Assert.IsTrue(card.Markup.Contains("2 KB", StringComparison.Ordinal));
        Assert.IsFalse(card.Markup.Contains("private/storage/key", StringComparison.Ordinal));
    }

    private static EntryResource PromptDefinition() => new()
    {
        Id = new EntryId("/resourceGroups/default/providers/Agentstration.Work/entries/prepare-report"),
        Name = "prepare-report",
        DisplayName = "Prepare a report",
        Description = "Describe the expected outcome.",
        Presentation = new EntryPresentation
        {
            Kind = EntryPresentationKind.Prompt,
            Placeholder = "What should the report cover?",
            Fields = [new EntryFieldDefinition { Name = "request", Type = EntryFieldType.Textarea, Required = true }],
            Suggestions = [new("Monthly report", "Prepare my monthly report")]
        },
        ResolvedTarget = new EntryResolvedTarget("/resourceGroups/default/providers/Agentstration.Flows/flows/report", "1.0.0"),
        Behavior = new EntryBehavior(),
        ResourceGroup = "default",
        Location = "local",
        ApiVersion = WorkplaceApiVersions.V20260805,
        Type = WorkResourceTypes.Entries
    };
}
