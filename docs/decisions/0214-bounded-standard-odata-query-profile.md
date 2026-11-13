# ADR-0214: Structured queries start with a bounded OData profile

## Status

Proposed

## Context

[#1151](https://github.com/gbaudrit/agentstration/issues/1151) needs interoperable semantics without inventing a proprietary query grammar. JSONB describes a physical representation; JSONPath and OData describe different query capabilities.

## Decision

The initial optional profile is `odata/4.01`, profile version `1.0`, encoding `odata-options+json`. This is a bounded use of OData URL conventions and the Data Aggregation extension over MCP, not an OData HTTP service. Microsoft.OData.Core parses and binds expressions to the declared optional JSON Schema model. No raw SQL is accepted.

The profile permits reviewed filter comparisons, logical operators, in-lists, contains/startswith/endswith, any/all, explicit selection, declared scalar ordering and simple count/sum/min/max/groupby. Unsupported options and nodes fail before execution. Missing fields are treated as null; equality distinguishes null from non-null, ge/le of two nulls is true, other ordered comparisons involving null are false. Strings use ordinal Unicode code-point order without locale collation. Documents and numeric results must remain in the exact .NET decimal domain. Declared fields enforce JSON types; optional nullable type unions are supported. Sort/group/aggregate fields require a declared scalar model; sum requires a number.

ASP owns page size, result-byte limits and authenticated opaque cursors; `$top`, `$skip`, arbitrary offsets, expansion, joins, arbitrary functions, nested/capturing lambdas and multi-stage aggregate pipelines are excluded. Ordering adds document ID as a final ascending tie breaker. Mutable pages use keyset traversal and can observe intervening mutations; sealed publication pages are stable. Aggregate groups are bounded to one page and fail rather than silently truncate.

## Consequences

The standard parser is a grammar dependency, not the ASP public contract. Another profile can use an object or text encoded as JSON without changing generic query types. Cursor authority includes profile, model, query, scope, selection, current grant and limits. Provider restart or cursor expiry produces an explicit error. The bounded subset and conformance fixtures are versioned; increasing syntax support cannot silently change existing semantics.
