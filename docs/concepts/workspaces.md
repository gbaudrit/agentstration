# Workspaces

Workspace is the implemented isolation boundary for content and Workplace data. Every owned entity and query includes a Workspace identifier; cross-workspace reads and mutations must not return another Workspace's data.

In the Workplace vertical, a published Workspace owns the functional work context and its Dashboards. Each Dashboard independently composes namespaced published Entries with presentation roles and order. Workspace names such as `personal` do not restrict capabilities. Workspace does not currently imply a Tenant, account, permission, or authentication model.
