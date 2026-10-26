# AtomUI.Foundation Agent Guide

This file gives AI coding agents the stable entry points for working in AtomUI.Foundation. Keep this file small. Put concrete, detailed rules in focused documents under `docs/`, then link them here.

## Project Profile

AtomUI.Foundation is the foundational library repository for the AtomUI ecosystem. It contains runtime foundation infrastructure, source generator infrastructure, tests, build configuration, local workflow skills, and NuGet release automation.

```text
AtomUI.Foundation/
├── src/
│   ├── AtomUI.Foundation
│   └── AtomUI.Foundation.Generator
├── tests/
│   ├── AtomUI.Foundation.Tests
│   └── AtomUI.Foundation.Generator.Tests
├── docs/
├── build/
├── resources/
├── .agents/
└── .github/
```

## Required Reading

- Global engineering rules: [docs/global-engineering-guidelines.md](docs/global-engineering-guidelines.md)
- Architecture overview: [docs/architecture/overview.md](docs/architecture/overview.md)
- Module host contract: [docs/architecture/modularity.md](docs/architecture/modularity.md)
- Managed lifetime contract: [docs/architecture/managed-lifetimes.md](docs/architecture/managed-lifetimes.md)
- Build and packaging: [docs/architecture/build-and-packaging.md](docs/architecture/build-and-packaging.md)
- Dependency graph: [docs/architecture/dependency-graph.md](docs/architecture/dependency-graph.md)
- Agent collaboration: [docs/engineering/agent-guidelines.md](docs/engineering/agent-guidelines.md)
- Source generators: [docs/engineering/source-generator-guidelines.md](docs/engineering/source-generator-guidelines.md)
- Verification contracts: [docs/engineering/verification.md](docs/engineering/verification.md)

## Common Commands

```bash
dotnet build AtomUI.Foundation.slnx
dotnet test tests/AtomUI.Foundation.Tests/AtomUI.Foundation.Tests.csproj --framework net10.0
dotnet test tests/AtomUI.Foundation.Generator.Tests/AtomUI.Foundation.Generator.Tests.csproj --framework net10.0
git diff --check
```
