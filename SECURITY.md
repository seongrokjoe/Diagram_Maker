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

## Incident handling

Disable the affected repository, revoke its read credential, block the worker, preserve metadata-only audit logs, and rotate LLM/database credentials if exposure is suspected. Never upload source, prompts, dumps, or diagrams to public issue trackers.
