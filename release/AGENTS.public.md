# Project instructions

This project is a Windows x64 .NET 8 account-management application and MCP plugin. Read this file as project instructions; README files describe the product rather than agent policy.

- Modify only this project. Preserve user changes and never expose a token, cookie, password, raw authentication file, account identifier or private diagnostic in tools, logs, commits or reports.
- Saved credentials and recovery data use DPAPI CurrentUser and restricted ACLs. Do not move credentials between Windows users or replace those controls with plaintext storage.
- New accounts require a human completing official login. Do not automate passwords, verification codes or login pages. Do not make paid inference requests or consume real reset credits in automated tests.
- Do not weaken native authentication, code signatures, sandboxing, permissions or TLS. Do not change models, system proxy, other plugins, desktop history or official installed application files as an unrelated repair.
- Disk authentication changes require all clients to exit normally, an encrypted backup, atomic replacement, concurrency checks and rollback. Never force-close a desktop or another task.
- Ordinary-startup integration is optional and machine-wide; it requires explicit installation consent and elevation. Individual account stores remain per Windows user. Do not create services or scheduled tasks.
- Retry is opt-in and limited to a clear quota rejection before any accepted output on a self-contained request. Preserve the model, reasoning settings and request bytes; never replay partial tool/output streams or automatically redeem reset credits.
- Reset consumption and saved-account removal require a specific account/action confirmation, retain recovery/unknown-result state and must not be executed on real data merely to test an implementation.
- Build with `Build.ps1`; validate with `Test.ps1`. Default tests must stay synthetic and loopback-only. Native integration experiments and real-account checks need separate explicit authorization and cannot establish untested compatibility.
- Add exportable files to `release/source-files.json`; never package the full working directory, account stores, generated output, vendor executables or extracted resources. Run release-tool tests and the manifest scan before creating a source candidate.
- Respect upstream license/notice requirements. Do not hide reuse or claim unsupported functionality is officially approved. Open findings in `docs/COMPLIANCE_REVIEW.md` remain release constraints until resolved with evidence.
- Report actual command exits and produced artifacts. Clearly separate real verification, synthetic tests and assumptions; never equate account selection or a successful build with an actual model request completing.
