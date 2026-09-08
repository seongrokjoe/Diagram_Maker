# Security model

## Trust boundary

Repository contents, branch names, commit metadata, diagram prompts, and LLM responses are untrusted. The application may read registered repositories but does not execute builds, Git hooks, package restore scripts, submodules, LFS downloads, or code from a repository.

Git access prefers non-interactive, read-only plumbing commands (`rev-parse`, `ls-tree`, `for-each-ref`, `show`, and `cat-file --batch`) with shell execution disabled. `GIT_OPTIONAL_LOCKS=0`, `GIT_TERMINAL_PROMPT=0`, and `GIT_NO_REPLACE_OBJECTS=1` are enforced; the worker does not clone, fetch, checkout, invoke external diff tools, or contact remotes.

## Required production controls

- Block internet egress at the host/container firewall. Permit only the internal Git mirror, PostgreSQL, identity proxy, and internal LLM endpoints.
- Terminate OIDC at an approved reverse proxy. Strip inbound `X-Remote-*` headers and add authenticated values at the trusted proxy only.
- Configure `Security__TrustReverseProxyHeaders=true` only behind that proxy.
- In local-only mode, bind to `127.0.0.1` and register only repositories owned by the current Windows user. Container deployments should mount repository mirrors read-only.
- Store database credentials in the company vault; never in `.env` or `appsettings.json`. The current approved LLM contract does not use credentials.
- Use internal TLS and preferably mTLS for the LLM and database.
- Run the container read-only, without privilege escalation, and with CPU/memory/process limits.
- Do not enable the development deterministic LLM stub in Production.

## Data handling

- Source text is held only in worker memory and removed from persisted `ChangedFile` records.
- LLM context contains structured changes and signatures, not entire repositories.
- Prompt, response, and source bodies are not written to application logs.
- Evidence uses immutable commit SHA and blob OID.
- Diagrams and graph metadata inherit repository authorization.

## Development-only Codex sample exception

The optional Codex sample launcher is an explicit external-inference test mode, not a fallback for failed corporate LLM requests. It requires Development, a 127.0.0.1-only HTTP binding, an isolated artifacts/codex-test store, and a saved ChatGPT CLI login. It never loads or rewrites the corporate LLM policy, reads browser credentials, copies auth tokens, or accepts API keys in the application. The CLI manages its own authentication; account/workspace policies and usage limits still apply.

Only shipped synthetic scenarios and enumerated refinement/options may reach the generation service. Ordinary registration/generation/DSL endpoints are blocked in this mode, including attempts that bypass the UI. Samples are checked against the prepared commit pair and shipped source content; symlinks, junctions, Git alternates, changed working files, foreign paths and extra context are rejected. OneDrive cloud placeholders are not treated as directory links. Local manual edit revisions can be viewed/saved but are never used as Codex regeneration input.

Codex runs in an empty temporary directory with ephemeral history, read-only sandboxing, no approvals, no project instructions, and shell/browser/MCP/plugin/hook/skill-discovery capabilities disabled. Missing required CLI capabilities fail closed; managed requirements are not bypassed. Child environment variables are allowlisted; stdin carries the synthetic request, never the command line. Only final response events are accepted. CLI output/prompt bodies are not written to application logs. The adapter serializes calls and kills the process tree on cancellation/timeout; the test stop command requests graceful shutdown before terminating its own verified process tree.

Test assets and launchers are excluded from the offline production package. The offline FakeVllm executable also implements a CLI test double for regression checks; it is not a real Codex model and must not be used to claim semantic quality. Real Codex/browser quality checks are separate, explicit local actions.

## Incident handling

Disable the affected repository, revoke its read credential, block the worker, preserve metadata-only audit logs, and rotate LLM/database credentials if exposure is suspected. Never upload source, prompts, dumps, or diagrams to public issue trackers.
