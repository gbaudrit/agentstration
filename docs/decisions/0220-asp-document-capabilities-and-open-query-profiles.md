# ADR-0220: ASP documents use additive capabilities and open query profiles

## Status

Proposed

## Context

[#1151](https://github.com/gbaudrit/agentstration/issues/1151) extends ASP beyond byte content. Existing content sessions, transfers and StoreRefs must remain compatible. Generic contracts must not depend on PostgreSQL, an OData parser tree or a mandatory query syntax.

## Decision

Add `asp.documents/1` alongside `asp.content/1`, on the existing MCP transport and protected AEP binding exchange. AEP advertises `aep.storage-documents@1.0` and explicit document, query and immutable-publication capabilities. One Storage Type contribution declares its mapping; no competing contribution registry is introduced.

A query profile is an open `(id, version, encoding)` reference plus an exact document-model digest. Discovery supplies supported features, input schema and limits. Requests pin the complete reference. Unknown profiles, encodings, versions and model revisions fail explicitly; no fallback or syntax guessing occurs. The generic SDK carries JSON query payloads, not a public provider AST. Optional OData code owns its parser tree.

Document get/conditional put/conditional delete and query use independent rights. Publication preparation/sealing/inspection/retirement is separately qualified. Unsupported operations fail explicitly, preserving the canonical MCP mapping. Patch and Search remain absent.

The ASP document profile owns its bounded document-schema subset. Storage management delegates collection-schema validation to the registered profile validator through its existing definition-validation boundary; generic configuration/envelope schemas and acquisition Artifact contracts retain their existing Schema-family behavior. Local nonrecursive `$defs`/`$ref`, typed nullable `anyOf`, and asserted `date-time`/`uri` formats are supported. A bounded expanded model feeds typed query discovery while the original schema remains the persisted, digest-bearing identity. Instance validation uses the standard JsonSchema.Net implementation with remote resolution disabled. Invalid collection schemas carry bounded field paths and keyword codes for localized Console diagnostics.

## Consequences

A provider can implement documents without query, or add other declared query profiles later. PostgreSQL never enters the generic contracts. Future JSONPath or governed relational profiles need their own semantics and authorization review. Existing content contribution digests and absent optional fields retain their original wire representation.
