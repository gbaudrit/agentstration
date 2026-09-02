using Agentstration.Application.Work;
using Agentstration.Flow;
using Agentstration.Flow.Application;
using Agentstration.Management.Abstractions;
using Agentstration.Resources;
using Agentstration.Work;

namespace Agentstration.Web.Hosting;

public static class WorkplaceDemoData
{
    public static async Task SeedAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var workspaceId = services.GetRequiredService<IWorkplaceContext>().WorkspaceId;
        var flows = services.GetRequiredService<FlowService>();
        var flowId = new FlowId("universal-router");
        var flow = await flows.GetAsync(workspaceId, flowId, cancellationToken);
        if (flow is null)
        {
            flow = await flows.CreateAsync(workspaceId, new CreateFlowCommand(
                flowId.Value,
                "Routes the Workplace primary Entry through the local managed-agent runtime.",
                "1.0.0",
                true,
                new DirectFlowDefinition(new FlowTargetReference(
                    FlowTargetKind.Agent,
                    "dotnet-expert"))), cancellationToken);
            await flows.PublishVersionAsync(workspaceId, flowId, "1.0.0", true, cancellationToken);
        }
        else if (flow.Value.ActiveVersion is null)
        {
            await flows.PublishVersionAsync(workspaceId, flowId, flow.Value.Version, true, cancellationToken);
        }

        var workplace = services.GetRequiredService<WorkplaceService>();
        var entries = services.GetRequiredService<EntryAdministrationService>();
        var entryId = new EntryId("universal-request");
        await SaveAndPublishAsync(entries, workplace, new EntryDraft
        {
            WorkspaceId = workspaceId,
            Id = entryId,
            Name = "universal-request",
            DisplayName = "Ask Agentstration",
            Description = "Describe an outcome. Agentstration orchestrates the work.",
            Presentation = new EntryPresentation
            {
                Kind = EntryPresentationKind.Prompt,
                Placeholder = "What would you like to accomplish?",
                Icon = "sparkle",
                Fields =
                [
                    new EntryFieldDefinition
                    {
                        Name = "request", Label = "Request", Type = EntryFieldType.Prompt, Required = true,
                        Placeholder = "Describe what you need", Validation = new EntryFieldValidation(3, 10_000), Role = EntryFieldRole.PrimaryInput
                    }
                ]
            },
            Binding = new EntryBinding(EntryBindingKind.Agent, "dotnet-expert"),
            Behavior = new EntryBehavior(TaskCreationMode.Automatic, AllowConversation: true, StreamResponse: true)
        }, cancellationToken);

