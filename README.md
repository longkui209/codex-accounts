# Codex Accounts

An independent Windows account-management application and local MCP plugin for Codex.

[简体中文](README.zh-CN.md) · [Compatibility](docs/COMPATIBILITY.md) · [Security](SECURITY.md) · [Compliance review](docs/COMPLIANCE_REVIEW.md)

**Status: experimental public source preview, source 0.15.5.** The full implementation is disclosed for review, including unresolved findings. Product compliance and program approval remain unverified. This is an independent project without OpenAI approval. A successful local test does not imply supported integration or service-policy permission.

## What is in the source

- Windows-user encrypted saved profiles and recovery data, with user-driven official login.
- Subscription/usage display, optional login profile details, and ten display languages.
- Confirmed saved-account removal with undo; reset-credit information and explicit redemption confirmation.
- Experimental request routing to a selected saved subscription while retaining the native desktop login.
- Opt-in retry after an explicit quota rejection before output, preserving the request's model and settings.
- Optional machine-wide startup integration, an explicitly installed version-update service and native input adaptation discovered from the current signed client.

The last three features depend on native application behavior or non-public service interfaces and remain subject to unresolved policy, compatibility and provenance findings. They are disclosed here because they are present in the code. They must not be advertised as unlimited usage, a supported quota bypass, or an officially endorsed plugin.

## Build and isolated checks

Requirements: Windows x64, the .NET 8 SDK specified in `global.json`, and Node.js 22+ for the validation/export tools. Running the framework-dependent application requires the x64 .NET 8 Windows Desktop and ASP.NET Core shared runtimes. WSL is not required. No external NuGet package is declared.

```powershell
.\Build.ps1
.\Test.ps1 -SkipBuild
node scripts/public-release.cjs check
```

The default checks use synthetic data and local fixtures. They do not install the plugin, alter machine startup, open a real desktop, submit inference, redeem credits or validate a real account. See [contributing](CONTRIBUTING.md) for scope and [release preparation](docs/RELEASING.md) for the complete export procedure.

`Build-Plugin.ps1` produces a local plugin folder. `Install-Plugin.ps1` is a separate, explicit operation that updates the current user's plugin configuration and preserves an encrypted backup. Building and testing never install it. The native application and official CLI must be installed separately for actual login/integration; neither is bundled here.

The optional ordinary-startup component requires administrator consent, stores a verified local native copy in Program Files, and registers a machine-wide exact-path IFEO rule. It can affect all Windows users starting that path; credentials remain per user. A plugin installation by itself does not silently install this component. Do not enable experimental integration without reviewing its scope, service-policy issues and restoration procedure.

The removable update service runs as LocalSystem and handles signed installation metadata and owned startup rules, without reading account stores or chats. It verifies new versions before registration; unknown resource layouts keep native behavior. See [version updates](docs/VERSION_UPDATES.md). The latest fixes cover split composer modules, official OAuth metadata and the resident sandbox-service startup guard. A completed real request after a normal restart is still required to confirm an individual installation.

## Source review export

```powershell
node scripts/public-release.cjs test
node scripts/public-release.cjs export --purpose review
node scripts/public-release.cjs export --purpose source-preview
```

Export uses `release/source-files.json`, rejects links and sensitive material, and generates file hashes plus a sanitized report. The source candidate contains no account store, generated diagnostic, build/runtime binary or vendor application asset. `source-preview` requires separate maintainer authorization and visible disclosure. `--purpose publication` remains blocked for a cleared product release while findings are unresolved. These flags are project release boundaries, not legal authorization or publishing commands.

## Documentation

- [Architecture and trust boundaries](docs/ARCHITECTURE.md)
- [Compatibility and verification limits](docs/COMPATIBILITY.md)
- [Privacy and local data](PRIVACY.md)
- [Function-by-function compliance review](docs/COMPLIANCE_REVIEW.md)
- [Documented integration path and current implementation gaps](docs/SUPPORTED_INTEGRATION_PATH.md)
- [Open-source program preparation](docs/OPEN_SOURCE_PROGRAM.md)
- [Third-party materials and provenance](THIRD_PARTY_NOTICES.md)
- [Changes](CHANGELOG.md)

Project contributions are offered under [MIT](LICENSE), subject to the provenance limitations documented above. Third-party software and services retain their own rights and terms. No repository activity, user count, adoption or approval is asserted without evidence.
