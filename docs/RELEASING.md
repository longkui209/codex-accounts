# Preparing a public source release

The development directory is not the publication source. It can contain private account diagnostics, native copies and generated fixtures. **Do not initialize and push the entire development directory.**

1. Run `Build.ps1` and `Test.ps1`; inspect real exit codes and result fields.
2. Run `node scripts/public-release.cjs test` and `node scripts/public-release.cjs check`.
3. Inspect `release/source-files.json` and `release/review-status.json`.
4. Run `node scripts/public-release.cjs export --purpose review`. A new directory under `dist/public-source/` contains `source/`, file hashes and a sanitized report. Earlier exports are never overwritten or deleted.
5. Build and validate inside exported `source/` to prove private development files are unnecessary. Build outputs remain ignored. Export a fresh candidate after any change.
6. The maintainer selected a complete experimental source preview with the findings disclosed. Use `export --purpose source-preview` for that scope. It requires explicit recorded authorization and visible disclosures; it does not clear any finding or approve a support-program application.
7. For a cleared product release, resolve the policy/provenance findings with evidence and update report/status. `export --purpose publication` must pass. A risk waiver or renamed feature is not service permission.
8. Inspect the intended account's existing repository and authorization. Initialize/push only the fresh exported source. Use a genuine present-day initial commit, without invented history. Never put access tokens in remote URLs, scripts or commits.

The manifest is a per-file allowlist. `release/AGENTS.public.md` becomes root `AGENTS.md`; `docs/COMPATIBILITY.md` also supplies the concise root compatibility file. Application `.cs` sources are included; private historical reports and standalone native research/probe binaries/scripts are excluded from the default suite.

Checks cover links, file types, sensitive names, personal checkout paths, common credential formats, raw JWT-like tokens and non-fixture emails. OAuth public client identifiers and compatibility hashes are public metadata. Synthetic credentials must be explicitly identifiable as fixtures. Checks reduce accidental disclosure; they cannot prove absence of every secret or copyright concern. Inspect the exact inventory before upload.

Neither exporter nor CI publishes to GitHub, modifies profiles or submits the support-program form. Source review exports are not approved applications. Public binaries require separate distribution/signing/provenance/compatibility review; this preparation is source-only.

## Restoration and updates

Preparation does not modify installed components. An installed startup component can restore its owned exact-path registration after an explicit administrator confirmation. It does not stop active work; runtime files can remain in use. Never distribute or recursively delete a native runtime in use.

Source, plugin and machine-component versions can differ. New native paths/hashes need fresh verification, not a compatibility assumption based on a prior local run.
