using AtomUI.Modularity;
using AtomUI.Modularity.Generators;
using Microsoft.CodeAnalysis;
using Shouldly;
using Xunit;

namespace AtomUI.Foundation.Generator.Tests;

public class ModuleCatalogGeneratorTests
{
    [Fact]
    public void Generates_module_registrations_from_module_attributes()
    {
        var result = GeneratorTestHost.RunGenerator(ModuleSource, new ModuleCatalogGenerator());

        result.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ShouldBeEmpty();
        result.GeneratedSources.ShouldContainKey("ModuleGeneratedCatalog.g.cs");
        var source = result.GeneratedSources["ModuleGeneratedCatalog.g.cs"];
        source.ShouldContain("Demo.CoreModule");
        source.ShouldContain("Demo.SelectionModule");
        source.ShouldContain("global::AtomUI.Modularity.ModuleRegistration.For<global::Demo.SelectionModule>");
        source.ShouldContain("global::AtomUI.Modularity.ModuleDescriptor.For<global::Demo.SelectionModule>");
        source.ShouldContain("global::AtomUI.Modularity.ModuleDependencyDescriptor.Required<global::Demo.CoreModule>()");
        source.ShouldNotContain("Version.Parse");
        source.ShouldNotContain("ModuleEnablement");
        source.ShouldNotContain("OptionalIntegration");
        source.ShouldNotContain(".Conflict<");
        source.ShouldNotContain("new global::AtomUI.Modularity.ModuleId(\"Demo.SelectionModule\")");
        source.ShouldNotContain("ModuleCapabilityId");
        source.ShouldNotContain("ForGeneratedMetadata");
        source.ShouldContain("static () => new global::Demo.SelectionModule()");
    }

    [Fact]
    public void Generator_Diagnostic_Codes_Do_Not_Collide_With_Runtime_Diagnostic_Codes()
    {
        var runtimeCodes = typeof(ModuleDiagnosticCodes)
            .GetFields()
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToHashSet(StringComparer.Ordinal);
        var generatorCodes = typeof(ModuleCatalogGenerator)
            .Assembly
            .GetType("AtomUI.Modularity.Generators.Diagnostics.ModuleGeneratorDiagnostics")!
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            .Where(field => field.FieldType == typeof(DiagnosticDescriptor))
            .Select(field => ((DiagnosticDescriptor)field.GetValue(null)!).Id)
            .ToArray();

        generatorCodes.ShouldAllBe(code => code.StartsWith("ATOMUIMODGEN", StringComparison.Ordinal));
        generatorCodes.Distinct(StringComparer.Ordinal).Count().ShouldBe(generatorCodes.Length);
        generatorCodes.ShouldNotContain(code => runtimeCodes.Contains(code));
    }

    [Fact]
    public void Generates_catalog_in_configured_namespace_and_type()
    {
        var result = GeneratorTestHost.RunGenerator(
            ModuleSource,
            new ModuleCatalogGenerator(),
            options: new Dictionary<string, string>
            {
                ["build_property.AtomUIModularityCatalogNamespace"] = "Demo.Generated",
                ["build_property.AtomUIModularityCatalogTypeName"] = "CustomModuleCatalog"
            });

        result.GeneratedSources.ShouldContainKey("CustomModuleCatalog.g.cs");
        var source = result.GeneratedSources["CustomModuleCatalog.g.cs"];
        source.ShouldContain("namespace Demo.Generated;");
        source.ShouldContain("internal static partial class CustomModuleCatalog");
    }

    [Fact]
    public void Invalid_catalog_namespace_or_type_options_fall_back_to_defaults()
    {
        var result = GeneratorTestHost.RunGenerator(
            ModuleSource,
            new ModuleCatalogGenerator(),
            options: new Dictionary<string, string>
            {
                ["build_property.AtomUIModularityCatalogNamespace"] = "   ",
                ["build_property.AtomUIModularityCatalogTypeName"] = "not-valid"
            });

        result.GeneratedSources.ShouldContainKey("ModuleGeneratedCatalog.g.cs");
        result.Diagnostics.ShouldContainDiagnostic("ATOMUIMODGEN009", DiagnosticSeverity.Warning);
        var source = result.GeneratedSources["ModuleGeneratedCatalog.g.cs"];
        source.ShouldNotContain("namespace");
        source.ShouldContain("internal static partial class ModuleGeneratedCatalog");
    }

    [Fact]
    public void Csharp_keywords_in_catalog_namespace_or_type_options_fall_back_to_defaults()
    {
        var result = GeneratorTestHost.RunGenerator(
            ModuleSource,
            new ModuleCatalogGenerator(),
            options: new Dictionary<string, string>
            {
                ["build_property.AtomUIModularityCatalogNamespace"] = "Demo.class",
                ["build_property.AtomUIModularityCatalogTypeName"] = "namespace"
            });

        result.GeneratedSources.ShouldContainKey("ModuleGeneratedCatalog.g.cs");
        result.Diagnostics.Count(diagnostic => diagnostic.Id == "ATOMUIMODGEN009")
            .ShouldBe(2);
        var source = result.GeneratedSources["ModuleGeneratedCatalog.g.cs"];
        source.ShouldNotContain("namespace Demo.class;");
        source.ShouldContain("internal static partial class ModuleGeneratedCatalog");
    }

