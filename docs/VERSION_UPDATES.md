# Windows client version updates

Introduced in application source 0.15.0. The optional, removable **CodexAccountsVersionUpdate** Windows service maintains the integration's startup registration when the official application changes. Installation requires explicit administrator consent. It runs as LocalSystem because registration and protected runtime directories are machine-wide.

## Scope

- Discovers public `OpenAI.Codex` Store installation metadata for all Windows users and checks package family, publisher, status and OpenAI Authenticode identity.
- Copies verified native files locally into a protected version directory; no native files are bundled in this project's public distribution.
- Maintains separate **exact path** IFEO filters owned by this integration. It does not install a root/global Debugger for every executable named ChatGPT.exe, change another program's rule or weaken signature/sandbox settings.
- Shares only runtime code and public installation metadata. The service entry does not construct a vault, read login/authentication, access chats, change models, use credits or launch/stop a user's desktop.
- Retries transient update discovery or copying failures while retaining the previous complete runtime. It does not remove or overwrite a runtime in use.

The monitor checks for updates periodically. A newly installed native version needs time for discovery, copying and verification; a client opened before preparation completes can require a normal reopen. A running desktop is never retroactively changed or forced closed. This is automatic version maintenance, not a guarantee that arbitrary future native behavior is compatible.

## Resource adaptation

Resource filenames and minified local symbols are discovered from the currently signed installation. Both required structures must be unique and preserve the required account/usage and local-consumer shape. The response body must still match the exact SHA-256 read during preparation. The shared Work decision remains intact; only its local input consumer is adapted. Missing, duplicate, mutated or unknown structures keep native behavior and report incompatibility.

These technical checks do not resolve the experimental integration's service-policy findings in [the compliance review](COMPLIANCE_REVIEW.md).

Source 0.15.2 uses the scalar signal factory and its verified `onMount(set, scope)` contract. A keyed signal family cannot be read as a scalar. The optional `Validation/native-signal-runtime.cjs` check executes the current official shared module with synthetic state, verifies watch/update/disposal and confirms the invalid family access is rejected. Run it with Node's `--experimental-vm-modules` against the current signed installation's `app.asar` after the read-only adapter audit. It neither starts a desktop nor reads a login. Resource-load flags alone do not establish that the UI can subscribe successfully.

Source 0.15.3 follows a separately bundled local Codex composer through its actual import of the Work state. It requires a unique explicit `host === local` consumer and verified shared signal/scope/derived exports; cloud and general ChatGPT consumers keep the original Work state. Runtime conformance includes the production signal and Work callback, not only source matching. Native diagnostics explicitly load lazy resources in the owned empty profile and save a screenshot after rejecting the crash-boundary screen.

The monitor writes a fresh ready heartbeat each cycle and records an intentional stop/shutdown control. Unexpected failures use Windows service recovery (one minute for the first two failures, then five minutes); deliberate stops are not automatically undone. Neither a configured automatic start nor an old ready file proves that a service is currently running. Older builds did not record stop controls, so a previous unexplained stop cannot be retrospectively attributed to a specific caller.

Source 0.15.4 distinguishes the official resident sandbox provisioner from an active client. The exception requires the fixed official SCM service, LocalSystem configuration, matching live PID, exact protected Store path and valid OpenAI signature; it does not ignore all processes with the same name or stop the service. Authentication parsing recognizes the optional provenance/login-owner fields documented by the official [token schema](https://github.com/openai/codex/blob/main/codex-rs/login/src/token_data.rs). Rotation retains those fields and rejects an authority or OAuth-client mismatch while keeping the fixed official token endpoint.

## Legacy subscription shortcut

An old separately created shortcut can still point at an immutable 0.13.0 runtime after the plugin and machine component have been upgraded. That parser rejects newer official authentication metadata and reports a generic vault-damage error. Compatibility must be checked before interpreting this as damaged encryption or restoring any account data.

The first shortcut migration used AppsFolder activation. A real reactivation reused an existing desktop, but a subsequent cold launch started outside integration. Reactivation evidence was insufficient to establish cold-start coverage.

`Repair-LegacyRoutingShortcut.ps1` recognizes the exact owned legacy marker, arguments and runtime under this checkout's `dist` tree, or its previously migrated owned AppsFolder entry. It checks the current signed package and owned exact-path integration, then embeds a fixed self-contained command from `NativeLaunchCommand.ps1`. On each launch the command resolves the current user's official installation, verifies the signature and matching owned rule, and starts the signed image directly. It does not pin an installed version or use AppsFolder activation. The component still verifies its protected files before opening the native desktop.

The Unicode shell-link interface retains the full command; WScript.Shell alone truncates long arguments. The script backs up the original link, verifies a temporary replacement, checks for concurrent changes, and atomically replaces only the owned link. The current user's shortcut is the only desktop artifact changed. `Install-Plugin.ps1` attempts the same migration; absent or foreign shortcuts remain untouched. No account data is rewritten.

The optional `Validation/LegacyRoutingShortcutSelfCheck.ps1` requires a locally installed verified integration and writes only owned fixture shortcuts under `validation-output`. It verifies a dry run, backup, complete version-independent command, repeated execution and preservation of a foreign shortcut. A synthetic dispatch intercepts process creation and verifies the direct signed-image target; it does not start a desktop or establish a real cold boot. It reads no account store and is not part of the default CI suite. The maintainer subsequently reported working operation and read-only diagnostics observed a connected route, both adapters and a completed HTTP 200 request. Independent ChatGPT distributions and every packaged activation path remain unverified.

## Restoration

“Restore original startup” stops and deletes only the owned update service, removes only the integration's owned filters, and retains files used by a running client. User accounts, authentication, conversations, proxies and other plugins are unaffected. Files retained for active clients must not be recursively removed until they are no longer in use.
