# ADR-0087 — Source Channel compatibility uses Semantic Version intervals

## Status

Accepted

## Context

Different Channels of one Source Version may publish content for different Agentstration generations. Provider acquisition and locale selection cannot decide whether that content is safe for the running product. Compatibility must remain part of the immutable published Channel definition and must be recalculated when Agentstration is upgraded.

## Decision

- Every Source Channel declares `compatibility.agentstration.minVersion` and may declare `maxVersionExclusive`.
- Bounds and the running product version use Semantic Versioning 2.0 precedence. The minimum is inclusive, the maximum is exclusive, prerelease identifiers use numeric-versus-lexical SemVer ordering, and build metadata does not affect precedence.
- Imports reject malformed versions and empty or reversed intervals. Compatibility does not affect import: an incompatible Source Version remains available for inspection.
- The running version is read from Agentstration assembly informational metadata through an injectable provider. Status is evaluated on each request and is therefore recalculated after an upgrade without rewriting immutable Source Versions or snapshots.
- Status is `Compatible`, `Incompatible`, or `CompatibilityUnknown` and includes a stable reason code. An unknown running version fails closed for new consumption.
- Channel refresh and pinned-snapshot catalog browsing require `Compatible`. Existing snapshots and refresh history remain retained and directly inspectable while incompatible.
- Compatibility applies to the complete Channel Snapshot. Bootstrap locale variants cannot override it, and Source Providers do not evaluate it.

## Consequences

Administrators can import and inspect content targeting another Agentstration generation without accidentally refreshing or consuming it. Future Bootstrap application and Pack installation entry points must pass through the same compatibility guard when they consume Source catalog selections. Local Bootstrap profiles and Packs that do not originate from Sources remain unchanged.
