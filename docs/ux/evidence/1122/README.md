# Reusable collection coverage evidence (#1122)

The expanded local campaign passed **115 cases**, with zero failures, skipped or flaky cases. It exercised 98 independent route/tab/alias cases, ten populated domain workflows, four long-owner/homonym variants and three source-inventory checks against parent implementation `1e2c797`. A separate follow-up passed all three strengthened catalog checks after distinguishing three unmounted legacy renderers and explicit host ownership for live shared widgets.

These unedited screenshots are success attachments from the actual executable application. The owner name uses the maximum supported 120 characters; the two Model Profiles share a display/technical name and differ by namespace. Tests verify exact navigation/copy, visible owner type, bounded ellipsis, full hover/focus/tap disclosure, Escape/focus restoration, accessible text and absence of per-row owner-resolution requests.

| Variant | Full owner disclosure |
| --- | --- |
| desktop dark | [View](long-owner-desktop-dark.png) |
| desktop light | [View](long-owner-desktop-light.png) |
| mobile French dark | [View](long-owner-mobile-french-dark.png) |
| mobile French light | [View](long-owner-mobile-french-light.png) |

[Machine-readable local result](results.json). Final stacked CI runs all 218 smoke cases; its links and status are maintained in [draft PR #1132](https://github.com/gbaudrit/agentstration/pull/1132). The implementation’s [adoption matrix](../../all-list-adoption-1121.md) distinguishes active collection evidence from source-only retained legacy components. Implemented-UX acceptance and both merges remain pending.
