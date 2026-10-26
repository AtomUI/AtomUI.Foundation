# AtomUI.Foundation AI Collaboration Guidelines

## Scope Control

- Keep changes focused on the requested module or workflow.
- Do not introduce Gallery, application hosts, control packages, or tools unless requested.
- Preserve the repository documentation boundary: architecture docs under `docs/architecture/`, engineering rules under `docs/engineering/`, plans under `docs/superpowers/plans/`.
- Follow [the canonical architecture](../architecture/overview.md). Preserve neutral service registration and caller-owned instances; enable managed lifetime behavior explicitly rather than changing legacy Freeze or Getter semantics.

## Verification

- Run the narrowest useful command while iterating.
- Run `dotnet build AtomUI.Foundation.slnx` before reporting project structure or build-system work complete.
- Run `dotnet test tests/AtomUI.Foundation.Tests/AtomUI.Foundation.Tests.csproj --framework net10.0` before reporting runtime behavior complete.
- Run `dotnet test tests/AtomUI.Foundation.Generator.Tests/AtomUI.Foundation.Generator.Tests.csproj --framework net10.0` before reporting source generator work complete.
- Run `git diff --check` before reporting any file-editing work complete.
- Use [verification contracts](verification.md) for lifecycle, compatibility and packaged-consumer acceptance. A documentation update alone does not deliver the managed runtime.
