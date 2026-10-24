# Contributing

Use Windows x64, the .NET SDK selected by `global.json`, and Node.js 22 or later. The application uses the .NET Windows Desktop and ASP.NET Core shared frameworks; no external NuGet package is declared. Do not add a dependency or change the model/provider configuration as part of an unrelated fix.

```powershell
.\Build.ps1
.\Test.ps1 -SkipBuild
node scripts/public-release.cjs check
```

Tests use temporary synthetic accounts and loopback fixtures. The default suite does not launch a real desktop, log into a service, read your account vault, install a machine component, make a model request or redeem a reset credit. Native integration experiments and real-account diagnostics are deliberately outside that suite.

For a change, describe the user-visible behavior, the actual checks run and anything not verified. A selected account, open connection, successful build or simulated reply is not proof of a completed real subscription request. Do not publish identifying diagnostic output.

New source files must be added to `release/source-files.json`; the exporter does not recursively publish the working directory. Run the release-tool adversarial checks before changing its filters. Keep public documentation consistent with the executable, including experimental or unsupported functionality.

Contributions must be yours to license or supplied under compatible terms. Retain upstream copyright, license and attribution where required; add the origin and applicable notice to `THIRD_PARTY_NOTICES.md`. Do not describe copied work as independently authored. Do not include vendor binaries, extracted desktop bundles or personal account material.

Before proposing a public product release, resolve the open findings in `docs/COMPLIANCE_REVIEW.md` and update the evidence in `release/review-status.json`. Renaming a feature or accepting a disclaimer does not resolve a service-policy issue.
