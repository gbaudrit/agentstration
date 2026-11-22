# TEST ONLY: Bootstrap load profile

This committed input contains **512 Tenant-scoped integer Parameters**, named
`bootstrap-load-test-001` through `bootstrap-load-test-512`. Select an explicit,
disposable Tenant in **System > Bootstrap profiles**. It uses SQLite and no
provider, Secret, network service, Docker or cloud account. It is intentionally
absent from all standard `InitialProfiles` selections.

Preview is read-only. Reapplying skips existing addresses and preserves edits.
Use an isolated data directory or disposable Tenant; this is not production
configuration. The manifests are the versioned test input; builds generate none.

The profile exercises incremental persistence and observation at the catalog's
512-document limit. Its elapsed time depends on the machine and storage. Focused
regression tests gate a local handler beyond a short initiating HTTP budget and
observe durable progress while execution remains blocked; they do not require a
machine-dependent wall-clock threshold. See `docs/reference/declarative-bootstrap.md`
for the asynchronous API and recovery behavior.

## Retained local measurement — 2026-11-18

On Debian GNU/Linux 13, .NET SDK 10.0.300 / runtime 10.0.8, SQLite, deterministic AI and fresh
isolated browser-fixture storage, the desktop Console applied all **512 Parameters
in 30.009 seconds** while the offline integration and browser campaigns were also
running. The Console accepted the application, displayed persisted progress,
reloaded the same identity, and reached `succeeded` with 512 created resources,
zero extra applications, zero page errors and no horizontal page overflow. No
artificial resource delay was enabled for this browser measurement. The mobile
reapplication took **17.282 seconds**, retained the same identity after refresh,
and finished with all 512 resources skipped.

This is a reproducible workload and a measured example beyond the ordinary
15-second request budget, not a universal duration or minimum resource threshold.
The focused gated regression separately ensures coverage beyond 15 seconds even
on faster storage. Reapplication is tested as skips, not resource replacement.
