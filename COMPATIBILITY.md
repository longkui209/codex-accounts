# Compatibility

Reviewed 2026-10-25. Application source: **0.14.0**. Existing local machine startup component last verified: **0.13.1**. Source preparation updates neither active installation.

| Area | Evidence and limit |
| --- | --- |
| Platform | Windows x64 and .NET 8 Windows Desktop/ASP.NET Core. No Linux, macOS, ARM64 or WSL claim. |
| Panel | Ten languages and synthetic MCP/DOM checks. Previous hidden-browser rendering covered all ten languages. A new host connection can be required for a newly installed plugin. |
| Native application | Local evidence concerns `OpenAI.Codex_26.1013.4894.0_x64`. Its executable name `ChatGPT.exe` does not prove support for a separately distributed ChatGPT application. |
| Request routing | A prior HTTP 200 completion matched a selected subscription different from the unchanged desktop login. This is one recorded local completion, not universal account/model compatibility. |
| Mihomo / phone remote | The user previously confirmed paired-phone operation after a Mihomo launch. No new real remote check is performed here. |
| Quota retry | Synthetic parallel failures reproduced the old race and passed after the fix. Natural exhaustion across all live chats was not induced or certified. |
| Startup | Exact-path registration and repeated activation were verified locally. Other Windows users were not individually tested. Updates can invalidate paths/hashes. |
| Usage / resets | Internal endpoints can change. Stale/unknown values stay labeled; login claims do not prove current plan. No real reset is consumed by validation. |
| Publication | Complete experimental source preview excludes private/generated/vendor files and discloses findings. Cleared product release and support-program submission remain on hold. |

Historical private diagnostics are excluded. Do not create public evidence by uploading them or claim a new repository already has adoption history.
