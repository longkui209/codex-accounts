# Changelog

## Unreleased — direct startup and legacy shortcut migration

- Repair an existing owned subscription shortcut that still launches an old immutable runtime. With verified native integration installed, a self-contained command resolves the current official installation, verifies its signature and owned exact-path rule, then starts the signed image directly. No client or switcher version is embedded in the launch target.
- Correct the previous AppsFolder migration: reactivation of an existing desktop passed, but a subsequent cold launch bypassed integration. Only recognized owned legacy links are migrated; ordinary packaged activation is not claimed to be covered by this fix.
- Preserve the complete command through the Unicode shell-link interface because WScript.Shell truncates long arguments. Back up the original and verify a temporary replacement before atomically replacing the owned link.
- Plugin installation attempts this bounded migration only for the matching legacy marker and runtime under the same checkout's publication directory. The old shortcut is backed up; unrelated shortcuts and account stores are preserved.
- Reproduced the damage warning with synthetic encrypted data: 0.13.0 rejects the newer authentication metadata, while 0.15.5 reads the same unchanged vault. No real vault recovery or reset is needed for that compatibility error.
- After the correction the maintainer reported working operation; read-only diagnostics observed a connected route, both input adapters and a completed HTTP 200 request. This latest request used the same account as desktop login; it does not establish another cross-account switch or universal startup compatibility.

## 0.15.5 — service-stop transition handling

- Handle Windows error 1061 during an owned service stop only by continuing to the existing bounded SCM stopped-state check. Configuration, replacement or removal still cannot proceed until the service is confirmed stopped; no process is forced closed.
- Reproduced the running-service update failure on Windows and verified the stopped state separately. This corrects the running-to-stopped update path without changing native sandbox or account settings.

## 0.15.4 — startup daemon and authentication metadata compatibility

- Exclude the official resident sandbox provisioner from the client-exit guard only after verifying its SCM registration, LocalSystem configuration, live PID, protected Store image path and OpenAI signature. Same-name processes remain blockers.
- Accept the official optional OAuth provenance and login-owner fields. Keep API-key/unknown-field rejection and fixed official token endpoint; verify provenance against the original OAuth client and preserve it during encrypted token rotation.
- Read-only diagnostics distinguish authentication schema rejection from authentication-file access failure without returning field values or credentials.
- Added synthetic tests for daemon impersonation, malformed or foreign OAuth metadata, unchanged account identities, encrypted metadata preservation and refresh client mismatches.

## 0.15.3 — split composer compatibility and service recovery

- Follow the actual Work export into a separately bundled Codex composer. Adapt its unique `host === local` consumer and keep the shared Work definition, cloud consumers, busy checks and permission checks unchanged.
- Added runtime conformance checks for both production signal plans and the actual derived Work subscription, including cleanup and preservation of the original blocked value.
- The owned update service now records its lifecycle and latest stop control, writes a fresh heartbeat, reports unexpected termination as failure and configures Windows recovery for failures. An intentional service stop remains a stop.
- Empty-profile native diagnostics load lazy adapter modules, reject the crash boundary and save a rendered screenshot. These checks still do not prove a real account request or remote connection completed.

## 0.15.2 — native signal subscription correction

- Corrected adaptive discovery to import a scalar signal rather than a keyed signal family. Pass the subscription through the verified `onMount(set, scope)` options contract; a family without a key crashes when the native host begins watching quota state.
- Added an optional local conformance check that executes the currently installed official scoped-state implementation with synthetic values, reproduces the invalid family read and verifies signal subscribe/update/unsubscribe behavior. It reads no account data and distributes no vendor source.
- Resource hashes, identity checks, native permissions, signatures and sandbox settings remain enforced.

## 0.15.0 — adaptive Windows startup candidate

- Added an explicitly authorized removable version-update service that discovers signed installed versions and maintains separate owned exact-path startup filters. Existing clients and account stores are not stopped or changed.
- Startup status compares the actual current installation and prepared runtime instead of treating any historical registration as active. Service registration and running state are checked separately.
- Native resource plans discover current filenames and local symbols, retain source hashes and reject ambiguous/incompatible structures. Shared Work decisions, native signatures and sandbox protections remain intact.
- Added synthetic regressions for installation-path changes, protected service commands, local-consumer preservation, duplicate resources and changed response content. Actual deployment/runtime verification is recorded separately.

## Unreleased — public source preparation

- Added an MIT license for project contributions, third-party/provenance notes, privacy/security documentation and contribution guidance.
- Added a source-only export manifest, sensitive-file checks, content hashes and a publication gate tied to documented unresolved findings.
- Replaced checkout-specific paths in synthetic protocol harnesses with source-checkout discovery; kept synthetic credential and root restrictions.
- Fixed long local paths in protected authentication-file Win32 calls, with a reproduced failure and a synthetic switch/restore regression above 260 characters; existing reparse, ACL and file-identity checks remain.
- Added a repeatable Windows synthetic validation entry and CI definition.
- Rewrote the public introduction and compatibility summary to distinguish verified local behavior, simulations and unresolved policy questions.
- Documented the official OSS ChatGPT plan integration/account-picker preview and the concrete differences from this implementation; no blanket claim that third-party multi-account tools are unsupported.

Public review exports contain source only; this preparation entry does not assert a publicly released binary. Application and machine-component deployment are verified separately from source packaging.

## 0.14.0 — existing application source

- Ten display languages, a compact account panel and per-Windows-user encrypted language preferences.
- Existing features include encrypted profiles, usage/reset metadata, confirmed removal and reset actions, optional account routing/rotation and experimental ordinary-startup integration. See the review before publishing or enabling those integrations.
