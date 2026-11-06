using AtomUI.Modularity;
using AtomUI.Modularity.Generators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace AtomUI.Foundation.Generator.Tests;

/// <summary>
/// P0 red baseline for O09 of the 2026-11-05 architecture optimization proposal.
/// An edit that does not touch any module declaration must leave the catalog output
/// step cached. The current models hold ISymbol/Location instances without value
/// equality, so every compilation change regenerates the catalog.
/// </summary>
[Trait("Category", "P0Red")]
public sealed class P0RedIncrementalCacheTests
{
    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Preview);

    private const string ModuleSource = """
        using AtomUI.Modularity;

        namespace Sample;

        [Module]
        public sealed class CoreModule : ModuleBase { }

        [Module(Dependencies = [typeof(CoreModule)])]
        public sealed class FeatureModule : ModuleBase { }
        """;

    [Fact]
    public void O09_unrelated_edit_keeps_catalog_output_cached()
    {
        var modules = CSharpSyntaxTree.ParseText(ModuleSource, ParseOptions, path: "Modules.cs", cancellationToken: TestContext.Current.CancellationToken);
        var unrelated = CSharpSyntaxTree.ParseText("namespace Sample; internal static class Helper { }", ParseOptions, path: "Helper.cs", cancellationToken: TestContext.Current.CancellationToken);
        var compilation = CSharpCompilation.Create("IncrementalCacheTest", [modules, unrelated], References(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary).WithNullableContextOptions(NullableContextOptions.Enable));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new ModuleCatalogGenerator().AsSourceGenerator()],
            parseOptions: ParseOptions,
            driverOptions: new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: true));
        driver = driver.RunGenerators(compilation, TestContext.Current.CancellationToken);
        Assert.Single(driver.GetRunResult().GeneratedTrees);

        var edited = compilation.ReplaceSyntaxTree(unrelated,
            unrelated.WithChangedText(unrelated.GetText(TestContext.Current.CancellationToken).Replace(0, 0, "// unrelated edit\n")));
        driver = driver.RunGenerators(edited, TestContext.Current.CancellationToken);

        var result = Assert.Single(driver.GetRunResult().Results);
        var outputs = result.TrackedOutputSteps.SelectMany(step => step.Value)
            .SelectMany(step => step.Outputs).Select(output => output.Reason).ToArray();
        Assert.NotEmpty(outputs);
        Assert.All(outputs, reason => Assert.True(
            reason is IncrementalStepRunReason.Cached or IncrementalStepRunReason.Unchanged,
            $"Catalog output step re-ran with reason '{reason}' after an unrelated edit."));
    }

    private static IEnumerable<MetadataReference> References()
    {
        var trusted = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? string.Empty;
        foreach (var path in trusted.Split(Path.PathSeparator))
            if (!string.IsNullOrWhiteSpace(path)) yield return MetadataReference.CreateFromFile(path);
        yield return MetadataReference.CreateFromFile(typeof(IModule).Assembly.Location);
    }
}
