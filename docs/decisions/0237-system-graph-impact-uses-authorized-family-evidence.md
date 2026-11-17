# ADR-0237: SystemGraph impact uses authorized family evidence

## Status

Proposed

## Context

[#1197](https://github.com/gbaudrit/agentstration/issues/1197) extends ADR-0236.
The maintainer authorized stacked implementation on the #1196 branch. Families
retain different UID/generation/publication evidence; Query selection has its own
accepted semantics in ADR-0228. Name joins and generic version sorting are unsafe.

## Decision

Typed family-owned contributors extend the existing projection. Family stores
retain authority. Native publications use owner UID plus exact version as graph
key, retaining native publication UID separately. Agent revisions retain their
native UID. Embedded type/function versions invent no UID. Generation nodes use
UID/generation; consumer-specific evidence belongs to relations. Missing/replaced
historical UIDs never rebind by name.

Historical references lacking UIDs remain unsupported. Direct Flow Tool references
share the existing contract resolver lookup and are labelled owner-relative runtime
selection, not historical pins. Parameter/Secret references contain identity
metadata only: value rotation does not enter publication closure. No provider,
acquisition, content or Secret-value operation occurs. Schema content, literals,
query bodies, connection values and physical locations are omitted.

Invocation and graph extraction share Data Sources' publication-selection primitive
and typed Query Tool binding decoder. Exact stays exact. Unpinned selects publication
time, including a lower semantic version published later; drafts and active pointers
are excluded. The selected family contract digest is compared to the relay's
expected contract. Compatible dynamic changes need no downstream republication.
Incompatible latest remains a mismatch without older fallback. General publication
compatibility stays unknown: integrity equality is not universal compatibility.

One validated authorized observation feeds bounded reverse traversal and change
comparison. Dependency edges point consumer to dependency; generation links connect
pins to identities; membership connects definitions to publications. Declared,
operational, value-reference and logical-membership edges do not enter executable
closure. Paths preserve IDs, source fields and selection strategy. Limits are
1–32 edges deep and 1–1,000 paths, default 16/200. Ordering is deterministic; cycles
and truncation are explicit. Comparison distinguishes identity replacement,
generation drift, Query contract compatibility/mismatch and unknown publication
compatibility. Analysis creates no plan or mutation.

Current RBAC/Discover/Use projection precedes analysis. Derived pins also require
inspection rights on the consumer disclosing them: target-only checks would let a
hidden consumer change visible counts/digests. Relations check the same owner.
Missing and invisible roots are indistinguishable. Coverage is a static capability
matrix with no resource-specific diagnostics or inventory counts.

Limitations include legacy references without UIDs, opaque settings, native Entry
identity integration and #1189 proposal refresh. None is fabricated. Missing
evidence, partial authorization, cycles or truncation cannot certify no impact or
safe reconciliation. GloballyComplete remains false. ADR-0236 repeated-read/cursor
rules remain; no persistent index, cross-store transaction or historical backfill
is introduced. Existing ADR/DAT scope drift stays recorded.

## Consequences

Application services support API and future Console. SQLite and deterministic
workers suffice; supported stores reuse existing ports without migration. Explorer
remains #1198, reconciliation #1199 and operational overlay #1200. In particular,
#1031's undecided Agent-adoption primitive is not implemented by an impact path or
compatibility label.
