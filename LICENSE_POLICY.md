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

1. Restore only from approved internal NuGet/NPM/container registries.
2. Use committed lock files and `npm ci --ignore-scripts`.
3. Run `npm audit` and the repository license checker.
4. Produce and archive SPDX or CycloneDX SBOMs for the deployable image.
5. Preserve upstream LICENSE/NOTICE files in the release notice bundle.
6. Require human approval for every dependency update that changes a license expression.

The `khroma@2.1.0` manifest omits its license field; its packaged `license` file was manually verified as MIT and is recorded as an explicit checker override.
