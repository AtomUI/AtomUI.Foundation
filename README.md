# AtomUI.Foundation

[![License: LGPL-3.0](https://img.shields.io/badge/license-LGPL--3.0-white?labelColor=black&style=flat-square)](LICENSE)

Documentation Language: [English](README.md) | [简体中文](README.zh-CN.md)

## Overview

AtomUI.Foundation is the foundational infrastructure repository for the AtomUI ecosystem. It provides shared runtime primitives
and source-generation support that other AtomUI packages can build on without depending on a specific control library.

The first release focuses on modularity:

- `AtomUI.Foundation` contains module identifiers, module descriptors, dependency graph resolution, lifecycle hosting and
  module service registration primitives.
- `AtomUI.Foundation.Generator` contains Roslyn source generators and diagnostics for module catalog generation.

## Packages

| Package | Description |
|---|---|
| `AtomUI.Foundation` | Core runtime infrastructure for AtomUI. |
| `AtomUI.Foundation.Generator` | Roslyn source generators for AtomUI.Foundation. |

Current release:

```bash
dotnet add package AtomUI.Foundation --version 1.0.2
```

If your project needs generated module catalogs, reference the generator as an analyzer:

```xml
<PackageReference Include="AtomUI.Foundation.Generator" Version="1.0.2" PrivateAssets="all" />
```

## Modularity

Mark module classes with `ModuleAttribute` and derive from `ModuleBase`:

```csharp
using AtomUI.Modularity;

[Module(DisplayName = "Core")]
public sealed partial class CoreModule : ModuleBase
{
}

[Module(DisplayName = "Selection", Dependencies = [typeof(CoreModule)])]
public sealed partial class SelectionModule : ModuleBase
{
}
```

The module catalog generator emits an internal `ModuleGeneratedCatalog` type with a `CreateRegistrations()` method:

```csharp
using AtomUI.Modularity;

var registrations = ModuleGeneratedCatalog.CreateRegistrations();
var options = new ModuleHostOptionsBuilder().Enable<SelectionModule>().Build();
var host = new ModuleHost(registrations, options);
var result = await host.InitializeAsync();
```

Registration does not enable a module. Enabling `SelectionModule` also selects its required `CoreModule`; an empty enable-root set runs no modules.

The 1.0.2 Generator package forwards custom catalog settings automatically for `PackageReference` consumers. Source `ProjectReference` consumers outside this repository should import the generator's shared props file and expose the settings to the compiler:

```xml
<PropertyGroup>
    <AtomUIModularityCatalogNamespace>MyApp.Generated</AtomUIModularityCatalogNamespace>
    <AtomUIModularityCatalogTypeName>MyModuleCatalog</AtomUIModularityCatalogTypeName>
</PropertyGroup>
<ItemGroup>
    <CompilerVisibleProperty Include="AtomUIModularityCatalogNamespace" />
    <CompilerVisibleProperty Include="AtomUIModularityCatalogTypeName" />
</ItemGroup>
```

## Architecture Contracts

The [architecture overview](docs/architecture/overview.md) indexes the canonical designs for the [module host](docs/architecture/modularity.md), [service registration](docs/architecture/service-registration.md), [managed lifetimes](docs/architecture/managed-lifetimes.md), and [source generation](docs/architecture/source-generation.md).

Version 1.0.2 preserves neutral descriptors and caller-owned instances while adding an opt-in managed service runtime with typed factories, owner-backed leases, scope sharing, and coordinated retirement. Managed behavior is enabled through `ModuleServiceRuntime`; existing descriptor registration does not activate a container or transfer ownership. See [verification contracts](docs/engineering/verification.md) for the tested scope and remaining cross-platform/product qualifications.

## Local Development

Build the full solution:

```bash
dotnet build AtomUI.Foundation.slnx
```

Run runtime tests:

```bash
dotnet test tests/AtomUI.Foundation.Tests/AtomUI.Foundation.Tests.csproj --framework net10.0
```

Run generator tests:

```bash
dotnet test tests/AtomUI.Foundation.Generator.Tests/AtomUI.Foundation.Generator.Tests.csproj --framework net10.0
```

Publish packages to a local NuGet source:

```powershell
pwsh -File scripts/PublishToLocalSources.ps1
pwsh -File scripts/PublishToLocalSources.ps1 -localSourcesDir /path/to/nuget.local -buildType Release
```

## Repository Layout

| Path | Purpose |
|---|---|
| `src/AtomUI.Foundation` | Runtime package source. |
| `src/AtomUI.Foundation.Generator` | Roslyn generator package source. |
| `tests/AtomUI.Foundation.Tests` | Runtime tests. |
| `tests/AtomUI.Foundation.Generator.Tests` | Generator tests. |
| `build/` | Centralized MSBuild versioning, package metadata and output configuration. |
| `docs/` | Architecture and engineering documentation. |
| `scripts/` | Local development and publishing scripts. |

## License

AtomUI.Foundation uses the same license as AtomUI: GNU Lesser General Public License v3.0. See [LICENSE](LICENSE).
