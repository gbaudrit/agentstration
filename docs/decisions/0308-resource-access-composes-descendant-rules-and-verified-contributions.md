# ADR-0308: Resource access composes descendant rules and verified contributions

## Status

Accepted

## Context

[#1436](https://github.com/gbaudrit/agentstration/issues/1436) requires simple descendant access administration, extension-wide contribution administration, ToolSet inheritance and activation of managed Tools. ADR-0172 already separates local access overlays from governed resource content. ADR-0181 reserves managed content mutations to its exact manager. ADR-0150 preserves immutable executable ToolSet/Agent selections, but does not supply collection-derived administrative access.

An extension registration, its contributions and its consumer scopes are different identities. A resource grant must not be confused with access to everything it contributes. Conversely, enumerating today's Workspaces or copying a ToolSet grant to every member would lose future-descendant semantics and explainable inheritance.

## Decision

### Rule order

Evaluate exact resource identity, persisted scope ancestry and action before considering an allowance. Same-scope access retains ordinary RBAC. For descendants:

1. Reject missing, stale, invalid, cyclic or cross-owner relationship evidence.
2. Require every verified access prerequisite, including the exact source ExtensionRegistration, to allow the same consumer/action. A contribution grant cannot bypass a revoked parent.
3. Apply the resource's nearest applicable scoped action rule: exact Workspace overrides propagated Tenant rules; explicit deny wins over inherited collection allowances.
4. Apply the resource's durable `AllDescendantActions` general rule when no narrower scoped action exists.
5. Otherwise, allow through any authorized verified ToolSet membership. An explicit denial on the Tool itself blocks every ToolSet route. Absence of a grant still denies.

Allow and deny actions are disjoint. Targeted rules can propagate to descendants. General rules belong to the resource's exact owner and cover current and future valid descendants, without fictitious self-grants or a list of today's targets. Turning the general rule off preserves targeted rules. Workspace-owned resources have no descendant general rule.

### Verified relationships

Resource families provide stateless relationship projections over saved documents. The same bounded policy graph evaluates storage discovery and application use; no UI-only grant engine is introduced. Management and independent Flow identities are joined by exact UID at the existing identity-catalog boundary.

Trusted AEP reconciliation records the source registration UID in `ResourceOrigin.ResourceUid`. Source names, tags, annotations and string prefixes are not membership evidence. Recreating a same-named registration does not adopt its predecessor's grants or contributions.

Managed ToolSet drafts can provide administrative inheritance before their disabled Tools are published. Each member records exact Tool UID and generation alongside its exact reference and must retain the same owner and stable manager/source identity. Supplied extension version strings do not replace management authority. Operator-owned ToolSets provide inheritance through their exact same-owner published membership; a consumer-owned set cannot delegate an ancestor Tool. Multiple valid sets combine allowances, while explicit Tool denial remains decisive. Published ToolSet versions use the live access policy of their exact owning set.

Membership authorization never publishes a ToolSet, selects a new executable version, or adds Tools to an immutable Agent revision or Flow selection. Tool remains the invocation, approval and audit boundary. This evolves ADR-0150's administrative access behavior while retaining its execution/composition invariants.

### Two-panel administration

The generic Access panel always administers the displayed resource. It offers the all-descendants switch plus targeted, action-specific grants and explicit exceptions.

An optional additional panel administers verified governed resources or members. Its eligible targets come from the parent's saved effective access. A direct Instance-to-Workspace grant does not require an intermediate Tenant grant. A discover-only parent grant cannot supply contribution use. Parent revocation makes saved contribution settings ineffective; it does not erase them.

Tenant collection rules propagate to current and future Workspaces. Workspace switches create explicit exceptions and can restore inheritance. Bulk operations use current, exact bounded membership with an inventory digest and individual policy ETags. They preserve independently authored explicit resource denials, administer included ToolSets instead of copying grants to their Tools, and report individual conflicts or skipped exceptions. A batch applies to its reviewed current collection; it does not give unrelated future extension contributions blanket access.

The UI retains inherited origins, partial counts, direct exceptions and truthful availability states. Resource-owned administrative scope presentation does not expose operational Instance/Tenant Workspaces. Grouped provider views use verified ToolSet relationships, with a separate ungrouped collection.

### Operational activation

Administrative activation is a separate persisted, exact-UID operational overlay with its own ETag and update time. It is not stored in the governed definition and does not advance resource generations, content hashes, publications or executable pins. Missing operational state uses the declared enablement value. Refresh and re-enrollment do not overwrite an administrator's choice. Replaced UIDs cannot reuse it.

Tool resolution, publication eligibility, simulation/invocation and Bootstrap Tool contribution prerequisites use effective activation. Availability, provider/definition readiness, access and immutable assignment checks remain separate. Operational changes are audited through the existing security boundary.

Flow execution retains established owner-side authorization for private dependencies. Exact Core-governed built-in implementation Tools execute under their Tool owner, while an explicit consumer denial still blocks them; other non-extension dependencies use the Flow definition owner. The Artifact service similarly retains owner-side authorization only for exact core-governed built-in staging capabilities; extension backends keep the real consumer check. The server carries that authorization scope as internal invocation context; it is never derived from MCP business arguments. Extension-governed Tool access remains checked for the real logical consumer, including its source-registration eligibility. Administrative physical Workspaces do not become authorization scopes.

## Consequences

- Definition immutability and policy administration remain independent.
- Discovery, direct consumption and invocation share policy precedence and identity validation.
- Source-registration revocation is effective on subsequent authorization without removing saved local rules.
- SQLite and PostgreSQL store the same access and operational state model. This unreleased product uses a fresh schema; no compatibility bridge or upgrade migration is added for earlier development data.
- Required offline behavior tests cover identity forgery, multiple membership, scoped exceptions, parent revocation and content-preserving activation. The new reusable browser journey remains gated by maintainer UX approval and implementation merge under [#1437](https://github.com/gbaudrit/agentstration/issues/1437).
