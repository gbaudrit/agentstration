# ADR-0148: Flow termination uses named outputs

## Status

Accepted

## Context

The typed Flow graph originally represented successful and failed termination with separate `Output` and `Failure` step types. The executor converted those types to the fixed events `completed` and `failed`, so a caller could not distinguish several business-success or business-error results. A single graph-level output schema also could not describe those results independently.

Flow composition and the visual Designer need a stable, provider-neutral contract for each terminal result. That contract must preserve payload mapping, expose an effective schema, and let a parent Flow route on the exact result selected by a child without coupling Flow Core to Runtime or UI types.

## Decision

New graph definitions terminate through named `Output` steps. The step `name` is the stable public event identity and each output declares an `outcome` of `success` or `error`. It may also declare a display name, payload mapping, JSON schema, and, for error outcomes, a code, message, and optional details expression. Names such as `completed` and `error` are authoring defaults, not reserved protocol values.

A completed Flow Run records both the output name and outcome. A `Flow` call uses the child's output name as its transition event, including named error outputs, while the outcome still determines the child's terminal run status. Published Flow resolution exposes all named outputs and their effective schemas to composition consumers.

An output-level schema takes precedence over the legacy graph-level output schema. When neither is present, validation may infer a schema only from compatible, structurally known incoming payloads; ambiguous inference requires an explicit output schema.

Persisted definitions remain readable. An `Output` without `outcome` uses the historical successful `completed` event, and the historical `Failure` discriminator remains a deserialization adapter using the failed-run semantics. New templates and authoring surfaces emit named `Output` steps for both outcomes. Existing immutable published versions are not rewritten.

## Consequences

- A Flow can publish several success and error results without adding terminal step kinds.
- Parent Flows can route deterministically on a child's public output name and inspect each declared schema.
- Run history preserves which business result terminated an execution independently of success or failure status.
- The graph-level `outputSchema` and `failure` discriminator remain compatibility surfaces until a later migration removes them.
- Designer-specific ports, gestures, and visual styling remain separate UI concerns.
