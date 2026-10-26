namespace AtomUI.Modularity;

public static class ModuleDiagnosticCodes
{
    public const string DuplicateModuleId = "ATOMUIMOD001";
    public const string RequiredDependencyMissing = "ATOMUIMOD002";
    public const string DependencyCycle = "ATOMUIMOD003";
    public const string ModuleLifecycleFailed = "ATOMUIMOD007";
    public const string DiagnosticsObserverFailed = "ATOMUIMOD010";
    public const string InvalidLifecycleTransition = "ATOMUIMOD009";
}
