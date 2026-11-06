using AtomUI.Modularity;
using AtomUI.Modularity.Generators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace AtomUI.Foundation.Generator.Tests;

/// <summary>
/// P0 baseline for O09 of the 2026-11-05 architecture optimization proposal, green since P4.
/// An edit that does not touch any module declaration must leave the catalog output step
/// cached; an edit to a module declaration must regenerate it. Diagnostics keep real source
/// locations so #pragma suppression keeps working.
/// </summary>
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

    [Fact]
    public void Module_edit_regenerates_catalog_output()
    {
        var modules = CSharpSyntaxTree.ParseText(ModuleSource, ParseOptions, path: "Modules.cs", cancellationToken: TestContext.Current.CancellationToken);
        var compilation = CSharpCompilation.Create("IncrementalCacheTest", [modules], References(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary).WithNullableContextOptions(NullableContextOptions.Enable));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new ModuleCatalogGenerator().AsSourceGenerator()],
            parseOptions: ParseOptions,
            driverOptions: new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: true));
        driver = driver.RunGenerators(compilation, TestContext.Current.CancellationToken);

        var renamed = ModuleSource.Replace("public sealed class FeatureModule", "public sealed class SearchModule", StringComparison.Ordinal);
        driver = driver.RunGenerators(compilation.ReplaceSyntaxTree(modules,
            CSharpSyntaxTree.ParseText(renamed, ParseOptions, path: "Modules.cs", cancellationToken: TestContext.Current.CancellationToken)),
            TestContext.Current.CancellationToken);

        var result = Assert.Single(driver.GetRunResult().Results);
        Assert.Contains(result.TrackedOutputSteps.SelectMany(step => step.Value).SelectMany(step => step.Outputs),
            output => output.Reason == IncrementalStepRunReason.Modified);
        Assert.Contains("SearchModule", Assert.Single(result.GeneratedSources).SourceText.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Generator_diagnostics_keep_source_locations_for_pragma_suppression()
    {
        const string source = """
            using AtomUI.Modularity;

            namespace Sample;

            internal static class Usage
            {
                public static ModuleId Read() => ModuleId.FromSerializedValue("Sample/Legacy");
            }

            [Module(Dependencies = [typeof(string)])]
            public sealed class BrokenModule : ModuleBase { }
            """;
        var tree = CSharpSyntaxTree.ParseText(source, ParseOptions, path: "Usage.cs", cancellationToken: TestContext.Current.CancellationToken);
        var compilation = CSharpCompilation.Create("LocationTest", [tree], References(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary).WithNullableContextOptions(NullableContextOptions.Enable));
        var driver = CSharpGeneratorDriver.Create([new ModuleCatalogGenerator().AsSourceGenerator()], parseOptions: ParseOptions)
            .RunGenerators(compilation, TestContext.Current.CancellationToken);

        var diagnostics = driver.GetRunResult().Diagnostics;
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "ATOMUIMODGEN010");
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "ATOMUIMODGEN004");
        Assert.All(diagnostics, diagnostic =>
        {
            Assert.True(diagnostic.Location.IsInSource);
            Assert.Same(tree, diagnostic.Location.SourceTree);
        });
    }

    [Fact]
    public void Display_name_inference_has_no_product_specific_prefix()
    {
        const string source = """
            using AtomUI.Modularity;

            namespace Sample;

            [Module]
            public sealed class DataGridSelectionModule : ModuleBase { }
            """;
        var tree = CSharpSyntaxTree.ParseText(source, ParseOptions, path: "Display.cs", cancellationToken: TestContext.Current.CancellationToken);
        var compilation = CSharpCompilation.Create("DisplayTest", [tree], References(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary).WithNullableContextOptions(NullableContextOptions.Enable));
        var driver = CSharpGeneratorDriver.Create([new ModuleCatalogGenerator().AsSourceGenerator()], parseOptions: ParseOptions)
            .RunGenerators(compilation, TestContext.Current.CancellationToken);

        var generated = Assert.Single(driver.GetRunResult().GeneratedTrees).GetText(TestContext.Current.CancellationToken).ToString();
        Assert.Contains("\"Data Grid Selection\"", generated, StringComparison.Ordinal);
    }

    private static IEnumerable<MetadataReference> References()
    {
        var trusted = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? string.Empty;
        foreach (var path in trusted.Split(Path.PathSeparator))
            if (!string.IsNullOrWhiteSpace(path)) yield return MetadataReference.CreateFromFile(path);
        yield return MetadataReference.CreateFromFile(typeof(IModule).Assembly.Location);
    }
}
