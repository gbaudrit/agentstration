# Flows: real resource and wizard captures

Product source: `718ca699c9abb518c931a82d4e38d1cf614a79d9`. The actual browser fixture uses `Agentstration.Web`; its Console component assembly SHA-256 matched the tested project assembly (`1a7b7022cb656daf9d6b26927ef85d70dbb0e3a34df53fa32cb08148e886d3a8`) before the campaign. Web and Workplace Release hosts built without warnings or errors. No assembly was rebuilt during the campaign. Earlier attempts that rebuilt only the separate API/Console hosts are not validation of their nominal source commit and are excluded from these captures.

Desktop English/dark (1440×1000) and French touch/light (390×844) pass both temporary probes, using real taps on mobile, persisted appearance and language, and full document-width assertions. The Workflow probe checks saved Overview identity versus pending metadata, navigation retention, independent root activation and unchanged draft revision/graph/tags. Focused component cases separately check the independent root and draft ETags. The orchestration probe checks retained participant order and metadata, exact owner writes, activation preserving edits, and publication of the explicitly requested immutable 2.0.0 version with the same resource UID.

The creation probe freely navigates incomplete stages, exercises final inline validation, retains the exact manually entered technical name and selected template, and creates a real Workflow draft at revision 1 before opening the existing designer. It does not publish the draft. The existing handoff Flow/Entry publication journey also passed at `cf487270` with its recorded build; it remains separate from these capture probes.

The 58 combined Flow detail/editor/creation component cases and TypeScript checks pass. The latest complete Console suite recorded 849 passing cases before the scope-row assertion maintenance, which also passes the focused selection. Existing observability and execution-scope journeys pass on this capture source; their final campaign results are recorded separately. Flow Run capture validation is separate because the Worker assignment card still needed a mobile header-width correction. No final Fast/Integration or complete browser-smoke result is claimed. New reusable UX journeys remain deferred under the repository rules.

- [flow-creation-identity-desktop-en.png](flow-creation-identity-desktop-en.png)
- [flow-creation-identity-mobile-fr.png](flow-creation-identity-mobile-fr.png)
- [flow-creation-review-desktop-en.png](flow-creation-review-desktop-en.png)
- [flow-creation-review-mobile-fr.png](flow-creation-review-mobile-fr.png)
- [flow-creation-template-desktop-en.png](flow-creation-template-desktop-en.png)
- [flow-creation-template-mobile-fr.png](flow-creation-template-mobile-fr.png)
- [orchestration-overview-desktop-en.png](orchestration-overview-desktop-en.png)
- [orchestration-overview-mobile-fr.png](orchestration-overview-mobile-fr.png)
- [orchestration-published-desktop-en.png](orchestration-published-desktop-en.png)
- [orchestration-published-mobile-fr.png](orchestration-published-mobile-fr.png)
- [orchestration-retained-draft-desktop-en.png](orchestration-retained-draft-desktop-en.png)
- [orchestration-retained-draft-mobile-fr.png](orchestration-retained-draft-mobile-fr.png)
- [workflow-overview-desktop-en.png](workflow-overview-desktop-en.png)
- [workflow-overview-mobile-fr.png](workflow-overview-mobile-fr.png)
- [workflow-retained-draft-desktop-en.png](workflow-retained-draft-desktop-en.png)
- [workflow-retained-draft-mobile-fr.png](workflow-retained-draft-mobile-fr.png)
