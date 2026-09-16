# Functional Resource Planning contracts

Resource Planning agents exchange functional intent through `resource-planning.agentstration.io/v1`. The contract describes solution outcomes, functional roles, collaboration intent, integrations, experiences, runtime constraints, and logical dependencies. Logical IDs exist only inside one plan.

The contract deliberately excludes `apiVersion`/`kind` Resource envelopes, ETags, persistence DTOs, concrete Flow graphs, provider bindings, Resource IDs, and internal scope IDs. Those details belong to deterministic platform materializers.

## Materialization defaults and validation

The current materializer assigns every proposed Agent the ModelProfile reference `default/default` and RuntimeProfile reference `default/maf-builtin`. These are deterministic materialization options, not values selected by an agent or discovered from the Workspace. Materialization does not create either resource or require them to exist, so a fresh local instance can still produce a reviewable ChangeSet.

ChangeSet validation resolves those references in the target Workspace. If the ModelProfile is missing or invalid, validation records a `resource_change_model_profile_invalid` issue against each affected Agent and marks the ChangeSet `Blocked`. Create or repair the referenced ModelProfile, then revalidate. Validation never silently substitutes another available profile.

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
