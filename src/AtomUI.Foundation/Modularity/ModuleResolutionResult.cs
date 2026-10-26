namespace AtomUI.Modularity;

public sealed class ModuleResolutionResult
{
    private readonly IReadOnlyList<ModuleDescriptor> _modules;
    private readonly IReadOnlyList<ModuleDiagnostic> _diagnostics;

    public ModuleResolutionResult(
        IEnumerable<ModuleDescriptor>? modules = null,
        IEnumerable<ModuleDiagnostic>? diagnostics = null)
    {
        _modules = Array.AsReadOnly((modules ?? []).ToArray());
        _diagnostics = Array.AsReadOnly((diagnostics ?? []).ToArray());
    }

    public bool IsSuccess => !_diagnostics.Any(diagnostic => diagnostic.Severity == ModuleDiagnosticSeverity.Error);

    public IReadOnlyList<ModuleDescriptor> Modules => _modules;

    public IReadOnlyList<ModuleDiagnostic> Diagnostics => _diagnostics;

    public void ThrowIfFailed()
    {
        if (!IsSuccess)
        {
            throw new ModuleGraphException(_diagnostics);
        }
    }
}
