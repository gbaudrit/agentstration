# ADR-0264: Coding GitHub operations use protected relays and native recovery

## Status

Proposed — implementation for [#958](https://github.com/gbaudrit/agentstration/issues/958).

## Context

Coding needs native repository identity, issue relationships and pull requests without
adding GitHub fields to generic Work. The generic Git Source Provider is an acquisition
adapter; it does not own tracker policy. Provider transport authentication is distinct
from a consumer's GitHub authority (ADR-0122). Existing published Tool relays already
pin source identity and resolve protected inputs at execution time (ADR-0202).

## Decision

- Add Coding contracts, a canonical application service and a family API module.
  Resolve an explicitly associated private Project/task and exactly one repository for
  an atomic task, or a bounded set for composite work. Return native repository ID,
  default branch, observed commit and capabilities with Project/task versions and a
  context digest. Pull-request requests pin the exact published connection identity;
  new effects require the observed Project/task versions. Explicit recovery may outlive
  task edits but cannot silently adopt a changed connection.
- Keep the GitHub implementation in an optional autonomous AEP/MCP executable. Its
  wire DTOs have no references to host Work, Coding or application assemblies. Generic
  issue intents and local receipts continue through #955. Coding owns repository/PR
  policy; issue intake and one-issue/one-plan coordination remain #1346/#1349.
- Allow an exact published Tool as a Project Repository or TaskOperations binding.
  Preserve legacy ToolProvider discovery for generic task operations. Pin both Tool
  and provider, reauthorize Discover/Use and RunsExecute before dispatch, and invoke
  the existing governed pipeline with trusted WorkOperation caller coordinates.
  Bounded declaration metadata on ToolDefinition projects immutable capabilities;
  reserved platform identity metadata cannot be overridden. A relay's declaration
  cannot exceed its immutable source's capabilities.
- Bind `/credential` using an existing Secret binding. Bind API origin, allowed
  repository names/native IDs, and managed label prefix as fixed relay inputs. Public
  caller schemas omit fixed inputs and reject overrides. Existing protected execution
  resolves and redacts Secrets and suppresses argument capture. ExtensionRegistration
  credentials authenticate AEP transport only. No global GitHub token is introduced.
- Validate the API base against the extension's explicit origin allowlist, disable
  redirects, bound requests/responses, and verify native repository ID before every
  operation or replay. Recheck native issue/PR read authority before returning a
  terminal provider receipt. Renaming/replacing a repository fails closed. Production
  origins use HTTPS; HTTP loopback is a separately enabled fixture option.
- Persist provider write-ahead receipts on the extension's durable state volume under
  opaque operation keys. A cross-process file lease serializes same-key calls; atomic
  file replacement and flushing persist Sent before HTTP. Store authority, operation,
  exact input, intent digest and terminal result, never credentials. An intact journal
  with no entry can report Missing; loss of this state volume requires operator
  reconciliation before resuming writes. It is not safe to treat volume loss as proof
  that remote effects did not occur.
- Recover issue/PR creates using their exact native operation marker. Never repeat a
  Sent create merely because bounded native observation finds no match. Hidden,
  incomplete or ambiguous observations retain Uncertain. Existing terminal receipts
  replay unchanged, and changed input/authority conflicts. Explicit recovery does not
  become an automatic retry worker.
- Read native Sub-issue, parent and blocked-by relationships. Verify list completeness
  against native summaries; missing capability or incomplete visibility is an error.
  Add only explicitly supplied managed labels and native relations, preserving unrelated
  labels and existing parents. Reject destructive title/body/state updates because
  [GitHub does not generally support conditional unsafe methods](https://docs.github.com/en/rest/using-the-rest-api/best-practices-for-using-the-rest-api).
  ExpectedRevision is a preflight observation for additive operations, not atomic CAS.
  Authoritative inter-instance claims remain the later Git CAS integration.
- Verify the expected PR head before creation and inspect the returned head afterward.
  A concurrent branch move produces Uncertain; GitHub REST creation does not atomically
  bind the supplied SHA. Native branch ownership/CAS remains a caller prerequisite.

## Consequences

The standalone deterministic server needs no extension or GitHub credentials. Provider
state is separate from host Work persistence; generic task receipts retain SQLite and
PostgreSQL parity without schema changes. Native artifacts never accept local work
functionally or activate a plan. The offline fixture exercises the real AEP/MCP and
protected-relay boundary against a fake native HTTP server; live GitHub validation is
separate. No Console or Workplace journey changes.

Native API references: [Sub-issues](https://docs.github.com/en/rest/issues/sub-issues),
[dependencies](https://docs.github.com/en/rest/issues/issue-dependencies), and
[pull requests](https://docs.github.com/en/rest/pulls/pulls).
