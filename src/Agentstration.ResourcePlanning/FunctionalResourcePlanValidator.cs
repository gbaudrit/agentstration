using System.Text.Json;
using Agentstration.ResourcePlanning.Contracts;

namespace Agentstration.ResourcePlanning;

public interface IResourcePlanContentValidator
{
    PlanningValidationResult Validate(ResourcePlanContent content);
}

public sealed class FunctionalResourcePlanValidator : IResourcePlanContentValidator
{
    public PlanningValidationResult Validate(ResourcePlanContent content)
    {
        var issues = new List<PlanningValidationIssue>();
        if (!ResourcePlanningContractVersions.Supported.Contains(content.SchemaVersion))
        {
            issues.Add(Error("planning_contract_unsupported", "schemaVersion", $"Planning contract '{content.SchemaVersion}' is not supported."));
            return new(issues);
        }

        FunctionalResourcePlanV1 plan;
        try { plan = FunctionalResourcePlanSerializer.Deserialize(content); }
        catch (JsonException exception)
        {
            issues.Add(Error("planning_document_invalid", "$", exception.Message));
            return new(issues);
        }

        Required(plan.Solution?.Summary, "solution.summary", 4000, issues);
        RequireValues(plan.Solution?.Outcomes, "solution.outcomes", issues);
        var identities = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (role, index) in (plan.Roles ?? []).Select((value, index) => (value, index)))
        {
            var path = $"roles[{index}]";
            Identity(role.LogicalId, $"{path}.logicalId", identities, issues);
            Required(role.DisplayName, $"{path}.displayName", 160, issues);
            Required(role.Purpose, $"{path}.purpose", 2000, issues);
            RequireValues(role.Responsibilities, $"{path}.responsibilities", issues);
            RequireValues(role.Capabilities, $"{path}.capabilities", issues);
        }
        foreach (var (workflow, index) in (plan.Workflows ?? []).Select((value, index) => (value, index)))
        {
            var path = $"workflows[{index}]";
            Identity(workflow.LogicalId, $"{path}.logicalId", identities, issues);
            Required(workflow.DisplayName, $"{path}.displayName", 160, issues);
            Required(workflow.Objective, $"{path}.objective", 2000, issues);
            RequireValues(workflow.Participants, $"{path}.participants", issues);
        }
        foreach (var (integration, index) in (plan.Integrations ?? []).Select((value, index) => (value, index)))
        {
            var path = $"integrations[{index}]";
            Identity(integration.LogicalId, $"{path}.logicalId", identities, issues);
            Required(integration.DisplayName, $"{path}.displayName", 160, issues);
            Required(integration.Purpose, $"{path}.purpose", 2000, issues);
            RequireValues(integration.RequiredCapabilities, $"{path}.requiredCapabilities", issues);
        }
        foreach (var (experience, index) in (plan.Experiences ?? []).Select((value, index) => (value, index)))
        {
            var path = $"experiences[{index}]";
            Identity(experience.LogicalId, $"{path}.logicalId", identities, issues);
            Required(experience.DisplayName, $"{path}.displayName", 160, issues);
            Required(experience.Purpose, $"{path}.purpose", 2000, issues);
            RequireValues(experience.Audiences, $"{path}.audiences", issues);
        }
        ValidateReferences(plan, identities, issues);
        if (plan.Runtime is null)
            issues.Add(Error("planning_runtime_required", "runtime", "Runtime constraints are required."));
        else if (plan.Runtime.LocalOnly && plan.Runtime.CloudAllowed)
            issues.Add(Error("planning_runtime_contradictory", "runtime", "A local-only plan cannot also allow cloud execution."));
        return new(issues);
    }

    private static void ValidateReferences(FunctionalResourcePlanV1 plan, IReadOnlyDictionary<string, string> identities, List<PlanningValidationIssue> issues)
    {
        var roles = (plan.Roles ?? []).Select(value => value.LogicalId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var workflows = (plan.Workflows ?? []).Select(value => value.LogicalId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (workflow, index) in (plan.Workflows ?? []).Select((value, index) => (value, index)))
            foreach (var participant in workflow.Participants.Where(value => !roles.Contains(value)))
                issues.Add(Error("planning_reference_unresolved", $"workflows[{index}].participants", $"Role '{participant}' is not defined."));
        foreach (var (experience, index) in (plan.Experiences ?? []).Select((value, index) => (value, index)))
            if (!workflows.Contains(experience.Workflow))
                issues.Add(Error("planning_reference_unresolved", $"experiences[{index}].workflow", $"Workflow '{experience.Workflow}' is not defined."));
        foreach (var (dependency, index) in (plan.Dependencies ?? []).Select((value, index) => (value, index)))
        {
            if (!identities.ContainsKey(dependency.From)) issues.Add(Error("planning_reference_unresolved", $"dependencies[{index}].from", $"Element '{dependency.From}' is not defined."));
            if (!identities.ContainsKey(dependency.To)) issues.Add(Error("planning_reference_unresolved", $"dependencies[{index}].to", $"Element '{dependency.To}' is not defined."));
            Required(dependency.Relationship, $"dependencies[{index}].relationship", 160, issues);
        }
    }

    private static void Identity(string value, string path, IDictionary<string, string> identities, List<PlanningValidationIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 80 || value.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_')))
        {
            issues.Add(Error("planning_logical_id_invalid", path, "Logical IDs must contain 1-80 letters, digits, hyphens, or underscores."));
            return;
        }
        if (!identities.TryAdd(value, path)) issues.Add(Error("planning_logical_id_duplicate", path, $"Logical ID '{value}' is already used by '{identities[value]}'."));
    }

    private static void Required(string? value, string path, int maximum, List<PlanningValidationIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum) issues.Add(Error("planning_value_required", path, $"The value must contain 1-{maximum} characters."));
    }

    private static void RequireValues(IReadOnlyList<string>? values, string path, List<PlanningValidationIssue> issues)
    {
        if (values is null || values.Count == 0 || values.Any(string.IsNullOrWhiteSpace)) issues.Add(Error("planning_collection_required", path, "At least one non-empty value is required."));
    }

    private static PlanningValidationIssue Error(string code, string path, string message) => new(code, path, message);
}
