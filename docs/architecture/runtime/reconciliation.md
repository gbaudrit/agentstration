# Runtime reconciliation

Reconciliation compares authoritative desired deployment state with the reconstructible runtime registry. A missing runtime is rebuilt from its immutable revision; an outdated runtime is replaced; a stopped deployment is deprovisioned; and unsupported or unresolvable state is reported as failed or degraded.

The authoritative server runs bounded reconciliation during initialization and through hosted background processing. Optimistic concurrency prevents an observation from overwriting a concurrent management change. Reconciliation never turns the Runtime boundary into the owner of Agent definitions or desired state.

See [Authoritative server components](../c4/server-components.md), [Agent execution](agent-execution.md), and [ADR-0008](../../decisions/0008-reconstructible-maf-runtime.md).
