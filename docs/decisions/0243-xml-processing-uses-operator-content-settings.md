# ADR-0243: XML processing uses operator content settings

## Status

Proposed

## Context

The first implementation of #1214 limited XML invocation strings, parsed documents
and mapped JSON to independent hardcoded 64 KiB ceilings. A real acquisition
exceeded the wire string limit, while the Console reported only that both expected
and received values were strings. ADR-0236 already establishes configurable operator
content ceilings. The maintainer requested enriching those settings and an in-place
change for a reseed of the unreleased installation, without new resource versions.

## Decision

`Agentstration:XmlContent` owns positive operator ceilings for XML instance UTF-8
bytes, depth, reader nodes, attributes per element, selected nodes, array items and
mapping operations. The default document ceiling is 10 MiB, matching the existing
content-budget defaults. Structural and mapping defaults remain the original
32 levels, 8192 nodes, 64 attributes, 8192 selected nodes, 100 array items and 32768
mapping operations; operators can change them independently. There is no additional
fixed validation maximum below the settings' positive Int32 range.

The XSD instance validator and native mapper use the same XML content options.
The mapper's exact serialized JSON output uses the existing
`Agentstration:FunctionContent:MaximumValueBytes`. Wire invocation schemas describe
a string without a second hardcoded character limit. The existing function budget
still bounds serialized invocation JSON, including escaping and wrapper overhead.
Raw HTTP, Flow, Storage, remote protocol and business-schema constraints remain
independent; increasing one ceiling does not widen any other contract.

Standalone XSD definitions retain the bounded schema-authoring profile (64 KiB,
32 levels, 8192 reader nodes, 64 attributes). XML instance settings do not prevent
validating the schema document itself when an operator narrows an instance limit.
DTD/entity declarations, external resolution, import/include/redefine and schema
patterns remain prohibited. The mapping language and declaration-authoring limits
are unchanged. Parsing and mapping continue to observe cancellation.

Failures retain structured numeric metadata: unit, measured count, minimum/maximum
and, for operator quotas, the exact configuration key. Metadata never contains
instance content. Function/XSD/mapping failures use the existing data-contract
failure channel, including the causal run/step. The localized Console shows the
measured amount, actual limit, relevant setting and correction; it distinguishes
schema constraints from operator quotas. Historical string-length failures without
numeric evidence stay readable and do not fabricate measurements.

The unreleased engine, provider and resource versions stay at 1.0.0. Reseed is
required for changed immutable native invocation digests. No compatibility version,
implicit rebind, bootstrap-procedure change or automatic data reset is introduced.
This supersedes ADR-0241's fixed instance and mapping payload ceilings. Accepted
schema/mapping references and version adoption rules otherwise remain unchanged.

## Consequences

Configuration is validated before stores open and takes effect after restarting
the authority. Lower limits can reject previously fitting executions. Errors give
operators enough numeric evidence to adjust the specific setting without exposing
XML, JSON, credentials or opaque business values. This remains a bounded local
vertical, not a general XML execution or streaming platform. #1215 stays blocked
until maintainer UX validation and implementation merge; #1214 remains open.
