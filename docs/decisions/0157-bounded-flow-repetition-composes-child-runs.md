# ADR-0157 — Bounded Flow repetition composes durable child runs

## Status

Accepted

## Context

Some provider-neutral workflows must process an unknown number of bounded units. Knowledge acquisition is the first concrete case: an acquisition Tool can return an opaque content reference, while a Flow must repeatedly read bounded chunks and write them through governed StagedArtifact Tools. The graph executor intentionally rejects cycles, and copying complete content into one Flow transition would violate the bounded payload and staging decisions in ADR-0150 and ADR-0156.

Allowing arbitrary graph cycles would make progress recovery, retry identity, cancellation, and resource bounds implicit. Embedding provider-specific pagination in the Flow runtime would instead couple the engine to Crawl4AI or another acquisition technology.

## Decision

Add a provider-neutral `repeat` graph step. A repeat step selects an ordinary published child Flow by active or exact version, maps the first child input, maps each successful child output into the next child input, evaluates an explicit `until` expression, and declares `maximumIterations`. The limit must be between 1 and 1000; the existing root descendant limit remains an independent runtime bound.

This decision extends ADR-0105 and supersedes only its statement that the graph has exactly two generic composition primitives. Repeat remains generic Flow composition rather than a provider or resource-specific card.

Every iteration is a deterministic durable child FlowRun. The parent persists the current iteration, current mapped input, current child identity, and the ordered child identities before releasing its worker in `WaitingForChild`. A terminal child resumes the parent through the existing child-run path. Replay reuses the deterministic child identity, startup recovery recreates a missing current child from the persisted input, cancellation still walks the descendant tree, and causal diagnostics include every iteration.

The repeat step exposes the last successful child output as its step output. Earlier outputs remain available through their child FlowRuns rather than being accumulated into parent state. A false condition at the declared limit fails with `flow_repeat_limit_exceeded`; a non-boolean condition result fails with `flow_repeat_until_invalid`. Child failure, timeout, and cancellation retain the ordinary Flow-call transition semantics.

Graph transitions remain acyclic. Repetition is deliberately restricted to Flow composition, so each unit can contain governed Tool calls, Agents, transformations, staging writes, or Storage Flow calls without adding provider-specific engine behavior.

## Consequences

- Chunk transfer and other pagination workflows gain explicit, recoverable progress without arbitrary graph cycles.
- Each iteration consumes one descendant FlowRun and is subject to the existing nesting, descendant, authorization, and immutable-version rules.
- Flow authors must choose a finite iteration limit and keep iteration inputs and outputs bounded.
- The initial increment is authorable through JSON/YAML and rendered read-only by the topology; dedicated visual editing can be added without changing runtime semantics.
- External Tool effects retain their existing retry and at-least-once constraints; the child Flow must use idempotent governed Tools where replay can repeat an effect.
