# Contributing

Discuss substantial changes in an issue before implementation. Keep pull requests focused and preserve the documented API, ownership and dependency boundaries.

- Original contributions are offered under MPL 2.0, as described in [LICENSING.md](LICENSING.md). Retain third-party notices and disclose the provenance of copied material. Do not add the Incompatible With Secondary Licenses designation to existing covered files.
- Use the SDK in `global.json`. Run `pwsh -File .github/scripts/ci.ps1` for the Windows Debug build and headless test baseline. GPU/device/visible checks are separate and must not be added to ordinary hosted CI without a specific plan.
- Keep defaults and application policy with consumers; prefer verified published BRUTAL APIs over duplicated native/framework facilities. Preserve immutable publications, bounded scratch and predictable scaling.
- Do not commit generated outputs, private paths, credentials or diagnostic dumps. Package publishing is not automated; do not repack existing immutable versions.

CI and dependency updates do not establish image quality, GPU lifecycle, audio/controller behavior or release/licence compliance. Dependency updates require review; bindings and native APIs must not be automatically merged.
