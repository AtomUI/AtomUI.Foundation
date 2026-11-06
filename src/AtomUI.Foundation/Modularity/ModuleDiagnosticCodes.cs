namespace AtomUI.Modularity;

/// <remarks>
/// Codes ATOMUIMOD004-006 and ATOMUIMOD008 were used by pre-1.0 prototypes and are retired; they are
/// never reused so that historical logs stay unambiguous.
/// </remarks>
public static class ModuleDiagnosticCodes
{
    public const string DuplicateModuleId = "ATOMUIMOD001";
    public const string RequiredDependencyMissing = "ATOMUIMOD002";
    public const string DependencyCycle = "ATOMUIMOD003";
    public const string ModuleLifecycleFailed = "ATOMUIMOD007";
    public const string DiagnosticsObserverFailed = "ATOMUIMOD010";
    public const string InvalidLifecycleTransition = "ATOMUIMOD009";
    public const string ModuleChangeRejected = "ATOMUICHANGE001";
}
