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
