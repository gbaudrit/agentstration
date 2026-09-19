# Agentstration Assistant Pack

This official Workspace-scoped Pack installs one stable `ask-agentstration` Entry backed by a presentation-neutral routing Flow. The router delegates product questions to bounded documentation help, operational questions to read-only diagnostics, explicit design requests to the separately installed Resource Planning Pack, and ambiguous requests to a clarification fallback.

Bind `assistant-model` to a compatible ModelProfile and `assistant-runtime` to a local RuntimeProfile. Install `agentstration/resource-planning` in the same Workspace before invoking the planning route. The cross-Pack reference is explicit (`agentstration.resource-planning`) so help and diagnostics remain independently replaceable and the Assistant does not duplicate planning behavior.

The Entry is exposed to Console and Workplace owning-space surfaces. Presentation consumers discover it through the existing Entry contract; this Pack contains no surface-specific UI. Child Flow runs preserve the platform's parent/root causality chain. A failed child terminates through the router's stable capability-failure boundary rather than fabricating a response.
