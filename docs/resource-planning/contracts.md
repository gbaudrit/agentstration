# Functional Resource Planning contracts

Resource Planning agents exchange functional intent through `resource-planning.agentstration.io/v1`. The contract describes solution outcomes, functional roles, collaboration intent, integrations, experiences, explicit retirements, runtime constraints, and logical dependencies. Logical IDs exist only inside one plan.

The contract deliberately excludes `apiVersion`/`kind` Resource envelopes, ETags, persistence DTOs, concrete Flow graphs, provider bindings, Resource IDs, and internal scope IDs. Those details belong to deterministic platform materializers.

An optional `retirements` entry names an existing role, workflow, or experience by its stable logical ID and gives a reason. It requests deletion only after the corresponding exact Workspace Resource has been found, reviewed, verified, and authorized. Logical dependency edges between retired elements are reversed for deletion, so dependents are removed before their prerequisites. A missing target blocks ChangeSet creation. Agent deletion uses the exact Workspace scope, including for deployment cleanup.

## Materialization bindings and validation

The operator selects a ModelProfile and RuntimeProfile for each proposed Agent before materialization. These Workspace-scoped selections are saved against the current plan revision. Materialization records the exact references and profile evidence in the ChangeSet; it does not create the profiles.

ChangeSet validation resolves those references in the target Workspace. If a ModelProfile is missing or invalid, validation records a `resource_change_model_profile_invalid` issue against each affected Agent and marks the ChangeSet `Blocked`. Create or repair the referenced profile, then revalidate. Application rechecks the recorded profile evidence and never silently substitutes another available profile.

Each integration requires an operator-selected, visible, enabled, available Tool and at least one dependency edge from a planned role to that integration. Materialization adds the selected Tool reference to each dependent Agent, records the exact Tool evidence in the ChangeSet, and blocks ChangeSet creation if the binding is missing or unavailable. Application rechecks that evidence. The materialization digest also includes current target Resource evidence, so a concurrent target change produces a new review identity.

## Evolution

- Additive optional properties may be introduced within `v1`; readers ignore unknown JSON properties.
- Existing property meanings and enum values remain stable within `v1`.
- Removing a property, making an optional property required, or changing semantics requires a new contract version.
- The platform rejects unknown major contract versions with `planning_contract_unsupported` rather than guessing.
- Validation uses stable codes and JSON-style paths so agents and user interfaces do not parse messages.

## Example

```json
{
  "schemaVersion": "resource-planning.agentstration.io/v1",
  "document": {
    "solution": { "summary": "Coordinate support", "outcomes": ["Resolve requests"] },
    "roles": [
      {
        "logicalId": "triage",
        "displayName": "Triage",
        "purpose": "Classify requests",
        "responsibilities": ["Identify the request category"],
        "capabilities": ["Text analysis"]
      }
    ],
    "workflows": [
      {
        "logicalId": "support",
        "displayName": "Support",
        "objective": "Resolve a request",
        "participants": ["triage"],
        "collaboration": "ordered"
      }
    ]
  }
}
```
