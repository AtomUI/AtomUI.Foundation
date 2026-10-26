using AtomUI.Modularity.Generators;
using Microsoft.CodeAnalysis;
using Shouldly;
using Xunit;

namespace AtomUI.Foundation.Generator.Tests;

public class ModuleCatalogGeneratorRegressionTests
{
    [Fact]
    public void Other_attributed_partial_declarations_do_not_duplicate_a_module()
    {
        var result = GeneratorTestHost.RunGenerator("""
            using AtomUI.Modularity;
            namespace Demo;
            [Module]
            public sealed partial class FeatureModule : ModuleBase { }
            [System.Obsolete]
            public sealed partial class FeatureModule { }
            """, new ModuleCatalogGenerator());

        result.Diagnostics.ShouldBeEmpty();
        result.GeneratedSources["ModuleGeneratedCatalog.g.cs"]
            .Split("ModuleRegistration.For<", StringSplitOptions.None).Length.ShouldBe(2);
    }

    [Theory]
    [InlineData("[Module] public class FeatureModule<T> : ModuleBase { }", "FeatureModule", "generic")]
    [InlineData("public class Outer<T> { [Module] public class FeatureModule : ModuleBase { } }", "FeatureModule", "generic")]
    [InlineData("[Module] file class FeatureModule : ModuleBase { }", "FeatureModule", "file-local")]
    [InlineData("file class Outer { [Module] public class FeatureModule : ModuleBase { } }", "FeatureModule", "file-local")]
    [InlineData("public class Outer { [Module] private class FeatureModule : ModuleBase { } }", "FeatureModule", "accessible")]
    [InlineData("public class Outer { [Module] protected class FeatureModule : ModuleBase { } }", "FeatureModule", "accessible")]
    [InlineData("public class Outer { private class Inner { [Module] public class FeatureModule : ModuleBase { } } }", "FeatureModule", "accessible")]
    [InlineData("[Module] public abstract class FeatureModule : ModuleBase { }", "FeatureModule", "abstract")]
    public void Unsupported_type_shapes_report_a_precise_diagnostic_and_emit_no_factory(
        string declaration, string identifier, string reason)
    {
        var source = "using AtomUI.Modularity;\nnamespace Demo;\n" + declaration;
        var result = GeneratorTestHost.RunGeneratorAllowingDiagnostics(source, new ModuleCatalogGenerator());

        var diagnostic = result.Diagnostics.Single(diagnostic => diagnostic.Id == "ATOMUIMODGEN007");
        diagnostic.Severity.ShouldBe(DiagnosticSeverity.Error);
        diagnostic.GetMessage().ShouldContain(reason);
        source.Substring(diagnostic.Location.SourceSpan.Start, diagnostic.Location.SourceSpan.Length)
            .ShouldBe(identifier);
        result.GeneratedSources.ShouldBeEmpty();
        result.CompilationDiagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("[Module] public class FeatureModule : ModuleBase { public required string Name { get; init; } }")]
    [InlineData("public class Parent : ModuleBase { public required string Name { get; init; } } [Module] public class FeatureModule : Parent { }")]
    public void Unsatisfied_required_members_do_not_produce_an_invalid_factory(string declaration)
    {
        var source = "using AtomUI.Modularity;\nnamespace Demo;\n" + declaration;
        var result = GeneratorTestHost.RunGeneratorAllowingDiagnostics(source, new ModuleCatalogGenerator());

        var diagnostic = result.Diagnostics.Single(diagnostic => diagnostic.Id == "ATOMUIMODGEN008");
        diagnostic.GetMessage().ShouldContain("required");
        source.Substring(diagnostic.Location.SourceSpan.Start, diagnostic.Location.SourceSpan.Length)
            .ShouldBe("FeatureModule");
        result.GeneratedSources.ShouldBeEmpty();
        result.CompilationDiagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ShouldBeEmpty();
    }

    [Fact]
    public void Constructor_that_sets_required_members_is_constructible()
    {
        var result = GeneratorTestHost.RunGenerator("""
            using AtomUI.Modularity;
            namespace Demo;
            [Module]
            public class FeatureModule : ModuleBase
            {
                public required string Name { get; init; }
                [System.Diagnostics.CodeAnalysis.SetsRequiredMembers]
                public FeatureModule() { Name = "feature"; }
            }
            """, new ModuleCatalogGenerator());

        result.Diagnostics.ShouldBeEmpty();
        result.GeneratedSources["ModuleGeneratedCatalog.g.cs"]
            .ShouldContain("static () => new global::Demo.FeatureModule()");
    }

