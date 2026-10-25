# Data handling

Codex Accounts is a local Windows application and MCP plugin. It has no project-operated cloud account store or analytics collector in the reviewed source. OpenAI's native application, login service and other external services have their own policies; this statement does not describe their telemetry.

| Data | Location and use |
| --- | --- |
| Saved authentication and account labels | DPAPI `CurrentUser` vault under `%LOCALAPPDATA%\CodexAccountSwitcher\vault`; used for explicitly selected accounts. |
| Recovery, removal undo, language preference, quota/reset metadata and action state | Private local stores; sensitive vault state uses current-user encryption. Expired or unknown service results are not silently converted into a plan or quota. |
| Official login staging | A private temporary child directory of the local vault. The official CLI may write plaintext authentication until import and cleanup complete. |
| Desktop authentication | Resolved from `CODEX_HOME` or the current user's `.codex` directory. Capturing a desktop account reads this data; changing desktop login is a separate confirmed action after clients exit. |
| Model inputs and outputs | The relay handles request/response bytes and substitutes the selected saved account's authentication. Authorized requests go to fixed OpenAI HTTPS destinations. Optional retry bytes are bounded and memory-only, not a project request log. |
| Display name, username, avatar | Optional claims from that account's login. Missing fields stay hidden; an avatar can cause a request to an allowlisted external image origin. A masked email is still personal information. |
| Ordinary-startup component | Program Files runtime and machine registry registration; not a shared account vault. A native application copy can occupy several gigabytes. |
| Optional version-update service | LocalSystem service with signed installation metadata, owned exact-path startup rules and non-account lifecycle/heartbeat status. This entry does not construct a vault, read authentication or access chats. |

Quota display refreshes while the panel is visible. Calls include the selected/saved account's bearer credential and account identifier. Endpoints on the service operator's domain are not necessarily documented or supported third-party APIs; see the compliance review.

Default HTTP clients can inherit the user's configured proxy. The tool does not install a root certificate or change the system proxy. A local proxy and network operator may see connection metadata; TLS remains required for service destinations.

Removing a saved account retains an encrypted undo copy; it does not cancel the subscription, log out the remote service or necessarily remove desktop authentication. Uninstalling startup integration stops/deletes the owned version-update service, restores its owned startup registration and can retain runtime files that are in use. It does not stop the official sandbox service. Do not describe either action as secure erasure of every copy.

Never publish the local vault, configuration, authentication files, crash dumps, raw diagnostics, test fixtures generated on disk, or a whole development directory. Public source export uses an explicit allowlist and reports only filenames, rule identifiers and hashes when checking for sensitive material.
