# Build And Packaging

AtomUI.Foundation uses centralized MSBuild configuration from the repository root. `Directory.Build.props` imports versioning, common project settings, package metadata, and output-path configuration from `build/`.

## Build Inputs

| File | Responsibility |
|---|---|
| `AtomUI.Foundation.slnx` | Solution entry point for runtime, generator, and tests. |
| `Directory.Packages.props` | Central NuGet package versions. |
| `build/Version.props` | Shared package version property. |
| `build/Common.props` | Target frameworks, pack defaults, and compiler settings. |
| `build/PackageMetaInfo.props` | NuGet package identity and metadata. |
| `build/Output.props` | Centralized `.artifacts/` paths for build, obj, test, and package artifacts. |

## Packages

| Package | Project | Targeting |
|---|---|---|
| `AtomUI.Foundation` | `src/AtomUI.Foundation/AtomUI.Foundation.csproj` | `net10.0` in Debug; `net10.0` and `net8.0` in Release. |
| `AtomUI.Foundation.Generator` | `src/AtomUI.Foundation.Generator/AtomUI.Foundation.Generator.csproj` | `netstandard2.0`, packaged under `analyzers/dotnet/cs`. |

## Local Commands

```bash
dotnet build AtomUI.Foundation.slnx
dotnet test tests/AtomUI.Foundation.Tests/AtomUI.Foundation.Tests.csproj --framework net10.0
dotnet test tests/AtomUI.Foundation.Generator.Tests/AtomUI.Foundation.Generator.Tests.csproj --framework net10.0
dotnet pack AtomUI.Foundation.slnx --configuration Release
```

Build outputs are stored under `.artifacts/bin/<Configuration>`, intermediate outputs under `.artifacts/<Project>/obj`, test results under `.artifacts/TestResults/<Project>`, and NuGet packages under `.artifacts/Nuget/<Configuration>`.

## Release Workflow

`.github/workflows/release-atomui-foundation.yml` builds the solution, runs runtime and generator tests, packs both NuGet packages, uploads package artifacts, and can publish to nuget.org when manually dispatched with `publish_to_nuget=true`.

The current automatic trigger is restricted to changes to that workflow file on `release/1.0`; it is not a general source-code PR validation workflow. The current test projects execute on net10.0. Release compilation of the runtime for net8.0 does not prove net8.0 execution.

## Generator Configuration

The 1.0.1 generator package does not include the catalog-property bridge. The 1.0.2 release adds the shared `buildTransitive` props bridge for `PackageReference` consumers. Consumers customizing catalog names through a source `ProjectReference` outside this repository must expose both properties through CompilerVisibleProperty, as shown in [source generation](source-generation.md#msbuild-配置桥接).

The target package contract supplies a shared props/targets bridge for PackageReference and repository ProjectReference usage. Acceptance must inspect the real analyzer config and compile the generated configured catalog; packaging only the analyzer DLL does not prove that settings reach it.

## Target Consumer Qualification

The 1.0.2 release includes the explicit managed lifetime path while preserving neutral descriptors. New assemblies or a mandatory DI dependency are not implied by the logical layers in [the dependency graph](dependency-graph.md).

Release qualification follows [verification contracts](../engineering/verification.md): run net8.0/net10.0 consumers, test fresh local NuGet artifacts, execute applicable desktop and trim/Native AOT cases, and preserve legacy descriptor/instance ownership. A successful build or pack alone does not qualify lease, retirement or failure-retention behavior.
