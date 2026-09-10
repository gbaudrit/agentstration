# Reusable notification delivery

This sample keeps delivery as an ordinary Flow rather than an engine-specific notification step or a `NotificationChannel` resource.

- `flows/notification-delivery.yaml` maps a stable delivery contract to the atomic internal MCP Tool `work.notification.create`.
- `flows/news-alert-parent.yaml` calls that delivery Flow through the generic Flow card.
- `tooldefinitions/notification-send.yaml` optionally publishes the delivery Flow as `notification.send` for Agents and external MCP clients.

List the Tools in the Console once before importing the Flow so the workspace-scoped internal Tool projection is available to the designer and validator. The local implementation needs no external service or credential.

To move delivery to Slack, Teams, email, or another MCP provider, create a new `notification-delivery` draft version, replace the Tool card with that provider's ordinary Tool, explicitly map the stable input to its schema, publish, and activate the version. The parent Flow and `notification.send` ToolDefinition keep their active Flow reference and do not change. Ordinary Condition, Transform, Tool, and Failure cards can implement fan-out and fallback.

This is explicit delivery requested by a Workflow. Event-driven rules that observe run outcomes and decide whether to notify belong to #251; they can invoke the same delivery Flow.
