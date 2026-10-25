# Architecture and trust boundaries

| Component | Responsibility | Sensitive boundary |
| --- | --- | --- |
| `Core` | DPAPI vault, authentication snapshots, official manual login, usage/reset metadata, removal/undo and desktop-login transactions | Windows user identity, encrypted files, account-specific confirmations |
| `PluginHost` / `Plugin/ui` | Local stdio MCP server, embedded MCP Apps account panel, translations | Summaries rather than bearer tokens; app-only actions remain explicit |
| `LiveSession` | Owned native-backend connection and credential refresh | Fixed HTTPS token endpoint, current-user pipes, ownership checks |
| `ModelRouting` | Loopback relay, selected subscription, quota retry, native input and startup integration | Bound callers, account authorization, original model/body, optional machine-wide installation |
| `Validation` | Synthetic protocol/state tests; separate native diagnostics | Default runner uses the synthetic subset; real diagnostics need separate authorization |
| `scripts` / `release` | Allowlisted source candidate and privacy checks | No credentials, runtime binaries, extracted assets, fabricated history or external publication |

The user completes login using the separately installed official CLI in a private login directory. The tool imports the result into a DPAPI CurrentUser vault. The native desktop can retain account A while saved account B authorizes model requests through the relay. History, remote pairing and account-associated connections can still belong to A; request routing is not a universal login change.

The relay accepts bound native callers on loopback and forwards approved paths to fixed HTTPS origins. It preserves the requested model, reasoning setting and body. Optional retry requires an explicit quota rejection before accepted output; partial output, generic transport failures, authentication errors and non-self-contained continuations must not be replayed as if safe.

Native input adaptation reads current quota availability for the routing context and changes audited local input logic in memory. It does not alter the service's quota response or grant access. It still changes native restriction behavior and is an unresolved policy finding. Hash checks are technical controls, not permission.

The optional elevated startup component copies the locally installed signed application into protected Program Files storage and registers an exact-path machine-wide IFEO rule. Parent/child application roles use the same signed copy. Unrecognized update paths and resource hashes need fresh verification.

The component shares code, not accounts. Vaults and selection state remain per Windows user. Explicit administrator installation can add the removable version-update service described in [version updates](VERSION_UPDATES.md). Its LocalSystem entry handles installation metadata and owned exact-path rules without reading accounts or chats. Restoring startup removes that service and owned registration; active runtime files can remain to avoid interrupting a desktop. No scheduled task is installed.

The native adapter, backend endpoints, OAuth integration and startup hook are not established as supported third-party product APIs by this review. Their inclusion makes the implementation inspectable; MIT licensing and simulations do not resolve service-policy/provenance questions.
