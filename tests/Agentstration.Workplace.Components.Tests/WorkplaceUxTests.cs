using System.Text.Json;
using Agentstration.Resources;
using Agentstration.Work;
using Agentstration.Work.Contracts;
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
        Assert.IsTrue(rendered.Markup.Contains("Response needed", StringComparison.Ordinal));
        Assert.IsFalse(rendered.Markup.Contains("Action required", StringComparison.Ordinal));
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
    public void CompactDefaultsHideParticipantMechanicsAndTechnicalResults()
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
        Assert.AreEqual(0, rendered.FindAll(".task-result").Count);
        Assert.AreEqual(0, rendered.FindAll(".result-diagnostics-toggle").Count);
        Assert.IsFalse(rendered.Markup.Contains("Duplicate envelope", StringComparison.Ordinal));
        Assert.IsFalse(rendered.Markup.Contains("Comparison", StringComparison.Ordinal));
        Assert.IsFalse(rendered.Markup.Contains("Enriched answer", StringComparison.Ordinal));
    }

    [TestMethod]
    public void DevelopmentDiagnosticsRevealAutomaticResultsOnlyOnDemand()
    {
        using var context = new BunitContext();
        var workspaceId = new WorkspaceId(Guid.Parse("22222222-2222-2222-2222-222222222222"));
        var taskId = new WorkTaskId(Guid.NewGuid());
        var result = new WorkTaskResult(WorkTaskResultId.New(), workspaceId, taskId, "run-1", WorkTaskResultKind.Structured, "Execution result", JsonSerializer.SerializeToElement(new { finalOutput = "Answer", participants = new[] { "alice", "bob" } }), DateTimeOffset.UtcNow);
        var rendered = context.Render<InteractionView>(parameters => parameters
            .Add(value => value.DiagnosticsEnabled, true)
            .Add(value => value.Results, [result])
            .Add(value => value.ArtifactContentUrl, _ => "/content"));

        var toggle = rendered.Find(".result-diagnostics-toggle");
        Assert.AreEqual("false", toggle.GetAttribute("aria-expanded"));
        Assert.AreEqual(0, rendered.FindAll(".task-result").Count);
        Assert.IsFalse(rendered.Markup.Contains("participants", StringComparison.Ordinal));

        toggle.Click();
        Assert.AreEqual("true", rendered.Find(".result-diagnostics-toggle").GetAttribute("aria-expanded"));
        Assert.AreEqual(1, rendered.FindAll(".task-result").Count);
        Assert.IsTrue(rendered.Markup.Contains("participants", StringComparison.Ordinal));
    }

    [TestMethod]
    public void CompactProgressKeepsCurrentWorkButDoesNotRepeatCompletionBesideTheAnswer()
    {
        using var context = new BunitContext();
        var now = new DateTimeOffset(2026, 8, 18, 10, 0, 0, TimeSpan.Zero);
        var workspaceId = new WorkspaceId(Guid.Parse("22222222-2222-2222-2222-222222222222"));
        var interactionId = InteractionId.New();
        var taskId = new WorkTaskId(Guid.NewGuid());
        var started = new WorkTaskActivity(WorkTaskActivityId.New(), workspaceId, taskId, WorkTaskActivityType.TaskStarted, "Analyzing your request", null, now, WorkActorKind.Agentstration);
        var completed = new WorkTaskActivity(WorkTaskActivityId.New(), workspaceId, taskId, WorkTaskActivityType.TaskCompleted, "Task completed", null, now.AddSeconds(1), WorkActorKind.Agentstration);
        var answer = new ConversationMessage(Guid.NewGuid(), workspaceId, interactionId, taskId, ConversationRole.Agentstration, "Here is the answer.", now.AddSeconds(1));

        var running = context.Render<InteractionView>(parameters => parameters
            .Add(value => value.Activities, [started])
            .Add(value => value.ArtifactContentUrl, _ => "/content"));
        Assert.IsTrue(running.Markup.Contains("Analyzing your request", StringComparison.Ordinal));

        var compact = context.Render<InteractionView>(parameters => parameters
            .Add(value => value.Messages, [answer])
            .Add(value => value.Activities, [started, completed])
            .Add(value => value.ArtifactContentUrl, _ => "/content"));
        Assert.IsFalse(compact.Markup.Contains("Task completed", StringComparison.Ordinal));

        var detailed = context.Render<InteractionView>(parameters => parameters
            .Add(value => value.Presentation, new EntryPresentation { Progress = new(EntryProgressVisibility.Detailed) })
            .Add(value => value.Messages, [answer])
            .Add(value => value.Activities, [started, completed])
            .Add(value => value.ArtifactContentUrl, _ => "/content"));
        Assert.IsTrue(detailed.Markup.Contains("Task completed", StringComparison.Ordinal));
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
    public void DefaultConversationUsesAlignmentInsteadOfRedundantSpeakerChrome()
    {
        using var context = new BunitContext();
        var workspaceId = new WorkspaceId(Guid.Parse("22222222-2222-2222-2222-222222222222"));
        var interactionId = InteractionId.New();
        var now = DateTimeOffset.UtcNow;
        var messages = new[]
        {
            new ConversationMessage(Guid.NewGuid(), workspaceId, interactionId, null, ConversationRole.User, "Can you help?", now),
            new ConversationMessage(Guid.NewGuid(), workspaceId, interactionId, null, ConversationRole.Agentstration, "Here is the answer.", now.AddSeconds(1))
        };
        var rendered = context.Render<InteractionView>(parameters => parameters
            .Add(value => value.Messages, messages)
            .Add(value => value.ArtifactContentUrl, _ => "/content"));

        Assert.AreEqual(0, rendered.FindAll(".message-avatar").Count);
        Assert.AreEqual(0, rendered.FindAll(".conversation-message header strong").Count);
        Assert.AreEqual("You", rendered.Find(".message-user").GetAttribute("aria-label"));
        Assert.AreEqual("Agentstration", rendered.Find(".message-assistant").GetAttribute("aria-label"));
    }

    [TestMethod]
    public void ParticipantProgressUsesEntryVisibilityWithoutExposingFlowTopology()
    {
        using var context = new BunitContext();
        var now = new DateTimeOffset(2026, 8, 18, 10, 0, 0, TimeSpan.Zero);
        var workspaceId = new WorkspaceId(Guid.Parse("22222222-2222-2222-2222-222222222222"));
        var taskId = new WorkTaskId(Guid.NewGuid());
        var metadata = new Dictionary<string, string> { ["participantId"] = "alice-player" };
        var started = new WorkTaskActivity(WorkTaskActivityId.New(), workspaceId, taskId, WorkTaskActivityType.ProgressStarted, "Preparing a response", null, now, WorkActorKind.Agentstration, "run-1", metadata);
        var completed = new WorkTaskActivity(WorkTaskActivityId.New(), workspaceId, taskId, WorkTaskActivityType.ProgressCompleted, "Response prepared", null, now.AddSeconds(1), WorkActorKind.Agentstration, "run-1", metadata);

        var hidden = context.Render<InteractionView>(parameters => parameters
            .Add(value => value.Presentation, new EntryPresentation { Progress = new(EntryProgressVisibility.Detailed) })
            .Add(value => value.Activities, [started, completed])
            .Add(value => value.ArtifactContentUrl, _ => "/content"));
        Assert.IsTrue(hidden.Markup.Contains("Preparing a response", StringComparison.Ordinal));
        Assert.IsTrue(hidden.Markup.Contains("Response prepared", StringComparison.Ordinal));
        Assert.IsFalse(hidden.Markup.Contains("Alice Player", StringComparison.Ordinal));

        var visible = context.Render<InteractionView>(parameters => parameters
            .Add(value => value.Presentation, new EntryPresentation
            {
                Participants = new(EntryParticipantVisibility.Visible),
                Progress = new(EntryProgressVisibility.Detailed)
            })
            .Add(value => value.Activities, [started, completed])
            .Add(value => value.ArtifactContentUrl, _ => "/content"));
        Assert.IsTrue(visible.Markup.Contains("Alice Player is preparing a response", StringComparison.Ordinal));
        Assert.IsTrue(visible.Markup.Contains("Alice Player responded", StringComparison.Ordinal));
        Assert.IsFalse(visible.Markup.Contains("StepRun", StringComparison.Ordinal));
    }

    [TestMethod]
    public void AutoTaskDisplayMaterializesOnlyDurableOrSubstantialWork()
    {
        using var context = new BunitContext();
        var now = new DateTimeOffset(2026, 8, 18, 10, 0, 0, TimeSpan.Zero);
        var workspaceId = new WorkspaceId(Guid.Parse("22222222-2222-2222-2222-222222222222"));
        var taskId = new WorkTaskId(Guid.NewGuid());
        var firstMilestone = new WorkTaskActivity(WorkTaskActivityId.New(), workspaceId, taskId, WorkTaskActivityType.ProgressCompleted, "First step ready", null, now.AddSeconds(5), WorkActorKind.Agentstration);
        var secondMilestone = new WorkTaskActivity(WorkTaskActivityId.New(), workspaceId, taskId, WorkTaskActivityType.ProgressCompleted, "Second step ready", null, now.AddSeconds(10), WorkActorKind.Agentstration);

        var shortWork = context.Render<InteractionView>(parameters => parameters
            .Add(value => value.Task, TaskResponse(taskId, now, now.AddSeconds(10)))
            .Add(value => value.Activities, [firstMilestone])
            .Add(value => value.ArtifactContentUrl, _ => "/content"));
        Assert.AreEqual(0, shortWork.FindAll(".inline-task-card").Count);

        var durableWork = context.Render<InteractionView>(parameters => parameters
            .Add(value => value.Task, TaskResponse(taskId, now, now.AddMinutes(1)))
            .Add(value => value.Activities, [firstMilestone])
            .Add(value => value.ArtifactContentUrl, _ => "/content"));
        Assert.AreEqual(1, durableWork.FindAll(".inline-task-card").Count);

        var substantialWork = context.Render<InteractionView>(parameters => parameters
            .Add(value => value.Task, TaskResponse(taskId, now, now.AddSeconds(10)))
            .Add(value => value.Activities, [firstMilestone, secondMilestone])
            .Add(value => value.ArtifactContentUrl, _ => "/content"));
        Assert.AreEqual(1, substantialWork.FindAll(".inline-task-card").Count);

        var deliverables = new[]
        {
            new WorkTaskArtifact(WorkTaskArtifactId.New(), workspaceId, taskId, "run-1", "report.md", "text/markdown", 20, "report", now.AddSeconds(5)),
            new WorkTaskArtifact(WorkTaskArtifactId.New(), workspaceId, taskId, "run-1", "data.csv", "text/csv", 20, "data", now.AddSeconds(6), 2)
        };
        var deliveredWork = context.Render<InteractionView>(parameters => parameters
            .Add(value => value.Task, TaskResponse(taskId, now, now.AddSeconds(10), WorkTaskStatus.Completed))
            .Add(value => value.Artifacts, deliverables)
            .Add(value => value.ArtifactContentUrl, _ => "/content"));
        Assert.AreEqual(1, deliveredWork.FindAll(".inline-task-card").Count);

        var actionableWork = context.Render<InteractionView>(parameters => parameters
            .Add(value => value.Task, TaskResponse(taskId, now, now.AddSeconds(1), WorkTaskStatus.ActionRequired))
            .Add(value => value.ArtifactContentUrl, _ => "/content"));
        Assert.AreEqual(1, actionableWork.FindAll(".inline-task-card").Count);

        var explicitlyHidden = context.Render<InteractionView>(parameters => parameters
            .Add(value => value.Presentation, new EntryPresentation { Task = new(EntryTaskDisplay.Hidden) })
            .Add(value => value.Task, TaskResponse(taskId, now, now.AddMinutes(2)))
            .Add(value => value.ArtifactContentUrl, _ => "/content"));
        Assert.AreEqual(0, explicitlyHidden.FindAll(".inline-task-card").Count);

        var explicitlyVisible = context.Render<InteractionView>(parameters => parameters
            .Add(value => value.Presentation, new EntryPresentation { Task = new(EntryTaskDisplay.Visible) })
            .Add(value => value.Task, TaskResponse(taskId, now, now.AddSeconds(1)))
            .Add(value => value.TaskDetailsUrl, task => $"/w/personal/tasks/{task.Id}")
            .Add(value => value.ArtifactContentUrl, _ => "/content"));
        Assert.AreEqual(1, explicitlyVisible.FindAll(".inline-task-card").Count);
        var taskLink = explicitlyVisible.Find("a.inline-task-card");
        Assert.AreEqual($"/w/personal/tasks/{taskId}", taskLink.GetAttribute("href"));
        Assert.IsTrue(taskLink.TextContent.Contains("Prepare report", StringComparison.Ordinal));
        Assert.IsFalse(taskLink.TextContent.Contains("View details", StringComparison.Ordinal));
        Assert.IsFalse(taskLink.TextContent.Contains("result", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(taskLink.TextContent.Contains("file", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void RunningTaskWithoutActivityUsesTransientWaitingFeedback()
    {
        using var context = new BunitContext();
        var now = new DateTimeOffset(2026, 8, 18, 10, 0, 0, TimeSpan.Zero);
        var taskId = new WorkTaskId(Guid.NewGuid());
        var rendered = context.Render<InteractionView>(parameters => parameters
            .Add(value => value.Task, TaskResponse(taskId, now, now.AddSeconds(1)))
            .Add(value => value.ArtifactContentUrl, _ => "/content"));

        Assert.AreEqual(1, rendered.FindAll(".processing-feedback").Count);
        Assert.IsTrue(rendered.Markup.Contains("Agentstration is working on your request", StringComparison.Ordinal));
        Assert.IsFalse(rendered.Markup.Contains("I’ve started the work", StringComparison.Ordinal));
    }

    [TestMethod]
    public void TaskProgressCollapsesCompletedTurnsAndKeepsOnlyUnresolvedWorkCurrent()
    {
        using var context = new BunitContext();
        var now = new DateTimeOffset(2026, 8, 18, 10, 0, 0, TimeSpan.Zero);
        var workspaceId = new WorkspaceId(Guid.Parse("22222222-2222-2222-2222-222222222222"));
        var taskId = new WorkTaskId(Guid.NewGuid());
        var metadata = new Dictionary<string, string> { ["participantId"] = "alice", ["participantTurn"] = "1" };
        var started = new WorkTaskActivity(WorkTaskActivityId.New(), workspaceId, taskId, WorkTaskActivityType.ProgressStarted, "Preparing a response", null, now, WorkActorKind.Agentstration, "run-1", metadata);
        var completed = new WorkTaskActivity(WorkTaskActivityId.New(), workspaceId, taskId, WorkTaskActivityType.ProgressCompleted, "Response prepared", null, now.AddSeconds(1), WorkActorKind.Agentstration, "run-1", metadata);

        var active = context.Render<TaskProgressTimeline>(parameters => parameters
            .Add(value => value.Activities, [started])
            .Add(value => value.Status, WorkTaskStatus.Running));
        Assert.AreEqual(1, active.FindAll(".progress-step.current").Count);
        Assert.IsTrue(active.Find(".progress-step.current").TextContent.Contains("Preparing a response", StringComparison.Ordinal));
        Assert.IsFalse(active.Markup.Contains(">In progress<", StringComparison.Ordinal));

        var advanced = context.Render<TaskProgressTimeline>(parameters => parameters
            .Add(value => value.Activities, [started, completed])
            .Add(value => value.Status, WorkTaskStatus.Running));
        Assert.IsFalse(advanced.Markup.Contains("Preparing a response", StringComparison.Ordinal));
        Assert.AreEqual(1, advanced.FindAll(".progress-step.completed").Count);
        Assert.IsTrue(advanced.Markup.Contains("Response prepared", StringComparison.Ordinal));
        Assert.AreEqual(1, advanced.FindAll(".progress-step.current").Count);
        Assert.IsTrue(advanced.Find(".progress-step.current").TextContent.Contains("In progress", StringComparison.Ordinal));
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
            .Add(value => value.Presentation, new EntryPresentation { Progress = new(EntryProgressVisibility.Detailed), Results = new(EntryResultDisplay.Visible) })
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
        Assert.AreEqual(1, progress.FindAll(".progress-step.current").Count);

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

    private static WorkTaskResponse TaskResponse(WorkTaskId taskId, DateTimeOffset createdAt, DateTimeOffset updatedAt, WorkTaskStatus status = WorkTaskStatus.Running) => new(
        taskId.Value,
        Guid.Parse("22222222-2222-2222-2222-222222222222"),
        "prepare-report",
        Guid.NewGuid(),
        "Prepare report",
        null,
        status,
        createdAt,
        updatedAt,
        "run-1",
        [],
        [],
        [],
        null,
        null,
        new RespondAction(string.Empty),
        1);
}
