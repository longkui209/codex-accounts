# Security

This project handles subscription credentials. Current source is an **experimental review candidate**, not a security-certified product. No response-time commitment or independent penetration-test claim is made.

## Reporting

After the repository is published, use its private vulnerability reporting feature if enabled. If no private channel is available, open an issue containing only the affected version, a generic symptom and a request for a private contact. A maintainer must establish that channel before receiving sensitive evidence. Never post a token, cookie, login URL, authorization code, vault, account email, screenshot with personal data, raw request, or dump in a public issue.

Reproduce with synthetic credentials and a disposable Windows user. Do not probe another person's account, intercept unrelated traffic, exhaust a subscription, consume reset credits, or disable a platform security control to demonstrate a finding.

## Boundaries

- DPAPI uses `CurrentUser`; the vault and recovery files have restricted ACLs. This does not protect against malicious software running as the same user or an administrator.
- Official login may temporarily create a plaintext authentication file in a private, randomly named directory. The tool imports it into the encrypted vault and attempts cleanup. Cleanup failure is reported; do not claim plaintext can never exist.
- Model routing uses loopback and validates the current Windows user, process ownership and signed native callers. Loopback alone is not an authentication mechanism.
- Request retries keep bounded bytes in memory. Managed strings, crash dumps, paging and hostile same-user processes are outside a guaranteed-erasure claim.
- Reset redemption and desktop-login changes require a specific confirmation. Unknown network outcomes remain pending and must not be retried as a different operation.
- Optional ordinary-startup integration requires Windows elevation, installs into Program Files and registers an exact-path machine-wide IFEO filter. It affects every Windows user starting that path, while each user's vault stays separate.
- Native desktop adaptation is version/hash gated. Unknown versions must retain native behavior. Do not weaken Authenticode, Chromium sandboxing or other protection to regain compatibility.

## Release checks

Run `Test.ps1` and `node scripts/public-release.cjs check` in a source checkout. Export only with the explicit release manifest. An ignored file is not evidence that an archive is clean. Do not upload raw local diagnostics or test directories; use the sanitized export report.

See [privacy](PRIVACY.md), [architecture](docs/ARCHITECTURE.md) and [the compliance review](docs/COMPLIANCE_REVIEW.md). Technical test success does not establish service-policy permission.
