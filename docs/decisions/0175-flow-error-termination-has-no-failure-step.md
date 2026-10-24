# ADR-0175: Flow error termination has no Failure step

## Status

Accepted

## Context

ADR-0148 introduced named `Output` steps whose `outcome` distinguishes successful and failed Flow termination. Its initial compatibility plan retained the former `failure` discriminator for persisted definitions. That compatibility surface was implemented only on unreleased development branches and created two competing representations for the same terminal behavior.

Keeping both representations would require Flow authoring, validation, persistence, visualization, and execution to continue carrying special handling for a step kind that is no longer part of the product model. It would also obscure the rule that transition events come from output names while terminal Run status comes from output outcomes.

## Decision

Remove the `Failure` step type and its `failure` discriminator from the Flow model, serializers, authoring surfaces, runtime, and fixtures. Every terminal result is represented by a named `Output` step. Failed termination uses `outcome: error` and may declare a stable code, message, details expression, schema, and payload mapping.

Automatic error transitions remain outcome-based. They target an available output whose outcome is `error`; they never infer error behavior from an output named `failure`.

The compatibility clauses in ADR-0148 for the historical `Failure` shape and `failure` discriminator are superseded. Because that representation was never released, no persisted-data migration or deserialization adapter is provided. Development environments created from the intermediate branches must be reseeded before running the updated model.

An `Output` that omits `outcome` retains its historical successful behavior because that shape predates named outcomes in released definitions.

## Consequences

- Flow termination has one model and one discriminator for both success and error outcomes.
- Output names remain business event identities; output outcomes alone determine terminal Run status and automatic error routing.
- The Designer, runtime, validators, manifests, and tests no longer carry an unreleased compatibility branch.
- Local data produced by intermediate development branches containing `type: failure` is intentionally unreadable and must be reseeded.

