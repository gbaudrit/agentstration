using Agentstration.ResourcePlanning.Contracts;

namespace Agentstration.ResourcePlanning.Tests;

[TestClass]
public sealed class FunctionalResourcePlanValidatorTests
{
    private readonly FunctionalResourcePlanValidator validator = new();

    [TestMethod]
    public void ValidatesFunctionalRelationshipsWithoutResourceContracts()
    {
        var content = FunctionalResourcePlanSerializer.Serialize(new()
        {
            Solution = new("Coordinate support", ["Resolve requests"]),
            Roles = [new("triage", "Triage", "Classify requests", ["Classify"], ["Text analysis"])],
            Workflows = [new("support", "Support", "Resolve a request", ["triage"], PlanningCollaborationStyle.Ordered)],
            Experiences = [new("support-chat", "Support chat", "Help a user", "support", ["employees"])],
            Runtime = new(LocalOnly: true, CloudAllowed: false)
        });
        var result = validator.Validate(content);
        Assert.IsTrue(result.IsValid, string.Join(Environment.NewLine, result.Issues.Select(value => value.Message)));
    }

    [TestMethod]
    public void ReportsStableCodesForDuplicatesUnresolvedReferencesAndContradictions()
    {
        var content = FunctionalResourcePlanSerializer.Serialize(new()
        {
            Solution = new("Coordinate support", ["Resolve requests"]),
            Roles = [new("worker", "Worker", "Work", ["Work"], ["Text"]), new("worker", "Other", "Work", ["Work"], ["Text"])],
            Workflows = [new("support", "Support", "Resolve", ["missing"], PlanningCollaborationStyle.Ordered)],
            Experiences = [new("chat", "Chat", "Help", "missing-flow", ["users"])],
            Runtime = new(LocalOnly: true, CloudAllowed: true)
        });
        var result = validator.Validate(content);
        Assert.IsFalse(result.IsValid);
        CollectionAssert.IsSubsetOf(
            new[] { "planning_logical_id_duplicate", "planning_reference_unresolved", "planning_runtime_contradictory" },
            result.Issues.Select(value => value.Code).Distinct().ToArray());
    }

    [TestMethod]
    public void RejectsUnknownContractVersionBeforeDeserialization()
    {
        var content = FunctionalResourcePlanSerializer.Serialize(new() { Solution = new("Goal", ["Outcome"]) }) with { SchemaVersion = "resource-planning.agentstration.io/v2" };
        var issue = validator.Validate(content).Issues.Single();
        Assert.AreEqual("planning_contract_unsupported", issue.Code);
    }
}
