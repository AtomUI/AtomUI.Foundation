# Source Generator Guidelines

`AtomUI.Foundation.Generator` is packaged as a Roslyn analyzer.

The canonical behavior is defined in [module source generation](../architecture/source-generation.md). The package already implements a module catalog generator; treat symbol discovery, diagnostics and build integration as product behavior, not a project skeleton.

## Targeting

- Target `netstandard2.0`.
- Use `IsRoslynComponent=true`.
- Package the generator assembly under `analyzers/dotnet/cs`.

## Dependency Rules

- Do not reference `AtomUI.Foundation` from `AtomUI.Foundation.Generator`.
- Keep generator dependencies private with `PrivateAssets="all"`.
- Runtime projects consume the generator through analyzer references only.

## Testing

Add focused regression tests before changing generator behavior. Verify partial symbol uniqueness, type accessibility and constructibility, global type names, precise diagnostics, and compilation of the generated sources.

Validate catalog settings through real MSBuild and NuGet consumers, including CompilerVisibleProperty and the generated editorconfig. Injecting AnalyzerConfig values directly in unit tests does not verify the package configuration bridge. Full acceptance requirements are in [verification contracts](verification.md).

Run generator tests with:

```bash
dotnet test tests/AtomUI.Foundation.Generator.Tests/AtomUI.Foundation.Generator.Tests.csproj --framework net10.0
```
