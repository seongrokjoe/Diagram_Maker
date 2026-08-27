# Third-party notices

This file identifies the primary open-source components. The release SBOM and packaged upstream license texts are the authoritative complete inventory.

| Component | Purpose | License |
|---|---|---|
| .NET / ASP.NET Core | API and worker runtime | MIT |
| Microsoft.CodeAnalysis.CSharp (Roslyn) | C# syntax analysis | MIT |
| Npgsql | PostgreSQL client | PostgreSQL License |
| PostgreSQL | Persistent job/result store | PostgreSQL License |
| React / React DOM | Web UI | MIT |
| Mermaid | Diagram rendering | MIT |
| isomorphic-git | Read-only Git object access | MIT |
| jsdiff | Text hunk generation | BSD-3-Clause |
| DOMPurify (Mermaid transitive) | Sanitization | Apache-2.0 selected from dual license |
| caniuse-lite (build-time data) | Browser compatibility data | CC-BY-4.0 |

The application does not bundle PlantUML, Excalidraw, Git CLI, libgit2, Neo4j, Redis, MinIO, analytics SDKs, CDN assets, or external LLM provider SDKs.

Before distribution, attach the license texts generated from the exact lock files and have the company open-source reviewer approve the bundle.
