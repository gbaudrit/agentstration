# Governed inventory browser fixture

This executable wraps the real Console host with `WebApplicationFactory` and Kestrel. Its Testing environment disables production background workers and telemetry; the real authentication, password policy, API, resource-family services, Bootstrap handlers, governance enforcement and isolated SQLite stores remain active. The public disposable local Development/browser fixture is `admin / Fixture753!Admin`; it is test data, not a secret, and must never be reused for an external or exposed deployment.

An in-process inspector exposes deterministic extension identity and contribution states. Startup seeds two Instance-owned registrations in namespace `qa-inventory`, their paired providers and Tools, and available enrollment inventory. The real AEP bundle provisioner installs a ToolSet, workflow and DataSourceProfile with their immutable publications. Extension version `9.0.0` and bundle version `2.4.0` intentionally differ.

`qa-managed` supplies one usable Tool, one disabled Tool, one absent contribution and a duplicate contribution conflict. `qa-degraded` retains installed evidence but is marked degraded with a fixture diagnostic. `qa-user` installs the same supported resource families without extension governance for the editability comparison. No workflow is executed and no remote AEP/Crawl4AI service is required.

The fixture is test-only: it adds no product endpoint or general management bypass. Tests must not point these seed operations at an external instance. External captures require equivalent disposable resources to be prepared explicitly.
