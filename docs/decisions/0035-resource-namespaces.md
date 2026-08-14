# ADR-0035: Resource names are scoped by explicit namespaces

Status: Accepted — 2026-08-14

## Context

A workspace can host independent sets of Agents, Flows, Entries, model resources, and runtime references that legitimately reuse the same short name. Encoding ownership into the name would make references brittle and recreate hierarchical identifiers inside strings.

## Decision

Resource identity is `(workspace, namespace, kind, name)`. `default` is the implicit namespace for existing resources, routes, and serialized references.

`Agentstration.Resources` owns the provider-neutral `ResourceNamespace` and `ResourceAddress` value types. Management metadata, Flow and Workplace identifiers, and Runtime agent references carry this value without creating dependencies between their bounded contexts.

A reference may omit its namespace. An omitted namespace resolves relative to the namespace of the resource containing the reference. An explicitly supplied namespace is preserved. Public namespace-specific routes use `/api/namespaces/{namespace}/...`; existing routes remain compatibility aliases for `default`.

SQLite logical keys and unique indexes include the namespace. Existing rows and keys migrate to `default`, preserving their old addresses.

## Consequences

- Homonymous resources can coexist in one workspace without ambiguity.
- Relative references remain portable when a complete resource set is moved between namespaces.
- Cross-namespace references are visible and intentional in serialized definitions.
- Callers that omit a namespace retain the pre-ADR behavior.
- Namespace isolation is logical isolation inside a workspace; it does not replace tenant, workspace, or authorization checks.
