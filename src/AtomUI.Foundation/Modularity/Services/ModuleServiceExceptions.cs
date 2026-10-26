namespace AtomUI.Modularity.Services;

public static class ModuleServiceDiagnosticCodes
{
    public const string RequiredDependencyMissing = "ATOMUISVC001";
    public const string DependencyCycle = "ATOMUISVC002";
    public const string SingletonCapturesScoped = "ATOMUISVC003";
    public const string CleanupFailed = "ATOMUISVC005";
    public const string OwnerRetainedAfterCleanupFailure = "ATOMUISVC006";
}

public sealed class ModuleServiceValidationException : InvalidOperationException
{
    public ModuleServiceValidationException(IEnumerable<ModuleDiagnostic> diagnostics)
        : this(diagnostics.ToArray()) { }

    private ModuleServiceValidationException(ModuleDiagnostic[] diagnostics)
        : base(string.Join(Environment.NewLine, diagnostics.Select(diagnostic => diagnostic.Message))) =>
        Diagnostics = Array.AsReadOnly(diagnostics);

    public IReadOnlyList<ModuleDiagnostic> Diagnostics { get; }
}

public sealed class ModuleServiceShutdownException : InvalidOperationException
{
    public ModuleServiceShutdownException(IEnumerable<ModuleDiagnostic> diagnostics)
        : base("Managed service shutdown failed; retained resources have not been released.") =>
        Diagnostics = Array.AsReadOnly(diagnostics.ToArray());

    public IReadOnlyList<ModuleDiagnostic> Diagnostics { get; }
}
