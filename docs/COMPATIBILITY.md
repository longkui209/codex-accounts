# Compatibility

Reviewed 2026-10-27. Application source: **0.15.5**, with subsequent source-only shortcut repair. Source preparation does not itself update an active installation; machine deployment and actual requests require separate verification.

| Area | Evidence and limit |
| --- | --- |
| Platform | Windows x64 and .NET 8 Windows Desktop/ASP.NET Core. No Linux, macOS, ARM64 or WSL claim. |
| Panel | Ten languages and synthetic MCP/DOM checks. Previous hidden-browser rendering covered all ten languages. A new host connection can be required for a newly installed plugin. |
| Native application | Earlier evidence concerned 26.1013.4894.0; the current resource-discovery and empty-profile render checks concern 26.1021.3705.0, with scalar-state conformance also checked for 26.1016.8622.0. The executable name `ChatGPT.exe` does not prove support for a separately distributed ChatGPT application. |
| Request routing | A prior HTTP 200 completion matched a selected subscription different from the unchanged desktop login. This is one recorded local completion, not universal account/model compatibility. |
| Mihomo / phone remote | The user previously confirmed paired-phone operation after a Mihomo launch. No new real remote check is performed here. |
| Quota retry | Synthetic parallel failures reproduced the old race and passed after the fix. Natural exhaustion across all live chats was not induced or certified. |
| Startup | Optional version-update service maintains verified exact-path rules. The owned legacy shortcut now resolves and verifies the current image before direct launch; its previous AppsFolder route bypassed integration on a real cold launch. After correction, the maintainer reported working operation and diagnostics observed a connected route, both adapters and HTTP 200 completion. The service was not enabled in that latest snapshot. Every packaged activation path and other Windows users were not individually tested; service registration alone does not prove routing. |
| Usage / resets | Internal endpoints can change. Stale/unknown values stay labeled; login claims do not prove current plan. No real reset is consumed by validation. |
| Publication | Complete experimental source preview excludes private/generated/vendor files and discloses findings. Cleared product release and program approval are not asserted. The maintainer separately reported submitting the program form. |

Historical private diagnostics are excluded. Do not create public evidence by uploading them or claim a new repository already has adoption history.
