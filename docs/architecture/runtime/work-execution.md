# Work execution

The Work boundary receives, represents, and tracks work delegated to agents. It owns the functional request, lifecycle, requester identity, correlation, interactions, notifications, history, result, error, and optimistic version. Runtime owns technical Agent execution; Flow owns graph execution and Flow Run history.

A Work Item stores only a lightweight reference to an exact or active Flow version. Root submission resolves an active reference to an immutable published version, creates or recovers one idempotent Work Item and deterministic root Flow Run, and persists trusted Tenant, Workspace, Principal, origin, causation, correlation, and idempotency context.

The functional lifecycle includes pending, queued, running, waiting-for-input, waiting-for-approval, completed, failed, and cancelled states. State transitions are aggregate operations rather than caller-assigned values. Stable execution event identities make replay idempotent.

Work code depends on the provider-neutral execution gateway rather than Runtime, Microsoft Agent Framework, provider, storage, or HTTP implementations. Work storage remains logically independent from Management, Flow, and Runtime storage.

See [Submit Work](dynamic-views.md#submit-work), [Execute Flow](dynamic-views.md#execute-flow), [ADR-0009](../../decisions/0009-independent-work-plane.md), and [ADR-0135](../../decisions/0135-work-conversations-and-tasks-are-principal-owned.md).
