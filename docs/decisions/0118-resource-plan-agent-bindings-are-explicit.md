# ADR-0118: Resource Plan Agent bindings are explicit

## Status

Accepted

## Context

A functional Resource Plan describes roles without choosing concrete Model or Runtime Profiles. Materialization previously assigned fixed profile names to every generated Agent. Those names could be missing, unsuitable, or ambiguous across ownership scopes, and a reviewer could not choose alternatives.

## Decision

Materialization accepts a Model Profile and Runtime Profile reference for every planned role. REST, the Console, and governed planning Tools pass the same binding request to the Resource Planning service. A missing or unresolvable binding produces an error diagnostic and prevents ChangeSet creation. Resolution uses the canonical visibility rules of the plan's Workspace; the Agent proposal records the resolved exact ownership scope.

The materialization digest includes the selected references and the current identity, generation, ETag, and digest of both profiles. ChangeSet creation recomputes materialization and can require the reviewed digest, so a changed profile or selection cannot silently replace the preview. Canonical Agent validation checks the references again when the ChangeSet is validated. Binding choices do not mutate the functional plan or the selected profiles.

## Consequences

- A Resource Plan remains functional and portable until a reviewer selects concrete profiles.
- Materialization can preview an incomplete plan, but cannot create a ChangeSet from it.
- Profile changes require a fresh preview and validation before any later application step.
- Profile catalogs shown by the Console are filtered to the plan's visible scopes; the server independently enforces visibility.
