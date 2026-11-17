# ADR-0236: Storage content has operator ceilings and resource envelopes

## Status

Proposed

## Context

The HTTP operation, native dataset, local object, retained publication and Artifact
materialization paths used independent fixed content bounds. Raising a policy
default could create a policy rejected by the ASP chunk validator, while larger
stored values could fail later during Artifact registration or publication.

The maintainer selected server configuration combined with narrowing Storage
resource limits, and explicitly requested a reseed instead of a compatibility
version for the unreleased local type.

## Decision

Storage owns validated operator options under `Agentstration:StorageContent`.
The effective operation bound is the minimum of the server ceiling, exact type
capability, Storage envelope and consumer rule. Object size and transfer chunk
size are distinct. Local content, retained publications and materialized Artifact
reads enforce the configured object budget; native Artifact copies use bounded
chunks. Local object count, decoded object-byte capacity, receipt count and
retained publication count remain separate operator quotas.

Data Sources owns `Agentstration:DataSourceContent` for raw builtin HTTP response
bytes and serialized JSON values. JSON encoding and wrapping can increase size;
an accepted HTTP body does not guarantee that the serialized result fits.

Product local content validation reuses ASP identity, key and range rules without
mistaking product payload sizes for wire chunks. ASP and AEP keep their standalone
64 KiB content wire/contribution ceilings. The integrated `asp.documents/1`
contracts, document collections, query profiles, immutable document publications
and remote adapters remain unchanged. Their document/result limits and the
64 KiB aggregate JSON record-set profile are independent of product content limits.
Local record publications also honor narrower operator ceilings. Server
configuration cannot grant missing capabilities or widen a record-set profile.

Local `1.0.0` keeps a stable 16 MiB supported payload ceiling independent of host
configuration. Its unreleased schema/contract is updated in place for the
maintainer-authorized reseed. No existing resource is silently rebound or reset.
The new default server content budget is 10 MiB and default transfer chunks remain
64 KiB. The previous 64 KiB local publication profile in ADR-0207/0208 is superseded
by these configured bounds; those records remain unchanged.

## Consequences

- Invalid or excessive configuration fails during composition, before storage
  initialization. Environment variables use the standard double-underscore names.
- Resources retain their own envelopes. Raising the server ceiling does not
  widen an existing envelope or consumer rule; lowering it can refuse operations
  that previously fit. Changes take effect after restarting the authority.
- Supported in-memory payloads remain bounded to 16 MiB, and local decoded object
  capacity to 1 GiB. ASP wire schemas, buffer sizes, agent text-read windows and
  unrelated schema, parameter, token and retrieval-scan limits remain separate.
- The existing local JSON object backend still rewrites bounded state atomically;
  this change does not add a streaming object database or a new persistence layer.
- Reseed is required for databases containing the previous immutable local type
  contract. This change supplies no migration and does not perform the reseed.
