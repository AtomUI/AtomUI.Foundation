using System.Collections.ObjectModel;

namespace AtomUI.Modularity;

public sealed partial class ModuleHost
{
    private readonly object _stateLock = new();
    private readonly AsyncLocal<CallbackFrame?> _callbackFrame = new();
    private readonly IReadOnlyList<ModuleRegistration> _registrations;
    private readonly ModuleHostOptions _options;
    private readonly List<ModuleEntry> _enabledModules = [];
    private readonly ReadOnlyCollection<ModuleEntry> _enabledModuleView;
    private readonly ModuleServiceCollection _services = new();
    private readonly HashSet<ModuleEntry> _shutdownAttempts = [];
    private IReadOnlyList<ModuleDescriptor>? _resolvedModules;
    private ModuleHostPhase _phase = ModuleHostPhase.Created;
    private Task<ModuleHostResult>? _activeOperation;
    private string? _activeStage;
    private TaskCompletionSource<ModuleHostResult>? _shutdownOperation;
    private TaskCompletionSource<ModuleHostResult>? _cleanupOperation;
    private bool _shutdownRequested;
    private bool _terminalFailure;

    public ModuleHost(IEnumerable<ModuleRegistration> registrations, ModuleHostOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(registrations);

        _registrations = Array.AsReadOnly(registrations.ToArray());
        _options = options ?? new ModuleHostOptions();
        _enabledModuleView = _enabledModules.AsReadOnly();
        Services = new ModuleServiceRegistry(_services);
    }

    public IReadOnlyList<ModuleEntry> EnabledModules => _enabledModuleView;

    public ModuleHostPhase Phase
    {
        get { lock (_stateLock) { return _phase; } }
    }

    public ModuleServiceRegistry Services { get; }

    public ValueTask<ModuleHostResult> InitializeAsync(CancellationToken cancellationToken = default)
    {
        return StartOperation("Initialize", () => InitializeCoreAsync(cancellationToken), cancellationToken, shareInitialization: true);
    }

    /// <summary>Validates module registrations and their Required graph without executing factories.</summary>
    public ModuleHostValidationResult Validate()
    {
        var result = ResolveGraph();
        var diagnostics = CreateResult(result.Diagnostics).Diagnostics;
        return diagnostics.Count == 0 ? ModuleHostValidationResult.Success : ModuleHostValidationResult.Fail(diagnostics.ToArray());
    }

    public ModuleHostResult ResolveModules()
    {
        return StartOperation("ResolveModules", () => ValueTask.FromResult(ResolveModulesCore()), rollbackOnFailure: false)
            .AsTask().GetAwaiter().GetResult();
    }

    /// <summary>
    /// Creates modules synchronously. Failure cleanup can wait for asynchronous shutdown callbacks;
    /// callers with a synchronization context should prefer <see cref="CreateModulesAsync"/>.
    /// </summary>
    public ModuleHostResult CreateModules()
    {
        return CreateModulesAsync().AsTask().GetAwaiter().GetResult();
    }

    public ValueTask<ModuleHostResult> CreateModulesAsync(CancellationToken cancellationToken = default)
    {
        return StartOperation("CreateModules", () => CreateModulesCoreAsync(cancellationToken), cancellationToken);
    }

    public ValueTask<ModuleHostResult> PreConfigureServicesAsync(CancellationToken cancellationToken = default)
    {
        return StartOperation("PreConfigureServices", () => RunServicePhaseCoreAsync(
            "PreConfigureServices", ModuleHostPhase.ModulesCreated, ModuleHostPhase.PreConfiguredServices,
            (module, context, token) => module.PreConfigureServicesAsync(context, token), cancellationToken), cancellationToken);
    }

    public ValueTask<ModuleHostResult> ConfigureServicesAsync(CancellationToken cancellationToken = default)
    {
        return StartOperation("ConfigureServices", () => RunServicePhaseCoreAsync(
            "ConfigureServices", ModuleHostPhase.PreConfiguredServices, ModuleHostPhase.ConfiguredServices,
            (module, context, token) => module.ConfigureServicesAsync(context, token), cancellationToken), cancellationToken);
    }

    public ValueTask<ModuleHostResult> PostConfigureServicesAsync(CancellationToken cancellationToken = default)
    {
        return StartOperation("PostConfigureServices", () => RunServicePhaseCoreAsync(
            "PostConfigureServices", ModuleHostPhase.ConfiguredServices, ModuleHostPhase.PostConfiguredServices,
            (module, context, token) => module.PostConfigureServicesAsync(context, token), cancellationToken), cancellationToken);
    }

    public ModuleHostResult FreezeServices()
    {
        return StartOperation("FreezeServices", () => ValueTask.FromResult(FreezeServicesCore()))
            .AsTask().GetAwaiter().GetResult();
    }

    public ValueTask<ModuleHostResult> InitializeModulesAsync(CancellationToken cancellationToken = default)
    {
        return StartOperation("InitializeModules", () => InitializeModulesCoreAsync(cancellationToken), cancellationToken);
    }

    public ValueTask<ModuleHostResult> RunModulePhaseAsync(
        ModulePhaseId phaseId,
        ModuleHostPhase requiredPhase,
        ModulePhaseExecutionOrder executionOrder,
        Func<ModulePhaseContext, CancellationToken, ValueTask> phase,
        CancellationToken cancellationToken = default,
        bool rollbackOnFailure = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(phaseId.Value);
        ArgumentNullException.ThrowIfNull(phase);
        if (!Enum.IsDefined(executionOrder))
        {
            throw new ArgumentOutOfRangeException(nameof(executionOrder), executionOrder, "Module phase execution order is not supported.");
        }

        return StartOperation(phaseId.Value,
            () => RunModulePhaseCoreAsync(phaseId.Value, requiredPhase, executionOrder, phase, cancellationToken),
            cancellationToken,
            rollbackOnFailure: rollbackOnFailure);
    }

    public ValueTask<ModuleHostResult> ShutdownAsync(CancellationToken cancellationToken = default)
    {
        if (_callbackFrame.Value?.IsActive == true)
        {
            return ValueTask.FromResult(InvalidTransition("Shutdown", "outside a host callback"));
        }

        Task<ModuleHostResult>? active = null;
        TaskCompletionSource<ModuleHostResult> completion;
        var start = false;
        lock (_stateLock)
        {
            if (_shutdownOperation is null)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return ValueTask.FromCanceled<ModuleHostResult>(cancellationToken);
                }

                _shutdownRequested = true;
                _shutdownOperation = NewCompletion();
                active = _activeOperation;
                start = true;
            }
            completion = _shutdownOperation;
        }

        if (start)
        {
            _ = ShutdownWhenIdleAsync(active, completion);
        }
        return WaitForOperation(completion.Task, cancellationToken);
    }

}
