using AtomUI.Modularity;
using Xunit;

namespace AtomUI.Foundation.Tests.Modularity;

public sealed class ModuleGraphDiagnosticsTests
{
    [Theory]
    [InlineData(ModuleDiagnosticSeverity.Info)]
    [InlineData(ModuleDiagnosticSeverity.Warning)]
    public void NonErrorDiagnostics_AllowSuccessfulResults(ModuleDiagnosticSeverity severity)
    {
        var diagnostic = new ModuleDiagnostic("test", "informational diagnostic", severity: severity);

        Assert.True(new ModuleHostResult([diagnostic]).IsSuccess);
        Assert.True(new ModuleResolutionResult(diagnostics: [diagnostic]).IsSuccess);
        Assert.True(ModuleHostValidationResult.Fail(diagnostic).IsSuccess);
    }

    [Fact]
    public void MissingDependency_ProvidesFullStructuredRootPath()
    {
        var result = ModuleDependencyGraph.TryResolve([
            ModuleDescriptor.For<Middle>("middle", [ModuleDependencyDescriptor.Required<Missing>()]),
            ModuleDescriptor.For<Root>("root", [ModuleDependencyDescriptor.Required<Middle>()])]);

        Assert.False(result.IsSuccess);
        Assert.Equal(new[] { ModuleId.FromModuleType<Root>(), ModuleId.FromModuleType<Middle>(), ModuleId.FromModuleType<Missing>() }, Assert.Single(result.Diagnostics).DependencyPath);
    }

    [Fact]
    public void DependencyCycle_ProvidesFullStructuredRootPath()
    {
        var result = ModuleDependencyGraph.TryResolve([
            ModuleDescriptor.For<Root>("root", [ModuleDependencyDescriptor.Required<Middle>()]),
            ModuleDescriptor.For<Middle>("middle", [ModuleDependencyDescriptor.Required<Leaf>()]),
            ModuleDescriptor.For<Leaf>("leaf", [ModuleDependencyDescriptor.Required<Middle>()])]);

        Assert.False(result.IsSuccess);
        Assert.Equal(new[] { ModuleId.FromModuleType<Root>(), ModuleId.FromModuleType<Middle>(), ModuleId.FromModuleType<Leaf>(), ModuleId.FromModuleType<Middle>() }, Assert.Single(result.Diagnostics).DependencyPath);
    }

    private sealed class Root : ModuleBase;
    private sealed class Middle : ModuleBase;
    private sealed class Leaf : ModuleBase;
    private sealed class Missing : ModuleBase;
}
