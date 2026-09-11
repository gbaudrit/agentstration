using Agentstration.Work;
using Agentstration.Work.Contracts;

namespace Agentstration.Web.Components.WorkOperations;

public static class TaskPendingActionUi
{
    public static WorkplaceAction? ToAction(PendingActionContract action) => action.Kind switch
    {
        PendingActionKind.ConfirmationRequired or PendingActionKind.ApprovalRequired =>
            new RequestConfirmationAction(action.Title, action.Description, new(action.Id), string.Empty),
        PendingActionKind.ChoiceRequired when action.Fields.FirstOrDefault() is { } field =>
            new RequestChoiceAction(action.Title, action.Description, field.Options, new(action.Id), string.Empty, field.Name),
        PendingActionKind.InputRequired =>
            new RequestInputAction(action.Title, action.Description, action.Fields, new(action.Id), string.Empty),
        _ => null
    };
}
