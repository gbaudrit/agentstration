# Resource access and managed Tool activation smoke evidence

Related feature: [#1436](https://github.com/gbaudrit/agentstration/issues/1436).
Deferred reusable journey: [#1437](https://github.com/gbaudrit/agentstration/issues/1437), blocked by maintainer UX validation and implementation merge.

These are screenshots of the local Release executable, not design mockups. The disposable Development fixture uses deterministic AI, SQLite and local authentication. No live GitHub service or credentials are involved. A saved fixture extension contributes one provider, two exact-UID/generation governed ToolSets, two member Tools and one ungrouped Tool. Its descriptor endpoint is intentionally unavailable; the persisted governed inventory remains administrable.

## Routes and observations

- `/extensions/access-smoke?scopeRef=/instance&context=/instance#access`: generic resource access followed by a distinct related-resource panel. The initial saved parent Tenant grant excludes the second Tenant. Saving the general descendant switch makes both Tenant branches eligible. Removing it preserves the targeted grant.
- A related-resource Tenant switch authorizes its current Workspaces. Turning one Workspace off creates an exception; Restore inheritance returns its effective permission. Counts distinguish authorization from inactive/unavailable contributions.
- `/tools/providers/access-smoke?scopeRef=/instance&context=/instance#tools`: verified read/write ToolSet groups and an ungrouped section. Group activation succeeds while the provider definition remains read-only.
- `/tools/tracking-read?scopeRef=/instance&context=/instance#overview`: individual managed activation succeeds. An exact API comparison before/after confirms unchanged Tool definition, identity, generation and content ETag.
- French and English text rendered; the generic switch responds to keyboard Space and Discard restores the saved value.
- Desktop (1600 × 1100) and mobile (390 × 844) were inspected. Access and provider pages have no document-level horizontal overflow, and no uncaught browser errors occurred.

## Captures

- [French desktop access](access-desktop-fr.png)
- [French mobile access](access-mobile-fr.png)
- [English desktop provider groups](provider-desktop-en.png)
- [French mobile provider groups](provider-mobile-fr.png)

The fixture resource names are user-authored content and remain verbatim across locales. No maintainer UX approval is implied by these implementation smoke checks. The new reusable journey has not started.
