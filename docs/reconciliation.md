# Reconciliation

Reconciliation compares an `AgentDeployment` desired state with the local runtime registry.

- A missing running runtime is reconstructed from its immutable revision.
- A runtime with an outdated revision is deprovisioned and replaced.
- A stopped deployment is deprovisioned and observed as stopped.
- Missing revisions or unsupported hosting modes mark the deployment failed and degraded.
- Existing matching runtimes are observed and their operational state is refreshed.

The Web host runs reconciliation at startup and periodically. `Management:ReconciliationIntervalSeconds` controls the interval. ETag updates prevent a stale reconciliation result from overwriting a concurrent management operation.
