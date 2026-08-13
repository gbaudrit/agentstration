# Security policy

## Supported versions

Agentstration is under active `0.x` development and does not yet maintain multiple supported release lines. Security fixes are made on the latest `main` branch until a formal release-support policy is published.

## Reporting a vulnerability

Use [GitHub private vulnerability reporting](https://github.com/gbaudrit/agentstration/security/advisories/new) to report a suspected vulnerability privately to the maintainers. Include the affected component, impact, reproduction details, and any suggested mitigation. Remove production secrets, credentials, personal data, prompts, and document contents from the report whenever possible.

Do not open a public issue or disclose a critical vulnerability before maintainers have had a reasonable opportunity to investigate and coordinate a fix. Maintainers will acknowledge the report, assess its impact, and coordinate disclosure through the private advisory.

The current local-user standalone mode is not intended for untrusted public exposure. Follow the documented local-first security constraints when evaluating impact.