    [Fact]
    public void Global_references_survive_namespace_shadowing_and_escape_identifiers()
    {
        var result = GeneratorTestHost.RunGenerator("""
            using AtomUI.Modularity;
            namespace Demo
            {
                public class Outer
                {
                    [Module] internal class @class : ModuleBase { }
                }
                [Module(Dependencies = [typeof(Outer.@class)])]
                public class FeatureModule : ModuleBase { }
            }
            namespace Catalog { public class Demo { } }
            """, new ModuleCatalogGenerator(), new Dictionary<string, string>
            {
                ["build_property.AtomUIModularityCatalogNamespace"] = "Catalog"
            });

        result.Diagnostics.ShouldBeEmpty();
        var generated = result.GeneratedSources["ModuleGeneratedCatalog.g.cs"];
        generated.ShouldContain("new global::Demo.Outer.@class()");
        generated.ShouldContain("Required<global::Demo.Outer.@class>()");
    }

    [Fact]
    public void Open_generic_external_module_dependency_is_diagnosed_without_invalid_generated_code()
    {
        var externalModule = GeneratorTestHost.CreateReference("""
            using AtomUI.Modularity;
            namespace External;
            [Module]
            public sealed class GenericModule<T> : ModuleBase { }
            """, "External.Modules");
        var source = """
            using AtomUI.Modularity;
            namespace Consumer;
            [Module(Dependencies = [typeof(External.GenericModule<>)])]
            public sealed class FeatureModule : ModuleBase { }
            """;

        var result = GeneratorTestHost.RunGeneratorAllowingDiagnostics(
            source, new ModuleCatalogGenerator(), externalModule);

        Assert.True(result.Diagnostics.Any(diagnostic => diagnostic.Id == "ATOMUIMODGEN011"),
            string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => diagnostic.ToString())));
        result.GeneratedSources.ShouldBeEmpty();
        result.CompilationDiagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ShouldBeEmpty();
    }

    [Theory]
    [InlineData("ModuleId", "legacy.module")]
    [InlineData("ModulePhaseId", "legacy.phase")]
    public void Using_static_serialized_calls_are_diagnosed_semantically(string typeName, string value)
    {
        var source = $$"""
            using static AtomUI.Modularity.{{typeName}};
            namespace Demo;
            public static class Ids { public static readonly object Value = FromSerializedValue("{{value}}"); }
            """;
        var result = GeneratorTestHost.RunGenerator(source, new ModuleCatalogGenerator());

        var diagnostic = result.Diagnostics.Single(diagnostic => diagnostic.Id == "ATOMUIMODGEN010");
        source.Substring(diagnostic.Location.SourceSpan.Start, diagnostic.Location.SourceSpan.Length)
            .ShouldBe($"FromSerializedValue(\"{value}\")");
    }

    [Fact]
    public void Serialized_calls_through_aliases_and_static_boundaries_keep_the_same_contract()
    {
        var result = GeneratorTestHost.RunGenerator("""
            using AtomUI.Modularity;
            using Id = AtomUI.Modularity.ModuleId;
            using static AtomUI.Modularity.ModulePhaseId;
            namespace Demo;
            public static class Ids { public static readonly object Value = Id.FromSerializedValue("legacy"); }
            [ModuleSerializedIdBoundary]
            public static class Boundary { public static readonly object Value = FromSerializedValue("phase"); }
            public static class Unrelated { public static string FromSerializedValue(string value) => value; }
            public static class Caller { public static readonly object Value = Unrelated.FromSerializedValue("other"); }
            """, new ModuleCatalogGenerator());

        result.Diagnostics.Count(diagnostic => diagnostic.Id == "ATOMUIMODGEN010").ShouldBe(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Absent_or_empty_msbuild_options_use_warning_free_defaults(bool emptyOptions)
    {
        var options = emptyOptions
            ? new Dictionary<string, string>
            {
                ["build_property.AtomUIModularityCatalogNamespace"] = "",
                ["build_property.AtomUIModularityCatalogTypeName"] = ""
            }
            : new Dictionary<string, string>();
        var result = GeneratorTestHost.RunGenerator("", new ModuleCatalogGenerator(), options);

        result.Diagnostics.ShouldBeEmpty();
        var generated = result.GeneratedSources["ModuleGeneratedCatalog.g.cs"];
        generated.ShouldContain("internal static partial class ModuleGeneratedCatalog");
        generated.ShouldNotContain("namespace");
    }
}
