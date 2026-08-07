using Agentstration.Application.Work;
using Agentstration.Flow;
using Agentstration.Flow.Application;
using Agentstration.Work;

namespace Agentstration.Web.Hosting;

public static class WorkplaceDemoData
{
    public static async Task SeedAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var flows = services.GetRequiredService<FlowService>();
        var flowId = new FlowId("universal-router");
        var flow = await flows.GetAsync(flowId, cancellationToken);
        if (flow is null)
        {
            flow = await flows.CreateAsync(new CreateFlowCommand(
                flowId.Value,
                "Routes the Workplace primary Entry through the local managed-agent runtime.",
                FlowKind.Direct,
                "1.0.0",
                true,
                new DirectFlowSpec(new FlowTargetReference(
                    FlowTargetKind.Agent,
                    "/resourceGroups/default/providers/Agentstration.Agents/agents/dotnet-expert"))), cancellationToken);
            await flows.PublishVersionAsync(flowId, "1.0.0", true, cancellationToken);
        }
        else if (flow.Value.ActiveVersion is null)
        {
            await flows.PublishVersionAsync(flowId, flow.Value.Version, true, cancellationToken);
        }

        var workplace = services.GetRequiredService<WorkplaceService>();
        var entries = services.GetRequiredService<EntryAdministrationService>();
        var entryId = new EntryId("/resourceGroups/default/providers/Agentstration.Work/entries/universal-request");
        await SaveAndPublishAsync(entries, workplace, new EntryDraft
        {
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
            Binding = new EntryBinding(EntryBindingKind.Agent, "/resourceGroups/default/providers/Agentstration.Agents/agents/dotnet-expert"),
            Behavior = new EntryBehavior(TaskCreationMode.Automatic, AllowConversation: true, StreamResponse: true)
        }, cancellationToken);

        var reportEntryId = new EntryId("/resourceGroups/default/providers/Agentstration.Work/entries/prepare-report");
        await SaveAndPublishAsync(entries, workplace, new EntryDraft
        {
            Id = reportEntryId, Name = "prepare-report", DisplayName = "Prepare a report",
            Description = "Starts with a standard version, then lets you request further versions in the same conversation.",
            Presentation = new EntryPresentation
            {
                Kind = EntryPresentationKind.Prompt,
                Placeholder = "What should the report cover?",
                Suggestions =
                [
                    new("Monthly report", "Prepare a monthly report summarizing progress, risks, and next steps."),
                    new("Project brief", "Prepare a concise project brief for the next steering committee."),
                    new("Decision memo", "Prepare a decision memo comparing the available options.")
                ],
                Fields = [new EntryFieldDefinition { Name = "request", Label = "Report request", Type = EntryFieldType.Prompt, Required = true, Validation = new EntryFieldValidation(3, 10_000), Role = EntryFieldRole.PrimaryInput }]
            },
            Binding = new EntryBinding(EntryBindingKind.Flow, "/resourceGroups/default/providers/Agentstration.Flows/flows/universal-router"),
            Behavior = new EntryBehavior(TaskCreationMode.Automatic, true, true, new EntryConversationBehavior())
        }, cancellationToken);
        var guidedEntryId = new EntryId("/resourceGroups/default/providers/Agentstration.Work/entries/guided-request");
        await SaveAndPublishAsync(entries, workplace, new EntryDraft
        {
            Id = guidedEntryId, Name = "guided-request", DisplayName = "Guided request", Description = "Demonstrates a one-click clarification inside the conversation.",
            Presentation = new EntryPresentation
            {
                Kind = EntryPresentationKind.Prompt,
                Placeholder = "What should I prepare?",
                Suggestions = [new("Draft a summary", "Draft a summary of the latest project update.")],
                Fields = [new EntryFieldDefinition { Name = "request", Type = EntryFieldType.Prompt, Required = true, Role = EntryFieldRole.PrimaryInput }]
            },
            Binding = new EntryBinding(EntryBindingKind.Flow, "/resourceGroups/default/providers/Agentstration.Flows/flows/universal-router"),
            Behavior = new EntryBehavior(TaskCreationMode.Automatic, true, true, new EntryConversationBehavior())
        }, cancellationToken);
        var immediateEntryId = new EntryId("/resourceGroups/default/providers/Agentstration.Work/entries/quick-answer");
        await SaveAndPublishAsync(entries, workplace, new EntryDraft
        {
            Id = immediateEntryId, Name = "quick-answer", DisplayName = "Quick acknowledgement", Description = "Demonstrates an Interaction that completes without a Task.",
            Presentation = new EntryPresentation
            {
                Kind = EntryPresentationKind.Prompt,
                Placeholder = "Leave a short note",
                Suggestions = [new("Try a quick answer", "Acknowledge that the Workplace UX iteration is ready for review."), new("Save an idea", "Remember this idea for my next request.")],
                Fields = [new EntryFieldDefinition { Name = "request", Type = EntryFieldType.Prompt, Required = true, Role = EntryFieldRole.PrimaryInput }]
            },
            Binding = new EntryBinding(EntryBindingKind.Agent, "/resourceGroups/default/providers/Agentstration.Agents/agents/dotnet-expert"), Behavior = new EntryBehavior(TaskCreationMode.Never)
        }, cancellationToken);

        var workspaceAdministration = services.GetRequiredService<WorkspaceAdministrationService>();
        var workspaceDraft = new WorkplaceWorkspaceDraft
        {
            Id = new WorkplaceWorkspaceId("/resourceGroups/default/providers/Agentstration.Work/workspaces/personal"),
            Name = "personal",
            DisplayName = "Personal workspace",
            Description = "Your local place to delegate and follow work.",
            Entries =
            [
                new WorkspaceEntryReference { EntryResourceId = reportEntryId, Role = WorkspaceEntryRole.Primary, Order = 0 },
                new WorkspaceEntryReference { EntryResourceId = entryId, Role = WorkspaceEntryRole.Standard, Order = 10 },
                new WorkspaceEntryReference { EntryResourceId = guidedEntryId, Role = WorkspaceEntryRole.Standard, Order = 20 },
                new WorkspaceEntryReference { EntryResourceId = immediateEntryId, Role = WorkspaceEntryRole.Standard, Order = 30 }
            ]
        };
        if (!(await workspaceAdministration.ListAsync(cancellationToken)).Any(value => value.Id == workspaceDraft.Id))
            await workspaceAdministration.SaveAsync(workspaceDraft, cancellationToken);
        if (!(await workplace.ListWorkspacesAsync(cancellationToken)).Any(value => value.Id == workspaceDraft.Id))
            await workspaceAdministration.PublishAsync(workspaceDraft.Id, cancellationToken);
    }

    private static async Task SaveAndPublishAsync(EntryAdministrationService service, WorkplaceService workplace, EntryDraft draft, CancellationToken cancellationToken)
    {
        var existing = await service.ListAsync(cancellationToken);
        if (!existing.Any(value => value.Id == draft.Id)) await service.SaveAsync(draft, cancellationToken);
        if (!(await workplace.ListEntriesAsync(cancellationToken)).Any(value => value.Id == draft.Id && value.ResolvedTarget is not null)) await service.PublishAsync(draft.Id, cancellationToken);
    }
}
