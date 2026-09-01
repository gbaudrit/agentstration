using Agentstration.Work;
using Microsoft.Extensions.Localization;

namespace Agentstration.Workplace.Components;

internal static class WorkplaceActivityTitles
{
    public static string Get(IStringLocalizer<WorkplaceLayoutStrings> localizer, WorkTaskActivity activity) =>
        (activity.Type, activity.Title) switch
        {
            (WorkTaskActivityType.TaskCreated, "Task created") => localizer["ActivityTaskCreated"],
            (WorkTaskActivityType.TaskStarted, "Work started") => localizer["ActivityWorkStarted"],
            (WorkTaskActivityType.ProgressStarted, "Preparing a response") => localizer["PreparingResponse"],
            (WorkTaskActivityType.ProgressCompleted, "Response prepared") => localizer["ResponsePrepared"],
            (WorkTaskActivityType.TaskPaused, "Task paused") => localizer["ActivityTaskPaused"],
            (WorkTaskActivityType.TaskResumed, "Task resumed") => localizer["ActivityTaskResumed"],
            (WorkTaskActivityType.TaskCancelled, "Task cancelled") => localizer["ActivityTaskCancelled"],
            (WorkTaskActivityType.ActionRequired, "Action required") => localizer["ActionRequired"],
            (WorkTaskActivityType.TaskCompleted, "Task completed") => localizer["ActivityTaskCompleted"],
            (WorkTaskActivityType.TaskCompleted, "New version generated") => localizer["ActivityNewVersionGenerated"],
            (WorkTaskActivityType.TaskFailed, "Task failed") => localizer["ActivityTaskFailed"],
            _ => activity.Title
        };
}
