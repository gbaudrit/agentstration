# Functional Resource Planning composition

This sample defines six functional specialist Agents, six durable child Flows, and the `resource-planning` parent Flow. The parent carries one correlation chain through requirement analysis, capability design, workflow design, integration design, experience design, consolidation, and a non-mutating materialization preview.

The consolidation branch stops with clarification diagnostics when intent is incomplete or contradictory. A ready plan is created and progressed only through the governed `resource-planning.*` internal Tools. The preview reports missing profile and integration Tool bindings; a reviewer selects them before creating and validating a ChangeSet. The Flow never applies a ChangeSet.

The files are intentionally unpackaged source assets. The official installable Pack is delivered separately by FR #333. Before importing this sample, bind `conversational-model` and `local-runtime`, then import the Agents and child Flows before the parent Flow.

`fixtures/customer-support-plan.json` is a deterministic representative functional-contract fixture used by automated tests.
