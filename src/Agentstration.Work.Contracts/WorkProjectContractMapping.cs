namespace Agentstration.Work.Contracts;

public static class WorkProjectContractMapping
{
    public static WorkProjectResponse ToProjectResponse(WorkProjectSnapshot project) => new(project.Id.Value, project.WorkspaceId.Value, project.Name, project.Namespace,
        project.Definition.DisplayName, project.Definition.Description, project.Definition.PlanIds.Select(id => id.Value).ToArray(), project.Definition.TaskIds.Select(id => id.Value).ToArray(),
        project.Version, project.CreatedAt, project.UpdatedAt) { Bindings = project.Definition.Bindings };
}
