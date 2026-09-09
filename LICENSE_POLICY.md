# Open-source license policy

Production and build dependencies are locked and reviewed before they enter the internal artifact mirror.

Automatically allowed software licenses:

- MIT
- Apache-2.0
- BSD-2-Clause / BSD-3-Clause / 0BSD
- ISC
- Zlib
- PostgreSQL License
- Unlicense or equivalent public-domain dedication

`CC-BY-4.0` is allowed only for non-code reference data and requires attribution. `caniuse-lite` is the current reviewed instance. For dual-licensed packages, the build records the permitted branch; DOMPurify is consumed under Apache-2.0.

The following are blocked unless the company open-source review owner grants a written exception:

- GPL, LGPL, AGPL and SSPL
- MPL or EPL when there is no separately selectable permissive license
- source-available or non-commercial terms
- missing or ambiguous licenses without a verified upstream license file

CI requirements:

1. Pre-provision dependencies through the approved internal intake process. Build from local locked caches and preloaded images only; missing content must fail without a public fallback.
2. Use committed lock files. Use `npm ci --offline --ignore-scripts --no-audit` for worker and frontend. Run only the repository build/test commands; dependency lifecycle scripts are disabled.
3. Run the local license checker and use an approved internal vulnerability feed separately. Do not send dependency inventories to public audit APIs.
4. Produce and archive SPDX or CycloneDX SBOMs for the deployable image.
5. Preserve upstream LICENSE/NOTICE files in the release notice bundle.
6. Require human approval for every dependency update that changes a license expression.

The `khroma@2.1.0` manifest omits its license field; its packaged `license` file was manually verified as MIT and is recorded as an explicit checker override.

Verification writes npm/NuGet inventories and a CycloneDX 1.5 SBOM under artifacts/license-audit. Windows releases include application/build npm records, runtime NuGet packs and Node versions/hash. Test dependencies are identified in verification inventories. Container OS components require a separate SBOM tied to the reviewed base image digest.

Installed manifests, LICENSE/NOTICE/THIRD-PARTY files and license-bearing READMEs/source headers are preserved. Where a permitted declaration has no text file, the inventory identifies supplemental standard terms and retains original manifest attribution. This does not invent a copyright or claim an upstream text was verified. Unknown declarations fail closed.
