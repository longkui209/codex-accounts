# Validation record

Verified locally on Windows x64, 2026-10-25. A CI definition is not evidence that a remote GitHub job has run.

The default runner covers a framework build, Windows synthetic core checks, loopback routing protocol, isolated MCP, panel DOM behavior and export-tool adversarial fixtures. It does not validate real subscriptions, inference, reset redemption, machine installation, native upgrades or other Windows users.

## Actual preparation results

- Original checkout: Release build succeeded and the default synthetic suite passed.
- A clean 141-file allowlisted source export was built from scratch and passed the default suite, including 113 core checks with zero skips, loopback routing, isolated MCP and panel DOM checks.
- Release-tool adversarial checks passed: 40 cases, including links, traversal, sensitive content, non-echoing diagnostics and separate experimental-preview authorization. Formal product-publication checks still refuse unresolved findings.
- The source-content scan reported zero matches for its sensitive-content rules. This is a bounded rule-based result, not a guarantee that every possible secret is absent.
- The bilingual introduction and review documents were rendered in a hidden local browser preview. No user Chrome page or real account was used for visual review.

## Failure found and repaired

The first clean export built successfully but its core suite failed under a deep checkout path: protected authentication-file Win32 calls could not create or inspect a path beyond the legacy Windows path limit. A specific synthetic switch/restore case also reproduced the failure before the fix.

`AuthFileSafety` now normalizes and supplies an extended local path to the three native file-open/create calls for long paths. ACL, reparse, file-ID, concurrency and atomic-replacement checks remain. The new regression switches and restores only synthetic authentication above 260 characters, checks byte equality and encrypted backups, and checks that no temporary authentication remains. The clean export subsequently passed. [Windows API path documentation](https://learn.microsoft.com/en-ca/windows/win32/fileio/maximum-file-path-limitation)

Some restricted-sandbox runs refused creating test links or inspecting protected handles; those were recorded as failed attempts, not counted as passes. The successful suite used the approved local synthetic-test execution scope. No real account or startup installation was used to work around a test failure.

Portability evidence covers the exported source checkout, not every Windows installation or future native application update. Raw fixtures/results are excluded from publication.
