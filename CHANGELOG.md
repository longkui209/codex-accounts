# Changelog

## Unreleased — public source preparation

- Added an MIT license for project contributions, third-party/provenance notes, privacy/security documentation and contribution guidance.
- Added a source-only export manifest, sensitive-file checks, content hashes and a publication gate tied to documented unresolved findings.
- Replaced checkout-specific paths in synthetic protocol harnesses with source-checkout discovery; kept synthetic credential and root restrictions.
- Fixed long local paths in protected authentication-file Win32 calls, with a reproduced failure and a synthetic switch/restore regression above 260 characters; existing reparse, ACL and file-identity checks remain.
- Added a repeatable Windows synthetic validation entry and CI definition.
- Rewrote the public introduction and compatibility summary to distinguish verified local behavior, simulations and unresolved policy questions.
- Documented the official OSS ChatGPT plan integration/account-picker preview and the concrete differences from this implementation; no blanket claim that third-party multi-account tools are unsupported.

These changes do not install or update the user's active plugin or machine component. Application source version remains 0.14.0. Exported review candidates contain source only; no publicly released binary version is asserted.

## 0.14.0 — existing application source

- Ten display languages, a compact account panel and per-Windows-user encrypted language preferences.
- Existing features include encrypted profiles, usage/reset metadata, confirmed removal and reset actions, optional account routing/rotation and experimental ordinary-startup integration. See the review before publishing or enabling those integrations.
