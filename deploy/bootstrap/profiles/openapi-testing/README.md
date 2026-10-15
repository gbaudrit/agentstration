# Temporary OpenAPI testing bootstrap

This profile is a temporary bridge for testing #823 on top of #824 and #789. It is not the canonical AEP bundle installation mechanism.

Apply `openapi-testing` manually to the Workspace where the OpenAPI extension was enrolled, then select the provisioned `openapi.contract.discover` and `openapi.operation.invoke` Tools from that same enrollment. The profile installs the governed ToolSet, discovery and acquisition Flows, and Data Source Profile.

For this profile only, applying the ToolSet enables its two explicitly selected members. Other discovered AEP Tools remain disabled, and other bootstrap profiles do not inherit this behavior.

Remove this profile and its narrowly scoped activation bridge when #696 provides explicit governed installation for advertised AEP Data Source Profile bundles.
