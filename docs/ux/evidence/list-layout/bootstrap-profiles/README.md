# Bootstrap profile workflow disposition

Route `/settings/bootstrap` was reviewed individually and remains outside the top-level catalog template migration. The primary interaction is an ordered composition/apply workflow: select valid profiles of compatible scope, choose a Tenant/Workspace target, resolve typed bindings, preview, confirm, then apply the exact preview digest. Source-provided profile selection is also supported. These are protected form/workflow semantics; no UI or API was changed.

GET `/api/bootstrap/profiles` and the family service require PlatformAdmin. BootstrapProfileManagementService returns the configured server profile snapshot, eligible targets and the latest 100 application records, with recovery of interrupted Running applications. Binding option reads are independently bounded to 1,000 resources per kind. The existing apply endpoint receives ExpectedDigest, and the component submits preview.Digest after confirmation. A queryable complete resource catalog or pageable history contract cannot be inferred from this management view.

The profile checkboxes/order controls, target/binding choices and nested diagnostics retain their specialized classification. Existing CollectionView frames render preview resource dispositions, application history and applied-resource details inside the workflow. They remain embedded; no global filter, exact all-history total, new pagination or creation-page template is invented. Existing latest-retained-run wording, permissions, source selections, confirmation, digest checking and result states remain intact.

[Source proof](source-proof.json) records the reviewed page/client/API/application/catalog sources. Inventory entries now record this decision without changing their renderer or historical evidence. This documentation-only review claims neither new UI adoption nor fresh screenshots. Existing Bootstrap domain regressions and the final full offline lanes provide the reported validation; reusable #1242 remains blocked pending explicit UX validation and merge.

Final full-solution validation: warning-free Release build; Integration 1,542 passed / 28 optional cases skipped / zero failed; Fast 2,197 passed / one existing Flow orchestration fixture failure reproduced independently on upstream 118b9ad7; all three source inventory assertions passed. [Exact results and commands](validation.json). This records the full branch's handoff validation without claiming that unrelated protected editor regression is green.

## Superseding Bootstrap UX follow-up — 2026-11-21

The maintainer subsequently requested the catalog list layout. `/settings/bootstrap`
now adopts ResourceCollectionTemplate with its summary cards, search, scope filter
and exact local paging over the existing authorized catalog snapshot. The ordered
selection, explicit target/bindings, preview and confirmed digest submission stay
in this section. Preview rows are paged at 25 without rerunning validation.

Manual application observation now uses StepProgress within this same page,
with summary cards first, actual ordered resource groups and bounded resource
pages. Reload reads the persisted application/plan; it does not submit again.
History and audit details remain available. Historical captures and the earlier
review above do not validate the new layout. Reusable #1217 remains deferred.

[Fresh catalog, preview and progress review captures](../../1250/startup-progress/catalog-and-step-refinement/README.md) document this follow-up.
