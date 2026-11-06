using System.Text;
using AtomUI.Modularity.Generators.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace AtomUI.Modularity.Generators;

/// <summary>
/// Generates the module catalog. Modeling (<c>.Modeling.cs</c>) turns declarations into value-equal
/// models; validation (<c>.Validation.cs</c>) reports diagnostics and orders modules; emission
/// (<c>.Emission.cs</c>) writes the catalog source.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed partial class ModuleCatalogGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var modules = context.SyntaxProvider
            .CreateSyntaxProvider(
                static (node, _) => node is Microsoft.CodeAnalysis.CSharp.Syntax.TypeDeclarationSyntax { AttributeLists.Count: > 0 },
                static (context, cancellationToken) => CreateModuleModel(context, cancellationToken))
            .Where(static module => module is not null)
            .Select(static (module, _) => module!);
        var serializedModuleIdUsages = context.SyntaxProvider
            .CreateSyntaxProvider(
                static (node, _) => node is Microsoft.CodeAnalysis.CSharp.Syntax.InvocationExpressionSyntax,
                static (context, cancellationToken) => CreateSerializedModuleIdDiagnostic(context, cancellationToken))
            .Where(static diagnostic => diagnostic is not null)
            .Select(static (diagnostic, _) => diagnostic!);

        var catalogOptions = context.AnalyzerConfigOptionsProvider
            .Select(static (provider, _) => ModuleCatalogOptions.From(provider.GlobalOptions));

        context.RegisterSourceOutput(
            serializedModuleIdUsages,
            static (productionContext, diagnostic) => ReportDiagnostic(productionContext, diagnostic));
        context.RegisterSourceOutput(
            modules.Collect().Select(static (items, _) => ToEquatable(items)).Combine(catalogOptions),
            static (productionContext, input) => EmitCatalog(productionContext, input.Left, input.Right));
    }

    private static void EmitCatalog(
        SourceProductionContext context,
        EquatableArray<ModuleModel> modules,
        ModuleCatalogOptions options)
    {
        foreach (var diagnostic in options.Diagnostics)
        {
            ReportDiagnostic(context, diagnostic);
        }

        // A partial type with several attributed declarations is one symbol: keep its first model.
        var moduleArray = modules
            .GroupBy(module => module.Key, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(module => module.Id, StringComparer.Ordinal)
            .ThenBy(module => module.TypeName, StringComparer.Ordinal)
            .ToArray();
        var diagnostics = ValidateModules(moduleArray);
        foreach (var diagnostic in diagnostics)
        {
            ReportDiagnostic(context, diagnostic);
        }

        if (HasErrors(diagnostics))
        {
            return;
        }

        var orderedModules = OrderModules(moduleArray);

        context.AddSource(
            $"{options.TypeName}.g.cs",
            SourceText.From(EmitModuleCatalog(orderedModules, options), Encoding.UTF8));
    }

    private static void ReportDiagnostic(SourceProductionContext context, ModuleGeneratorDiagnostic diagnostic)
    {
        context.ReportDiagnostic(diagnostic.ToDiagnostic());
    }

    private static bool HasErrors(IEnumerable<ModuleGeneratorDiagnostic> diagnostics)
    {
        return diagnostics.Any(static diagnostic => diagnostic.Descriptor.DefaultSeverity == DiagnosticSeverity.Error);
    }

    private sealed record ModuleCatalogOptions(
        string? Namespace,
        string TypeName,
        EquatableArray<ModuleGeneratorDiagnostic> Diagnostics)
    {
        private const string DefaultTypeName = "ModuleGeneratedCatalog";

        public static ModuleCatalogOptions From(AnalyzerConfigOptions options)
        {
            var diagnostics = new List<ModuleGeneratorDiagnostic>();
            var hasNamespace = options.TryGetValue(
                "build_property.AtomUIModularityCatalogNamespace",
                out var namespaceValue);
            var hasTypeName = options.TryGetValue(
                "build_property.AtomUIModularityCatalogTypeName",
                out var typeNameValue);

            string? @namespace = null;
            if (hasNamespace && !string.IsNullOrEmpty(namespaceValue))
            {
                if (!string.IsNullOrWhiteSpace(namespaceValue) && IsValidNamespace(namespaceValue))
                {
                    @namespace = namespaceValue;
                }
                else
                {
                    diagnostics.Add(new ModuleGeneratorDiagnostic(
                        ModuleGeneratorDiagnostics.InvalidCatalogOption,
                        null,
                        "AtomUIModularityCatalogNamespace",
                        namespaceValue ?? string.Empty,
                        "<global namespace>"));
                }
            }

            var typeName = DefaultTypeName;
            if (hasTypeName && !string.IsNullOrEmpty(typeNameValue))
            {
                if (IsValidIdentifier(typeNameValue))
                {
                    typeName = typeNameValue!;
                }
                else
                {
                    diagnostics.Add(new ModuleGeneratorDiagnostic(
                        ModuleGeneratorDiagnostics.InvalidCatalogOption,
                        null,
                        "AtomUIModularityCatalogTypeName",
                        typeNameValue ?? string.Empty,
                        DefaultTypeName));
                }
            }

            return new ModuleCatalogOptions(@namespace, typeName, ToEquatable(diagnostics));
        }

        private static bool IsValidNamespace(string? value)
        {
            return !string.IsNullOrWhiteSpace(value)
                && value!.Split('.').All(static part => IsValidIdentifier(part));
        }

        private static bool IsValidIdentifier(string? value)
        {
            if (string.IsNullOrWhiteSpace(value) ||
                !Microsoft.CodeAnalysis.CSharp.SyntaxFacts.IsValidIdentifier(value) ||
                Microsoft.CodeAnalysis.CSharp.SyntaxFacts.GetKeywordKind(value) != Microsoft.CodeAnalysis.CSharp.SyntaxKind.None ||
                Microsoft.CodeAnalysis.CSharp.SyntaxFacts.GetContextualKeywordKind(value) != Microsoft.CodeAnalysis.CSharp.SyntaxKind.None)
            {
                return false;
            }

            return true;
        }
    }
}
