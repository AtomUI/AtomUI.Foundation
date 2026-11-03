# Dependency Graph

AtomUI.Foundation has two package projects and two test projects. The runtime package owns public modularity primitives; the generator package is consumed only as a Roslyn analyzer.

This is the current project graph. The design below does not create new projects, packages or dependencies. Canonical responsibilities are indexed by [the architecture overview](overview.md).

```mermaid
flowchart TD
    Runtime["AtomUI.Foundation\nruntime package"]
    Generator["AtomUI.Foundation.Generator\nRoslyn analyzer package"]
    RuntimeTests["AtomUI.Foundation.Tests"]
    GeneratorTests["AtomUI.Foundation.Generator.Tests"]

    Runtime -. analyzer .-> Generator
    Runtime --> RuntimeTests
    Runtime --> GeneratorTests
    Generator --> GeneratorTests
```

## Rules

- `AtomUI.Foundation` may reference `AtomUI.Foundation.Generator` only as an analyzer with `ReferenceOutputAssembly="false"`.
- `AtomUI.Foundation.Generator` must not reference `AtomUI.Foundation`.
- Tests may reference the runtime package and, for generator coverage, the generator package.
- Generator package dependencies stay private with `PrivateAssets="all"`.

## Runtime Logical Dependencies

The explicit managed runtime is implemented in the 1.0.2 release within the existing runtime assembly. Logical layers do not imply separate assemblies:

```mermaid
flowchart TD
    RuntimeBinding[Explicit service runtime binding] --> Modularity[Module identity and host contracts]
    RuntimeBinding --> Lifetimes[Generic owner and retirement contracts]
    Host[Host operation coordinator] --> Modules[Module Required graph and lifecycle ledger]
    Host --> Bridge[Optional managed shutdown bridge]
    Bridge --> Lifetimes
    Domain[Consumer domain adapter] --> RuntimeBinding
    Domain --> Lifetimes
    Neutral[Neutral service descriptors] --> External[Consumer-selected container]
```

- Lifetimes does not depend on Model, View, Command, document stores or platform controls. A source ModuleId may be retained without duplicating module resolution.
- The managed bridge connects real retirement tickets to the existing host. It must not introduce another ModuleHost or Required graph.
- Neutral descriptors remain exportable without activating a managed runtime. An external DI adapter must not create a second release authority for the same object.
- Domain adapters map their scopes and results to real leases; Foundation does not copy their data models or budget ledgers.
- New package boundaries and API signatures must be added to the canonical design before implementation. Microsoft DI is not a new mandatory runtime dependency.

## Module Reconfiguration in 1.0.3

The [module reconfiguration contract](module-reconfiguration.md) is implemented in 1.0.3, with local packaged-consumer evidence and separate release validation; it is absent from 1.0.2. It extends the same Host coordinator with candidate preparation, baseline validation and one publication decision for modules, service bindings and opaque domain payloads. It does not add a Host, a second Required graph, a provider authority or a package dependency.

The service runtime supplies isolated candidate bindings and preserves unaffected owners. The lifetime layer supplies generic partial retirement and real completion evidence; it does not interpret domain configuration or own the current module set. A domain adapter prepares and seals its payload, participates in recoverable admission suspension, and provides real work/reader facts through the generic protocol. Commit publication does not invoke domain callbacks under the coordinator lock.
