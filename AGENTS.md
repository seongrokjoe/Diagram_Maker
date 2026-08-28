# Repository Guidelines

## Project Structure & Module Organization

- `src/DiagramMaker.Api/` contains the ASP.NET Core API, domain contracts, services, storage providers, configuration, and the production `wwwroot` host.
- `tests/DiagramMaker.Tests/` contains xUnit unit and integration-style tests; `tests/DiagramMaker.FakeVllm/` provides a loopback LLM test service.
- `web/` is the React + TypeScript + Vite frontend. `tools/git-worker/` is the Node.js worker used for repository operations.
- `scripts/` contains verification, local start/stop, packaging, and license tooling. `packaging/windows/` contains offline Windows launch/configuration assets.
- Keep generated output in `artifacts/`; do not commit `bin/`, `obj/`, `node_modules/`, or local runtime data under `data/`.

## Build, Test, and Development Commands

Run the complete offline-friendly validation from the repository root:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\verify.ps1
```

This restores locked .NET dependencies, runs .NET and Node tests, builds the frontend, audits npm packages, and checks the license allowlist. For local development, use `scripts\start-local.ps1` and open `http://localhost:5080`; stop with `scripts\stop-local.ps1`. Frontend-only HMR is available with `npm.cmd run dev --prefix .\web`. Build an offline Windows package with `scripts\build-offline-win-x64.ps1`.

## Coding Style & Naming Conventions

Use four-space indentation in C# and PowerShell, two spaces in JSON/YAML, and the existing TypeScript formatting. Follow standard C# PascalCase for types and public members, camelCase for locals/parameters, and descriptive service names such as `MermaidCompiler`. Use React component names in PascalCase and hooks/functions in camelCase. Keep changes focused and preserve nullable-safe, explicit error handling. Run the repository verification script before submitting changes.

## Testing Guidelines

Name C# tests by behavior, typically `ThingTests.cs`, with readable facts/theories describing the expected result. Node worker tests use `*.test.mjs` and the built-in `node --test` runner. Add regression coverage for changed parsing, storage, security, Git, or LLM behavior; update fixtures under `tests/fixtures/` when needed.

## Commit & Pull Request Guidelines

Recent commits use concise imperative summaries (for example, `Integrate internal vLLM and offline Windows package`). Keep commits focused and use the same style. Pull requests should explain the behavior change, list validation commands and results, link the relevant issue, and include screenshots or a short UI recording for frontend changes. Call out configuration, security, packaging, or migration impacts explicitly.

## Security & Configuration Tips

Never commit secrets or real LLM endpoints/credentials. Start from `.env.example` or `packaging/windows/config/llm-policy.example.json`. Preserve loopback-only local defaults, origin validation, repository ACL checks, secret masking, and the policies documented in `SECURITY.md`.
