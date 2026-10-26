namespace AtomUI.Modularity;

public sealed class ModuleHostResult
{
    private readonly IReadOnlyList<ModuleDiagnostic> _diagnostics;

    public ModuleHostResult(IEnumerable<ModuleDiagnostic>? diagnostics = null)
    {
        _diagnostics = Array.AsReadOnly((diagnostics ?? []).ToArray());
    }

    public static ModuleHostResult Success { get; } = new();

    public bool IsSuccess => !_diagnostics.Any(diagnostic => diagnostic.Severity == ModuleDiagnosticSeverity.Error);

    public IReadOnlyList<ModuleDiagnostic> Diagnostics => _diagnostics;
}