    [Fact]
    public void Reports_handwritten_serialized_module_id_usage()
    {
        var result = GeneratorTestHost.RunGeneratorAllowingDiagnostics("""
            using AtomUI.Modularity;

            namespace Demo;

            public static class ModuleIds
            {
                public static readonly ModuleId LegacySelection = ModuleId.FromSerializedValue("legacy.selection");
            }
            """, new ModuleCatalogGenerator());

        result.Diagnostics.ShouldContainDiagnostic("ATOMUIMODGEN010", DiagnosticSeverity.Warning);
    }

    [Fact]
    public void Reports_handwritten_serialized_usage_for_supported_modularity_id_types()
    {
        var result = GeneratorTestHost.RunGeneratorAllowingDiagnostics("""
            using AtomUI.Modularity;

            namespace Demo;

            public static class SerializedIds
            {
                public static readonly ModuleId Module = ModuleId.FromSerializedValue("legacy.module");
                public static readonly ModulePhaseId Phase = ModulePhaseId.FromSerializedValue("legacy.phase");
            }
            """, new ModuleCatalogGenerator());

        result.Diagnostics.Count(diagnostic => diagnostic.Id == "ATOMUIMODGEN010")
            .ShouldBe(2);
    }

    [Fact]
    public void Allows_serialized_module_id_usage_inside_explicit_boundary()
    {
        var result = GeneratorTestHost.RunGeneratorAllowingDiagnostics("""
            using AtomUI.Modularity;

            namespace Demo;

            [ModuleSerializedIdBoundary]
            internal static class ModuleIdSerializer
            {
                public static ModuleId Read(string value)
                {
                    return ModuleId.FromSerializedValue(value);
                }
            }
            """, new ModuleCatalogGenerator());

        result.Diagnostics.ShouldNotContain(diagnostic => diagnostic.Id == "ATOMUIMODGEN010");
    }

    [Fact]
    public void Allows_supported_serialized_id_usage_inside_explicit_boundary()
    {
        var result = GeneratorTestHost.RunGeneratorAllowingDiagnostics("""
            using AtomUI.Modularity;

            namespace Demo;

            [ModuleSerializedIdBoundary]
            internal static class SerializedIdSerializer
            {
                public static object[] Read(string value)
                {
                    return
                    [
                        ModuleId.FromSerializedValue(value),
                        ModulePhaseId.FromSerializedValue(value)
                    ];
                }
            }
            """, new ModuleCatalogGenerator());

        result.Diagnostics.ShouldNotContain(diagnostic => diagnostic.Id == "ATOMUIMODGEN010");
    }

    [Fact]
    public void Reports_missing_required_dependency()
    {
        var result = GeneratorTestHost.RunGeneratorAllowingDiagnostics("""
            using AtomUI.Modularity;

            namespace Demo;

            public sealed class MissingModule : ModuleBase
            {
            }

            [Module(Dependencies = [typeof(MissingModule)])]
            public sealed partial class SelectionModule : ModuleBase
            {
            }
            """, new ModuleCatalogGenerator());

        result.Diagnostics.ShouldContainDiagnostic("ATOMUIMODGEN002", DiagnosticSeverity.Error);
    }

    [Fact]
    public void Reports_dependency_cycle()
    {
        var result = GeneratorTestHost.RunGeneratorAllowingDiagnostics("""
            using AtomUI.Modularity;

            namespace Demo;

            [Module(Dependencies = [typeof(ModuleB)])]
            public sealed partial class ModuleA : ModuleBase
            {
            }

            [Module(Dependencies = [typeof(ModuleA)])]
            public sealed partial class ModuleB : ModuleBase
            {
            }
            """, new ModuleCatalogGenerator());

        result.Diagnostics.ShouldContainDiagnostic("ATOMUIMODGEN003", DiagnosticSeverity.Error);
    }

    [Fact]
    public void Generates_required_dependencies()
    {
        var result = GeneratorTestHost.RunGenerator("""
            using AtomUI.Modularity;

            namespace Demo;

            [Module]
            public sealed partial class CoreModule : ModuleBase
            {
            }

            [Module(Dependencies = [typeof(CoreModule)])]
            public sealed partial class SelectionModule : ModuleBase
            {
            }
            """, new ModuleCatalogGenerator());

        var source = result.GeneratedSources["ModuleGeneratedCatalog.g.cs"];
        source.ShouldContain("global::AtomUI.Modularity.ModuleDependencyDescriptor.Required<global::Demo.CoreModule>()");
        source.ShouldNotContain("OptionalIntegration");
        source.ShouldNotContain(".Conflict<");
        source.ShouldNotContain("Capability");
    }

