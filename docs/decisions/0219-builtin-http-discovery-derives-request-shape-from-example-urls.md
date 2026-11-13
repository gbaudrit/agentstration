# ADR-0219: Builtin HTTP discovery derives request shape from example URLs

## Status

Proposed — implementation for [#1167](https://github.com/gbaudrit/agentstration/issues/1167).

## Context

Connected Services own reusable operation discovery; Data Sources own acquisition
parameters, and Knowledge Sources consume acquired data. Ordinary HTTP endpoints,
including RSS feeds, may have no OpenAPI contract. An example request URL supplies
enough information to describe a GET operation without probing the remote system.

## Decision

Add `http-endpoint@1.1.0` alongside the unchanged `1.0.0` descriptor. The new version
pins a Core-owned discovery Flow and `http-get@1.0.0` operation type. Discovery uses
the existing durable run, authorized internal Tool, validated manifest and operation
reconciliation lifecycle. It requires no extension and performs no network request.

The configured `baseUrl` is the complete example request URL. Its origin and exact
path become the operation endpoint. Distinct decoded query names become optional
string inputs, with no defaults, examples or fixed values. Parameter order is
canonicalized. The operation's external identity depends on method and endpoint;
structural changes alter its definition digest and require consumer reconciliation.
Query-value changes do not change the discovered definition. Duplicate decoded
names, empty/invalid names, credentials in URLs and fragments are rejected. Bounds
are 2,048 URL characters, 64 parameters and 128 characters per decoded name.

A Data Source selects the exact activated operation and supplies its own input
object. Missing optional parameters are omitted, empty strings are sent as empty
values, and names and values are percent-encoded independently. The internal
acquisition Tool revalidates the accepted acquisition/caller/run and resolves the
connection late. It regenerates the expected operation shape before dispatch;
changing a bound Parameter to another endpoint or query structure cannot redirect
an old operation silently. Example query values are never invocation fallbacks.

Reuse the existing public-network HTTP content fetcher with no redirects, a
20-second deadline, and a 48 KiB response-body limit. Text, JSON, XML, RSS and Atom
are supported as UTF-8 text. The result is a JSON envelope with `mediaType` and
`content`, subject to the existing 64 KiB serialized Data Source value limit. This
is content acquisition, not RSS parsing or output-schema inference. The existing
dataset pipeline stores and registers its result; Knowledge behavior is unchanged.

Existing consumers remain pinned to their selected type version. Adopting the new
service version is explicit; no service or Data Source is automatically upgraded.
Provisioning preserves existing use policies and does not create access grants.

## Consequences

- Simple endpoints become reusable without OpenAPI or an AEP extension.
- Discovery describes a request shape, not a verified remote API contract. It
  cannot establish requiredness, business semantics or response structure.
- Unsupported encodings, media types, private destinations, redirects and oversized
  responses fail through the existing bounded execution boundary.
- This increment supports GET query parameters only; repeated-key/array input,
  headers, bodies, path templates and authenticated HTTP require separate work.
- The new reusable browser scenario remains tracked under #1169 and starts only
  after explicit maintainer validation of implemented UX and implementation merge.
