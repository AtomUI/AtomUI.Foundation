using System.Collections.Immutable;
using AtomUI.Modularity;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace AtomUI.Foundation.Generator.Tests;

internal static class GeneratorTestHost
{
    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Preview);

    public static GeneratorRunResult RunGenerator(
        string source,
        IIncrementalGenerator generator,
        params MetadataReference[] additionalReferences)
    {
        return RunGenerator(source, generator, options: null, additionalReferences);
    }

    public static GeneratorRunResult RunGenerator(
        string source,
        IIncrementalGenerator generator,
        IReadOnlyDictionary<string, string>? options,
        params MetadataReference[] additionalReferences)
    {
        var result = RunGeneratorAllowingDiagnostics(source, generator, options, additionalReferences);
        if (result.GeneratedSources.Count == 0)
        {
            var diagnostics = string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => diagnostic.ToString()));
            throw new InvalidOperationException(
                "Generator did not produce sources."
                + Environment.NewLine
                + diagnostics);
        }

        return result;
    }

    public static GeneratorRunResult RunGeneratorAllowingDiagnostics(
        string source,
        IIncrementalGenerator generator,
        params MetadataReference[] additionalReferences)
    {
        return RunGeneratorAllowingDiagnostics(source, generator, options: null, additionalReferences);
    }

    public static GeneratorRunResult RunGeneratorAllowingDiagnostics(
        string source,
        IIncrementalGenerator generator,
        IReadOnlyDictionary<string, string>? options,
        params MetadataReference[] additionalReferences)
    {
        var compilation = CreateCompilation(source, additionalReferences);
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            new[] { generator.AsSourceGenerator() },
            optionsProvider: new DictionaryAnalyzerConfigOptionsProvider(options ?? new Dictionary<string, string>()),
            parseOptions: ParseOptions);

        driver = driver.RunGeneratorsAndUpdateCompilation(
            compilation,
            out var outputCompilation,
            out var diagnostics);

        var runResult = driver.GetRunResult();
        var exception = runResult.Results
            .Select(result => result.Exception)
            .FirstOrDefault(resultException => resultException is not null);
        if (exception is not null)
        {
            throw new InvalidOperationException("Generator execution failed.", exception);
        }

        var compilationErrors = outputCompilation.GetDiagnostics()
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ToArray();
        if (compilationErrors.Length > 0 && !runResult.Diagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
        {
            throw new InvalidOperationException(
                "Generated sources did not compile:"
                + Environment.NewLine
                + string.Join(Environment.NewLine, compilationErrors.Select(diagnostic => diagnostic.ToString())));
        }

        var allDiagnostics = diagnostics
            .AddRange(runResult.Diagnostics)
            .GroupBy(static diagnostic => (
                diagnostic.Id,
                diagnostic.Location.SourceTree?.FilePath ?? string.Empty,
                diagnostic.Location.SourceSpan.Start,
                diagnostic.Location.SourceSpan.Length,
                diagnostic.GetMessage()))
            .Select(static group => group.First())
            .ToImmutableArray();

        return new GeneratorRunResult(
            runResult.GeneratedTrees
                .ToDictionary(
                    syntaxTree => Path.GetFileName(syntaxTree.FilePath),
                    syntaxTree => syntaxTree.GetText().ToString()),
            allDiagnostics,
            outputCompilation.GetDiagnostics());
    }

    public static MetadataReference CreateReference(string source, string assemblyName)
    {
        var compilation = CreateCompilation(source, []);
        using var stream = new MemoryStream();
        var result = compilation
            .WithAssemblyName(assemblyName)
            .Emit(stream);
        if (!result.Success)
        {
            throw new InvalidOperationException(
                "Reference source did not compile:"
                + Environment.NewLine
                + string.Join(Environment.NewLine, result.Diagnostics));
        }

        return MetadataReference.CreateFromImage(stream.ToArray());
    }

    private static CSharpCompilation CreateCompilation(
        string source,
        IReadOnlyCollection<MetadataReference> additionalReferences)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(source, ParseOptions);

        return CSharpCompilation.Create(
            "ModularityGeneratorTest",
            [syntaxTree],
            CreateMetadataReferences().Concat(additionalReferences),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                .WithNullableContextOptions(NullableContextOptions.Enable));
    }

    private static IEnumerable<MetadataReference> CreateMetadataReferences()
    {
        var trustedPlatformAssemblies = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")) ?? string.Empty;

        foreach (var path in trustedPlatformAssemblies.Split(Path.PathSeparator))
        {
            if (!string.IsNullOrWhiteSpace(path))
            {
                yield return MetadataReference.CreateFromFile(path);
            }
        }

        yield return MetadataReference.CreateFromFile(typeof(IModule).Assembly.Location);
    }
}

internal sealed record GeneratorRunResult(
    IReadOnlyDictionary<string, string> GeneratedSources,
    ImmutableArray<Diagnostic> Diagnostics,
    ImmutableArray<Diagnostic> CompilationDiagnostics);

internal sealed class DictionaryAnalyzerConfigOptionsProvider(
    IReadOnlyDictionary<string, string> globalOptions)
    : AnalyzerConfigOptionsProvider
{
    private readonly DictionaryAnalyzerConfigOptions _globalOptions = new(globalOptions);

    public override AnalyzerConfigOptions GlobalOptions => _globalOptions;

    public override AnalyzerConfigOptions GetOptions(SyntaxTree tree)
    {
        return DictionaryAnalyzerConfigOptions.Empty;
    }

    public override AnalyzerConfigOptions GetOptions(AdditionalText textFile)
    {
        return DictionaryAnalyzerConfigOptions.Empty;
    }
}

internal sealed class DictionaryAnalyzerConfigOptions(
    IReadOnlyDictionary<string, string> values)
    : AnalyzerConfigOptions
{
    public static DictionaryAnalyzerConfigOptions Empty { get; } = new(new Dictionary<string, string>());

    public override bool TryGetValue(string key, out string value)
    {
        return values.TryGetValue(key, out value!);
    }
}