        var reportEntryId = new EntryId("prepare-report");
        await SaveAndPublishAsync(entries, workplace, new EntryDraft
        {
            WorkspaceId = workspaceId,
            Id = reportEntryId,
            Name = "prepare-report",
            DisplayName = "Prepare a report",
            Description = "Starts with a standard version, then lets you request further versions in the same conversation.",
            Presentation = new EntryPresentation
            {
                Kind = EntryPresentationKind.Prompt,
                Placeholder = "What should the report cover?",
                Progress = new(EntryProgressVisibility.Detailed),
                Task = new(EntryTaskDisplay.Visible),
                Suggestions =
                [
                    new("Monthly report", "Prepare a monthly report summarizing progress, risks, and next steps."),
                    new("Project brief", "Prepare a concise project brief for the next steering committee."),
                    new("Decision memo", "Prepare a decision memo comparing the available options.")
                ],
                Fields = [new EntryFieldDefinition { Name = "request", Label = "Report request", Type = EntryFieldType.Prompt, Required = true, Validation = new EntryFieldValidation(3, 10_000), Role = EntryFieldRole.PrimaryInput }]
            },
            Binding = new EntryBinding(EntryBindingKind.Flow, "universal-router"),
            Behavior = new EntryBehavior(TaskCreationMode.Automatic, true, true, new EntryConversationBehavior())
        }, cancellationToken);
        var guidedEntryId = new EntryId("guided-request");
        await SaveAndPublishAsync(entries, workplace, new EntryDraft
        {
            WorkspaceId = workspaceId,
            Id = guidedEntryId,
            Name = "guided-request",
            DisplayName = "Guided request",
            Description = "Demonstrates a one-click clarification inside the conversation.",
            Presentation = new EntryPresentation
            {
                Kind = EntryPresentationKind.Prompt,
                Placeholder = "What should I prepare?",
                Suggestions = [new("Draft a summary", "Draft a summary of the latest project update.")],
                Fields = [new EntryFieldDefinition { Name = "request", Type = EntryFieldType.Prompt, Required = true, Role = EntryFieldRole.PrimaryInput }]
            },
            Binding = new EntryBinding(EntryBindingKind.Flow, "universal-router"),
            Behavior = new EntryBehavior(TaskCreationMode.Automatic, true, true, new EntryConversationBehavior())
        }, cancellationToken);
        var immediateEntryId = new EntryId("quick-answer");
        await SaveAndPublishAsync(entries, workplace, new EntryDraft
        {
            WorkspaceId = workspaceId,
            Id = immediateEntryId,
            Name = "quick-answer",
            DisplayName = "Quick acknowledgement",
            Description = "Demonstrates an Interaction that completes without a Task.",
            Presentation = new EntryPresentation
            {
                Kind = EntryPresentationKind.Prompt,
                Placeholder = "Leave a short note",
                Suggestions = [new("Try a quick answer", "Acknowledge that the Workplace UX iteration is ready for review."), new("Save an idea", "Remember this idea for my next request.")],
                Fields = [new EntryFieldDefinition { Name = "request", Type = EntryFieldType.Prompt, Required = true, Role = EntryFieldRole.PrimaryInput }]
            },
            Binding = new EntryBinding(EntryBindingKind.Agent, "dotnet-expert"),
            Behavior = new EntryBehavior(TaskCreationMode.Never)
        }, cancellationToken);

        var dashboardAdministration = services.GetRequiredService<DashboardAdministrationService>();
        var home = new WorkplaceDashboardDraft
        {
            Id = new DashboardId("home"),
            WorkspaceId = workspaceId,
            Name = "home",
            DisplayName = "Home",
            Icon = DashboardIconDefaults.Home,
            Description = "Your default Workplace dashboard.",
            IsDefault = true,
            Entries =
            [
                new DashboardEntryReference { EntryResourceId = reportEntryId, Role = DashboardItemRole.Primary, Order = 0 },
                new DashboardEntryReference { EntryResourceId = entryId, Role = DashboardItemRole.Featured, Order = 10 },
                new DashboardEntryReference { EntryResourceId = guidedEntryId, Role = DashboardItemRole.Standard, Order = 20 },
                new DashboardEntryReference { EntryResourceId = immediateEntryId, Role = DashboardItemRole.Standard, Order = 30 }
            ]
        };
        var existingHome = (await dashboardAdministration.ListAsync(workspaceId, cancellationToken)).SingleOrDefault(value => value.Id == home.Id);
        if (existingHome is null || existingHome.Entries.Count == 0)
        {
            var savedHome = await dashboardAdministration.SaveAsync(home, cancellationToken);
            await dashboardAdministration.PublishAsync(workspaceId, savedHome.Id, cancellationToken);
        }
    }

    private static async Task SaveAndPublishAsync(EntryAdministrationService service, WorkplaceService workplace, EntryDraft draft, CancellationToken cancellationToken)
    {
        var existing = await service.ListAsync(draft.WorkspaceId, cancellationToken);
        var current = existing.SingleOrDefault(value => value.Id == draft.Id);
        if (current is null || current.Binding != draft.Binding) await service.SaveAsync(draft, cancellationToken);
        var published = (await workplace.ListEntriesAsync(draft.WorkspaceId, cancellationToken)).SingleOrDefault(value => value.Id == draft.Id);
        if (published is null || published.ResolvedTarget.FlowResourceId.Contains('/', StringComparison.Ordinal))
            await service.PublishAsync(draft.WorkspaceId, draft.Id, cancellationToken);
    }
}
