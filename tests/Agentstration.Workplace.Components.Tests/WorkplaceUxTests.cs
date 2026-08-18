using System.Text.Json;
using Agentstration.Resources;
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
            .Add(value => value.Role, DashboardItemRole.Primary)
            .Add(value => value.OnSubmit, _ => Task.CompletedTask)));
        var standard = context.Render<EntryRenderer>(parameters => parameters
            .Add(value => value.Definition, PromptDefinition())
            .Add(value => value.Role, DashboardItemRole.Standard)
            .Add(value => value.OnSubmit, _ => Task.CompletedTask));

        Assert.IsTrue(primary.Markup.Contains("What would you like to accomplish?", StringComparison.Ordinal));
        Assert.AreEqual(1, primary.FindAll("h2#primary-entry-heading").Count);
        Assert.AreEqual(0, primary.FindAll("h1").Count);
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
        var workspaceId = new WorkspaceId(Guid.Parse("22222222-2222-2222-2222-222222222222"));
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
            .Add(value => value.Presentation, new EntryPresentation { Results = new(EntryResultDisplay.Visible) })
            .Add(value => value.Results, results)
            .Add(value => value.Artifacts, artifacts)
            .Add(value => value.ArtifactContentUrl, value => $"/artifacts/{value.Id}"));

        Assert.IsTrue(rendered.Markup.Contains("style: concise", StringComparison.Ordinal));
        Assert.AreEqual(2, rendered.FindAll(".task-result").Count);
        Assert.AreEqual(2, rendered.FindAll(".artifact-card").Count);
        Assert.IsFalse(rendered.Markup.Contains("hidden-1", StringComparison.Ordinal));
    }

    [TestMethod]
    public void CompactDefaultsHideParticipantMechanicsAndDuplicateTextResults()
    {
        using var context = new BunitContext();
        var workspaceId = new WorkspaceId(Guid.Parse("22222222-2222-2222-2222-222222222222"));
        var interactionId = InteractionId.New();
        var taskId = new WorkTaskId(Guid.NewGuid());
        var participant = new ConversationMessage(Guid.NewGuid(), workspaceId, interactionId, taskId, ConversationRole.Agentstration, "Participant detail", DateTimeOffset.UtcNow, "agent-a", Metadata: new Dictionary<string, string> { ["participantId"] = "alice" });
        var summary = new ConversationMessage(Guid.NewGuid(), workspaceId, interactionId, taskId, ConversationRole.Agentstration, "Final answer", DateTimeOffset.UtcNow.AddSeconds(1));
        var text = new WorkTaskResult(WorkTaskResultId.New(), workspaceId, taskId, "run-1", WorkTaskResultKind.Text, "Text", JsonSerializer.SerializeToElement("Final answer"), DateTimeOffset.UtcNow.AddSeconds(2));
        var envelope = new WorkTaskResult(WorkTaskResultId.New(), workspaceId, taskId, "run-1", WorkTaskResultKind.Structured, "Duplicate envelope", JsonSerializer.SerializeToElement(new { finalOutput = "Final answer" }), DateTimeOffset.UtcNow.AddSeconds(3));
        var structured = new WorkTaskResult(WorkTaskResultId.New(), workspaceId, taskId, "run-1", WorkTaskResultKind.Table, "Comparison", JsonSerializer.SerializeToElement(new { rows = 2 }), DateTimeOffset.UtcNow.AddSeconds(4));
        var enriched = new WorkTaskResult(WorkTaskResultId.New(), workspaceId, taskId, "run-1", WorkTaskResultKind.Structured, "Enriched answer", JsonSerializer.SerializeToElement(new { finalOutput = "Final answer", confidence = 0.9 }), DateTimeOffset.UtcNow.AddSeconds(5));

        var rendered = context.Render<InteractionView>(parameters => parameters
            .Add(value => value.Messages, [participant, summary])
            .Add(value => value.Results, [text, envelope, structured, enriched])
            .Add(value => value.ArtifactContentUrl, _ => "/content"));

        Assert.IsFalse(rendered.Markup.Contains("Participant detail", StringComparison.Ordinal));
        Assert.IsTrue(rendered.Markup.Contains("Final answer", StringComparison.Ordinal));
        Assert.AreEqual(2, rendered.FindAll(".task-result").Count);
        Assert.IsFalse(rendered.Markup.Contains("Duplicate envelope", StringComparison.Ordinal));
        Assert.IsTrue(rendered.Markup.Contains("Comparison", StringComparison.Ordinal));
        Assert.IsTrue(rendered.Markup.Contains("Enriched answer", StringComparison.Ordinal));
    }

    [TestMethod]
    public void VisibleParticipantsAreAttributedInsideTheUnifiedTimeline()
    {
        using var context = new BunitContext();
        var workspaceId = new WorkspaceId(Guid.Parse("22222222-2222-2222-2222-222222222222"));
        var message = new ConversationMessage(Guid.NewGuid(), workspaceId, InteractionId.New(), null, ConversationRole.Agentstration, "Is it a real person?", DateTimeOffset.UtcNow, "alice-agent", Metadata: new Dictionary<string, string> { ["participantId"] = "alice-player" });
        var rendered = context.Render<InteractionView>(parameters => parameters
            .Add(value => value.Presentation, new EntryPresentation { Participants = new(EntryParticipantVisibility.Visible) })
            .Add(value => value.Messages, [message])
            .Add(value => value.ArtifactContentUrl, _ => "/content"));

        Assert.IsTrue(rendered.Markup.Contains("Alice Player", StringComparison.Ordinal));
        Assert.IsTrue(rendered.Markup.Contains("Is it a real person?", StringComparison.Ordinal));
    }

    [TestMethod]
    public void MessagesActivitiesResultsAndArtifactsAreOrderedAsOneTimeline()
    {
        using var context = new BunitContext();
        var now = new DateTimeOffset(2026, 8, 18, 10, 0, 0, TimeSpan.Zero);
        var workspaceId = new WorkspaceId(Guid.Parse("22222222-2222-2222-2222-222222222222"));
        var interactionId = InteractionId.New();
        var taskId = new WorkTaskId(Guid.NewGuid());
        var message = new ConversationMessage(Guid.NewGuid(), workspaceId, interactionId, taskId, ConversationRole.User, "Compare these options", now);
        var activity = new WorkTaskActivity(WorkTaskActivityId.New(), workspaceId, taskId, WorkTaskActivityType.TaskStarted, "Comparing options", null, now.AddSeconds(1), WorkActorKind.Agentstration);
        var result = new WorkTaskResult(WorkTaskResultId.New(), workspaceId, taskId, "run-1", WorkTaskResultKind.Table, "Comparison table", JsonSerializer.SerializeToElement(new { rows = 3 }), now.AddSeconds(2));
        var artifact = new WorkTaskArtifact(WorkTaskArtifactId.New(), workspaceId, taskId, "run-1", "comparison.csv", "text/csv", 32, "private-key", now.AddSeconds(3));
        var rendered = context.Render<InteractionView>(parameters => parameters
            .Add(value => value.Presentation, new EntryPresentation { Progress = new(EntryProgressVisibility.Detailed) })
            .Add(value => value.Messages, [message])
            .Add(value => value.Activities, [activity])
            .Add(value => value.Results, [result])
            .Add(value => value.Artifacts, [artifact])
            .Add(value => value.ArtifactContentUrl, _ => "/content"));

        var markup = rendered.Markup;
        Assert.IsTrue(markup.IndexOf("Compare these options", StringComparison.Ordinal) < markup.IndexOf("Comparing options", StringComparison.Ordinal));
        Assert.IsTrue(markup.IndexOf("Comparing options", StringComparison.Ordinal) < markup.IndexOf("Comparison table", StringComparison.Ordinal));
        Assert.IsTrue(markup.IndexOf("Comparison table", StringComparison.Ordinal) < markup.IndexOf("comparison.csv", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ProgressAndArtifactsExposeFunctionalInformationWithoutStorageDetails()
    {
        using var context = new BunitContext();
        var workspaceId = new WorkspaceId(Guid.Parse("22222222-2222-2222-2222-222222222222"));
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
        WorkspaceId = new(Guid.Parse("22222222-2222-2222-2222-222222222222")),
        Id = new EntryId("prepare-report"),
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
        ResolvedTarget = new EntryResolvedTarget("report", "1.0.0"),
        Behavior = new EntryBehavior(),
        ApiVersion = WorkplaceApiVersions.CoreV1,
        Type = WorkResourceTypes.Entries
    };
}
