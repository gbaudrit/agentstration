# OpenAPI installation configuration

Opt-in Instance profile for one authenticated OpenAPI installation. It declares an
Instance automatic-enrollment policy and applies a harmless example.invalid allowlist.
It is not included in the default development profile.

Start the extension independently with the existing SharedKeyFile announcement and
writable enrollment-state configuration, then select this profile in **System >
Bootstrap profiles**. For manual PairingCode, complete the intended pairing ceremony;
this profile does not bypass it. Multiple visible OpenAPI installations need an
explicit installation selector in the authored configuration and enrollment policy.

The initial preview can report the absent extension as a deferred dependency. The
application uses the normal bounded wait. Required operational contracts must be
available before the configuration resource can be applied. Repeat application keeps
operator edits; use the Instance Extensions installation-options section to change
values or retry unconfirmed activation.

See docs/reference/declarative-bootstrap.md and docs/reference/openapi-extension.md.
