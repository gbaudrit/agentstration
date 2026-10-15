# Temporary Crawl4AI testing bootstrap

This profile is a temporary bridge for testing the existing Crawl4AI AEP Data Source bundle on the #823/#824 integration stack. It is not the canonical AEP bundle installation mechanism.

Apply `crawl4ai-testing` manually to the Workspace where the Crawl4AI extension was enrolled, then bind the already provisioned `web.crawl`, `content.read`, and `content.delete` Tools from that same enrollment. The profile installs the governed ToolSet, chunk-transfer and acquisition Flows, and Data Source Profile.

This temporary profile does not enable or otherwise mutate Tool resources. Its selected Tool members must already be available under the ordinary AEP governance rules.

Remove this profile when #696 provides explicit governed installation for advertised AEP Data Source Profile bundles.
