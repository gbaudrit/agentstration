# Native Knowledge access validation — 2026-11-10

## Scope and decision

Feature [#1119](https://github.com/gbaudrit/agentstration/issues/1119) extends the
retained Snapshot work in [PR #1088](https://github.com/gbaudrit/agentstration/pull/1088)
on `feature/fr-1042-knowledge-snapshots`.
[ADR-0210](../../decisions/0210-knowledge-exposes-native-snapshot-access-tools.md)
records the native `read`, `search_regex` and `get_status` contracts. Acquisition,
transformation and projection remain governed Flow execution; content access is
owned by `KnowledgeAccessService` and the existing Storage/Artifact boundaries.

The new Tools are immutable ordinary relay ToolDefinitions with a fixed source
identity, projected through the existing Agentstration Tool provider and assembled
into a versioned `.access` ToolSet. Caller overrides are rejected before execution.
Old published Snapshots, Artifact identities and provenance are preserved.

## Deterministic behavioral coverage

`DataSourceDatasetTests.KnowledgeAccess.cs` exercises the real local Storage,
AWP acquisition and projection boundaries with no retrieval Flow:

- read first and regex first, from a source without any acquisition or Snapshot;
- overlapping compatible preparation, one acquisition and one projection;
- metadata-only status inspection before preparation;
- Unicode scalar ranges across an emoji and accented text;
- paged reading, regex pagination and direct reading of a returned passage;
- Snapshot A continuation after B publication and active/latest distinction after
  explicitly reselecting A;
- tampered, expired, mixed-parameter and wrong-source cursors;
- logical consumer isolation, revoked source access and disabled-source refusal;
- zero-length regex progress and unsupported-expression rejection;
- native three-operation relay publication and safe missing-source errors.

`DataSourceDatasetTests.cs` additionally executes all three governed Tools from
Instance context, refuses source override attempts, and checks native reading
against a lower Storage read envelope. The existing documentation bootstrap
end-to-end test invokes the generated native search relay against historical
Artifact content. Runtime Run evidence remains mandatory for actual Agent calls;
the fixture's standalone administrative invocation uses Console caller context.

The existing component tests cover source-name propagation, French result/range
presentation, optional retrieval configuration and prerender interactivity guards.
Console inputs stay disabled until the interactive circuit is ready, preventing
selection changes from being lost during initial rendering.

## Browser and documentation boundaries

The affected existing source lifecycle and governed Knowledge journeys are
maintained in this change. They save automatic projection parameters, verify an
explicit Snapshot through native regex search, and use the new `.access` ToolSet
and native operation identities. An implementation smoke also exercises native
read pages, regex-to-read handoff, detailed status, Instance Tool publication and
Artifact preview/download in English/French on desktop/mobile.

The new reusable journey remains deferred in the native child issue
[#1120](https://github.com/gbaudrit/agentstration/issues/1120), blocked on maintainer
UX validation and merge. Temporary implementation smoke does not satisfy or
replace that gate. The existing parent issues remain open.

## Deliberate limits

- Retained output still supports the builtin local publication adapter. This work
  does not qualify Azure/OpenDAL retained output.
- Text access and non-backtracking regex use the bounded profile documented in
  ADR-0210. Unsupported binary formats require explicit preparation transforms.
- Status freshness describes recorded Snapshot inputs, without probing the origin.
- #1115 TTL, direct handoff, fallback and history cleanup, and #1117 composition
  sequences remain later work. Neither blocks these native access contracts.
  Future cleanup must honor the durable, expiring read leases.
