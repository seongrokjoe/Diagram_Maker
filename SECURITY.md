# Security model

## Trust boundary

Repository contents, branch names, commit metadata, diagram prompts, and LLM responses are untrusted. The application may read registered repositories but does not execute builds, Git hooks, package restore scripts, submodules, LFS downloads, or code from a repository.

Git access prefers non-interactive, read-only plumbing commands (`rev-parse`, `ls-tree`, `for-each-ref`, `show`, and `cat-file --batch`) with shell execution disabled. `GIT_OPTIONAL_LOCKS=0`, `GIT_TERMINAL_PROMPT=0`, and `GIT_NO_REPLACE_OBJECTS=1` are enforced; the worker does not clone, fetch, checkout, invoke external diff tools, or contact remotes.

## Required production controls

- Block internet egress at the host/container firewall. Permit only approved PostgreSQL, identity proxy, internal DNS and internal LLM endpoints. Repository analysis reads local mirrors and never fetches them.
- Terminate OIDC at an approved reverse proxy. Strip inbound `X-Remote-*` headers and add authenticated values at the trusted proxy only.
- Configure `Security__TrustReverseProxyHeaders=true` only behind that proxy.
- In local-only mode, bind to `127.0.0.1` and register only repositories owned by the current Windows user. Container deployments should mount repository mirrors read-only.
- Store database credentials in the company vault; never in `.env` or `appsettings.json`. The current approved LLM contract does not use credentials.
- Use internal TLS and preferably mTLS for the LLM and database.
- Run the container read-only, without privilege escalation, and with CPU/memory/process limits.
- Do not enable the development deterministic LLM stub in Production.

## Data handling

- Git source text is held only in worker memory and removed from persisted `ChangedFile` records. Pasted code workspaces follow the explicit retention policy below.
- LLM context contains structured changes and signatures, not entire repositories.
- Prompt, response, and source bodies are not written to application logs.
- Evidence uses immutable commit SHA and blob OID.
- Diagrams and graph metadata inherit repository authorization.

## Pasted code workspaces

- Saving a draft or selecting Generate stores the full pasted C/C++/C# source, descriptions, group settings and user relations for the authenticated owner. Unsaved text remains in the current UI session and is not stored in browser storage.
- Each generation retains an immutable input snapshot and hash-bound, block-relative UTF-16 evidence. Source, results, clarification answers and manual edit history remain until the owner deletes the workspace; this feature does not use the Git analysis retention interval.
- Workspace deletion removes all associated snapshots, runs and edit records. Deployments must separately apply their approved backup retention/deletion policy. LocalFile storage is intended for a single application instance; use PostgreSQL for multiple instances and shared leases.
- Every workspace/run/input/page/evidence/edit/delete endpoint checks the authenticated owner. Writes use revision compare-and-swap; cancellation, input changes and lease replacement reject stale workers. Origin checks and request limits also cover read endpoints containing code.
- Roslyn and the dedicated stdin Tree-sitter worker parse supplied text only. Includes, file paths and comments never trigger file reads, builds, restores or execution. Synthetic fragment wrappers are not real source types. Incomplete syntax and unresolved relationships are disclosed.
- Code excerpts are sent only to the configured internal LLM with existing secret masking. Code, comments, descriptions, user relations and model responses remain untrusted; structured plans must pass fact/control-flow checks and semantic review before claiming semantic completion. Prompt/source/response bodies are never logged and there is no external model fallback.
- Code-derived and user-provided relations have distinct provenance. Manual endpoint/type/label changes lose their code evidence and receive a visible user-provided label. Relation confirmation does not prove execution position or order.
## Internal-only deployment and build

External inference adapters, sample launchers and their UI have been removed. Enabling the retired CodexTest:Enabled setting is a startup error. Preserved historical ZIPs and test records predate these controls; they are not approved current deployment artifacts.

An independently loaded network policy is required before the API starts. Empty lists deny access. LLM application settings cannot widen its origin or CIDR allowlist. All resolved addresses are checked and the socket connects to those checked addresses without another DNS lookup; HTTP proxies and redirects are disabled. PostgreSQL accepts only listed literal IP/port pairs and requires VerifyFull TLS for remote hosts. DNS queries must use the approved internal resolver; host egress enforcement remains required.

Code, repositories, data, policy and build/runtime directories must be outside cloud synchronization. The application rejects OneDrive paths and environment roots, UNC paths, reparse points and paths outside LocalRoots. Other synchronization software and nonstandard sync roots require administrator review. The policy file is configuration, not an authenticated approval token: provision it with administrator-managed ACLs. Code running as a policy writer can change it, so use OS separation and host firewall rules where required.

Verification and Windows packaging use pre-provisioned locked caches only, disable npm lifecycle scripts, public audit, update notifications and CLI telemetry, and fail on missing cache content. Direct SDK/tool invocation must follow the same host policy. Synthetic regressions use isolated stores and loopback fixtures, not corporate endpoints. Browser request guards cover page requests; they do not certify browser/OS background traffic.

Release checks reject removed inference types in DLLs and UI assets, actual policy/data payloads, missing SBOMs and unreviewed license expressions. Exact dependency declarations, selected SPDX branches, integrity and shipped notices are collected. Supplemental standard terms are distinguished from shipped upstream evidence. Vulnerability feeds, real corporate LLM/DB tests, host egress capture and container OS review require separate internal evidence.

## Incident handling

Disable the affected repository, revoke its read credential, block the worker, preserve metadata-only audit logs, and rotate LLM/database credentials if exposure is suspected. Never upload source, prompts, dumps, or diagrams to public issue trackers.