    [Fact]
    public void Reports_dependency_cycle_with_multiple_dependencies()
    {
        var result = GeneratorTestHost.RunGeneratorAllowingDiagnostics("""
            using AtomUI.Modularity;

            namespace Demo;

            [Module(Dependencies = [typeof(AardvarkModule), typeof(ModuleB)])]
            public sealed partial class ModuleA : ModuleBase
            {
            }

            [Module]
            public sealed partial class AardvarkModule : ModuleBase
            {
            }

            [Module(Dependencies = [typeof(ModuleA)])]
            public sealed partial class ModuleB : ModuleBase
            {
            }
            """, new ModuleCatalogGenerator());

        result.Diagnostics.ShouldContainDiagnostic("ATOMUIMODGEN003", DiagnosticSeverity.Error);
        result.GeneratedSources.ShouldBeEmpty();
    }

    [Fact]
    public void Reports_dependson_target_without_module_attribute()
    {
        var result = GeneratorTestHost.RunGeneratorAllowingDiagnostics("""
            using AtomUI.Modularity;

            namespace Demo;

            public sealed class PlainType
            {
            }

            [Module(Dependencies = [typeof(PlainType)])]
            public sealed partial class SelectionModule : ModuleBase
            {
            }
            """, new ModuleCatalogGenerator());

        result.Diagnostics.ShouldContainDiagnostic("ATOMUIMODGEN004", DiagnosticSeverity.Error);
    }

    [Fact]
    public void Reports_module_attribute_on_type_that_does_not_implement_module()
    {
        var result = GeneratorTestHost.RunGeneratorAllowingDiagnostics("""
            using AtomUI.Modularity;

            namespace Demo;

            [Module]
            public sealed partial class SelectionModule
            {
            }
            """, new ModuleCatalogGenerator());

        result.Diagnostics.ShouldContainDiagnostic("ATOMUIMODGEN005", DiagnosticSeverity.Error);
    }

    [Fact]
    public void Reports_module_without_accessible_parameterless_constructor()
    {
        var result = GeneratorTestHost.RunGeneratorAllowingDiagnostics("""
            using AtomUI.Modularity;

            namespace Demo;

            [Module]
            public sealed partial class SelectionModule : ModuleBase
            {
                public SelectionModule(string name)
                {
                }
            }
            """, new ModuleCatalogGenerator());

        result.Diagnostics.ShouldContainDiagnostic("ATOMUIMODGEN006", DiagnosticSeverity.Error);
    }

    [Fact]
    public void Infers_module_type_from_declared_type()
    {
        var result = GeneratorTestHost.RunGenerator("""
            using AtomUI.Modularity;

            namespace Demo;

            [Module]
            public sealed partial class SelectionModule : ModuleBase
            {
            }
            """, new ModuleCatalogGenerator());

        var source = result.GeneratedSources["ModuleGeneratedCatalog.g.cs"];
        source.ShouldContain("global::AtomUI.Modularity.ModuleRegistration.For<global::Demo.SelectionModule>");
        source.ShouldContain("global::AtomUI.Modularity.ModuleDescriptor.For<global::Demo.SelectionModule>");
        source.ShouldContain("\"Selection\"");
    }

    [Fact]
    public void Generates_dependency_for_module_from_referenced_assembly()
    {
        var reference = GeneratorTestHost.CreateReference("""
            using AtomUI.Modularity;

            namespace External;

            [Module]
            public sealed partial class CoreModule : ModuleBase
            {
            }
            """, "ExternalModules");
        var result = GeneratorTestHost.RunGenerator("""
            using AtomUI.Modularity;
            using External;

            namespace Demo;

            [Module(Dependencies = [typeof(CoreModule)])]
            public sealed partial class FeatureModule : ModuleBase
            {
            }
            """, new ModuleCatalogGenerator(), reference);

        result.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ShouldBeEmpty();
        result.GeneratedSources["ModuleGeneratedCatalog.g.cs"]
            .ShouldContain("global::AtomUI.Modularity.ModuleDependencyDescriptor.Required<global::External.CoreModule>()");
    }

    private const string ModuleSource = """
        using AtomUI.Modularity;

        namespace Demo;

        [Module]
        public sealed partial class CoreModule : ModuleBase
        {
        }

        [Module(Dependencies = [typeof(CoreModule)])]
        public sealed partial class SelectionModule : ModuleBase
        {
        }
        """;
}

internal static class DiagnosticAssertions
{
    public static void ShouldContainDiagnostic(
        this IEnumerable<Diagnostic> diagnostics,
        string diagnosticId,
        DiagnosticSeverity severity)
    {
        diagnostics.ShouldContain(
            diagnostic => diagnostic.Id == diagnosticId && diagnostic.Severity == severity,
            $"Expected diagnostic {diagnosticId} with severity {severity}.");
    }
}
