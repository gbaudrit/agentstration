# Workspaces

Workspace is the implemented isolation boundary for content and Workplace data. Every owned entity and query includes a Workspace identifier; cross-workspace reads and mutations must not return another Workspace's data.

In the Workplace vertical, a published Workspace defines the available Entries and their presentation roles. Workspace does not currently imply a Tenant, account, permission, or authentication model.
