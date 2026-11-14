# ADR-0197: Connected Service bindings support typed literals

## Status

Accepted

## Context

Connected Service type inputs currently require an exact Parameter or Secret
reference. This is appropriate for reusable configuration and credentials, but it
forces administrators to create a Parameter for ordinary values that belong only
to one Connected Service. The shared value-binding editor introduced for Model
Providers already distinguishes typed literals from governed resource references.

A Console-only literal would be lost when the resource is saved. The Connected
Service contract and resolver must therefore represent and validate the same
choice explicitly.

## Decision

Add a `literal` Connected Service binding kind carrying one JSON scalar in the
`literal` property. A binding contains exactly one payload: a Parameter reference,
a Secret reference or a literal.

Standard inputs accept a literal or a compatible Parameter. Standard text inputs
may also use a Secret when the value must remain write-only. Secured inputs accept
only Secrets; a literal is persisted configuration and is never a credential.

Validate a literal against the input's declared scalar type and cap its serialized
representation at 64 KiB. The internal resolver returns a cloned JSON value
directly, while Parameter and Secret resolution retain their existing governed
lookup paths. Management APIs, YAML and the Console expose literal values because
they are part of the resource definition; Secret values remain undisclosed.

This decision extends ADR-0195's binding contract and follows ADR-0194's shared
typed-literal semantics. It does not change the exact reference, scope or use-policy
rules for Parameters and Secrets.

## Consequences

- Ordinary endpoint, timeout, retry and feature values can be configured without
  creating single-use Parameter resources.
- Existing Connected Services and clients remain valid because the new kind and
  payload are additive.
- Operators must not place credentials in literals; secured inputs reject them and
  provider types should classify sensitive values as secured.
- Connected Service consumers receive the same typed value regardless of whether
  it came from a literal or a compatible Parameter.
