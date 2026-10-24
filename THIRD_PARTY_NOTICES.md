# Third-party materials and provenance

The root MIT license covers project contributions only to the extent their contributors hold the necessary rights. It grants no rights to OpenAI's services, trademarks, native desktop application or other third-party material. MIT licensing does not establish that a particular integration is permitted by service terms.

| Material | Relationship to this source tree | Notice / source |
| --- | --- | --- |
| .NET 8, Windows Desktop and ASP.NET Core | External build/shared-runtime dependencies; no framework binary is exported in the source candidate. | [.NET license](https://github.com/dotnet/runtime/blob/main/LICENSE.TXT); retain the notices supplied with any separately redistributed runtime. |
| Codex CLI | External official executable invoked for user-driven login. A public OAuth client identifier and protocol names occur in integration code. No CLI executable/source tree is bundled in this export. | [OpenAI Codex](https://github.com/openai/codex), [Apache-2.0 license](https://github.com/openai/codex/blob/main/LICENSE). Follow the license and notices applicable to an actual version if copying its copyrightable code. |
| Native Codex / ChatGPT desktop | Separately installed proprietary application. Experimental integration copies it locally during installation and matches short resource fragments and hashes in memory. No vendor executable, extracted resource or complete bundle is exported. | [OpenAI terms](https://openai.com/policies/terms-of-use/). Native resource adaptation and local copying remain unresolved review findings; the CLI's open-source license must not be applied to the desktop. |
| MCP / MCP Apps | Implemented as protocol messages by this project; no third-party SDK package or vendored implementation is currently declared. | [Model Context Protocol](https://modelcontextprotocol.io/), [MCP Apps specification](https://github.com/modelcontextprotocol/ext-apps). |
| GitHub Actions | Optional remote CI references official `actions` projects. Actions are fetched by GitHub and are not vendored in the source archive. | Individual action licenses and versions are linked in the workflow. |

## Limits of the provenance review

The starting working directory has no Git history or complete historical import ledger. Inspection of the present tree and absence of NuGet packages cannot prove that every line was independently authored. No blanket claim of independent creation is made.

The maintainer must verify ownership and identify any earlier reused material before a public product release. Preserve the required attribution; add exact upstream versions, file mappings, licenses and notices if reuse is found. The release candidate includes native matching fragments for transparent review, not as a claim that publishing or executing those fragments has been cleared by OpenAI. See C-05 in the compliance review.
