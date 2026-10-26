# AtomUI.Foundation Global Engineering Guidelines

This document is the global engineering rules entry point for AtomUI.Foundation.

## Documentation Placement

- Keep `AGENTS.md` small and route detailed rules to `docs/`.
- Put architecture documentation under `docs/architecture/`.
- Put engineering process and coding rules under `docs/engineering/`.
- Put development plans under `docs/superpowers/plans/`.
- Do not put architecture or design documents under `docs/superpowers/`.
- Treat [the architecture overview](architecture/overview.md) and its focused design chapters as the canonical contracts. Dated proposals, assessments, audits and execution plans retain decisions and evidence; they do not override the canonical design or prove implementation status.
- Keep new public API names and package boundaries in the relevant canonical chapter before implementation. Execution plans reference those contracts instead of defining a competing API.

## Change Discipline

- Understand the affected module before changing code.
- Keep changes scoped to the requested project boundary.
- Prefer root-cause fixes over trigger-point patches.
- Preserve public API compatibility unless a breaking change is explicitly approved.
- Do not hand-edit generated files; change the generator or source input instead.

## Verification

Choose verification based on the touched area:

- Build system or project structure: `dotnet build AtomUI.Foundation.slnx`.
- Runtime library behavior: `dotnet test tests/AtomUI.Foundation.Tests/AtomUI.Foundation.Tests.csproj --framework net10.0`.
- Source generator changes: add generator tests before changing generator behavior, then run `dotnet test tests/AtomUI.Foundation.Generator.Tests/AtomUI.Foundation.Generator.Tests.csproj --framework net10.0`.
- Every code or project-file change: run `git diff --check`.
