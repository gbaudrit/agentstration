# ADR-0159: Flow expressions use authoritative execution context

## Status

Accepted

## Context

Flow mappings can compose input, transition output, and prior step output, but some governed operations also require the identity of the execution producing them. In particular, a producer that calls an `artifact.storage.write/v1` Flow must supply the real producer FlowRun identity so the resulting `FlowRunArtifact` can retain verifiable lineage. Accepting that identity as ordinary caller input would let a caller assert provenance, while deriving it from naming conventions would couple authored Flows to internal identifier formats.

## Decision

Expose a reserved, read-only `execution` namespace to Flow expressions. It contains `flowRunId`, `rootFlowRunId`, `parentFlowRunId`, `stepName`, and `correlationId`. Values are constructed from the persisted FlowRun and the currently executing step. A root Run reports its own identity as `rootFlowRunId`; absent parent or correlation values resolve to JSON `null`.

The namespace is available wherever the existing expression evaluator is used, including mappings, conditions, child-Flow and repeat inputs, transitions, and outputs. It is not part of the Flow input schema, cannot be supplied or overridden by a caller, and exposes no credentials, Claims, headers, or mutable host state. Unknown execution properties are rejected during expression parsing.

## Consequences

- Producer Flows can pass `${execution.flowRunId}` explicitly to a Storage Flow without weakening artifact lineage validation.
- Nested and repeated child Runs can distinguish their current, root, and parent identities.
- Resume and retry reuse persisted Run identities and correlation, so expression results remain deterministic for a given Run and step.
- Published Flow definitions opt into the context only by referencing the reserved namespace; existing expressions retain their behavior.
- Adding another execution property requires an explicit contract change rather than exposing arbitrary runtime state.
