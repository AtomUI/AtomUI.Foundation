using System.Collections.Frozen;
using AtomUI.Modularity.Services;
using AtomUI.Lifetimes;

namespace AtomUI.Modularity;

public enum ModuleChangeStatus
{
    NoChange,
    Rejected,
    StaleBaseline,
    Canceled,
    Committed
}

public sealed class ModuleChangeRequest
{
    public ModuleChangeRequest(ModuleHostView baseline, IEnumerable<ModuleRegistration> registrations,
        ModuleHostOptions options, IEnumerable<ModuleId>? disabledModules = null,
        IEnumerable<ModuleId>? replacedModules = null, bool cascade = false,
        IModuleChangeParticipant? participant = null)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(registrations);
        ArgumentNullException.ThrowIfNull(options);
        Baseline = baseline;
        Registrations = Array.AsReadOnly(registrations.ToArray());
        Options = options;
        DisabledModules = (disabledModules ?? []).ToFrozenSet();
        ReplacedModules = (replacedModules ?? []).ToFrozenSet();
        Cascade = cascade;
        Participant = participant;
    }

    public ModuleHostView Baseline { get; }
    public IReadOnlyList<ModuleRegistration> Registrations { get; }
    public ModuleHostOptions Options { get; }
    public IReadOnlySet<ModuleId> DisabledModules { get; }
    public IReadOnlySet<ModuleId> ReplacedModules { get; }
    public bool Cascade { get; }
    public IModuleChangeParticipant? Participant { get; }
}

public sealed class ModuleChangeResult
{
    internal ModuleChangeResult(ModuleChangeStatus status, ModuleHostView view,
        IEnumerable<ModuleDiagnostic>? diagnostics = null, ModuleChangeRetirement? retirement = null)
    {
        Status = status;
        View = view;
        Diagnostics = Array.AsReadOnly((diagnostics ?? []).ToArray());
        Retirement = retirement ?? new ModuleChangeRetirement(Task.FromResult(ModuleHostResult.Success));
    }

    public ModuleChangeStatus Status { get; }
    public bool IsCommitted => Status == ModuleChangeStatus.Committed;
    public ModuleHostView View { get; }
    public IReadOnlyList<ModuleDiagnostic> Diagnostics { get; }
    public ModuleChangeRetirement Retirement { get; }
}

public sealed class ModuleChangeRetirement
{
    private object? _retainedResources;
    private LifetimeRetirement? _managedRetirement;
    internal ModuleChangeRetirement(Task<ModuleHostResult> completion, object? retainedResources = null)
    {
        FinalCompletion = completion;
        _retainedResources = retainedResources;
    }
    internal void ForgetReleasedResources() => _retainedResources = null;
    internal void RetainResources(object resources) => _retainedResources = resources;
    public Task<ModuleHostResult> FinalCompletion { get; }
    public LifetimeRetirement? ManagedRetirement
    {
        get => Volatile.Read(ref _managedRetirement);
        internal set => Volatile.Write(ref _managedRetirement, value);
    }
}

public sealed class ModuleChangeContext
{
    internal ModuleChangeContext(ModuleHostView current, ModuleComposition candidate)
    {
        Current = current;
        Candidate = candidate;
    }
    internal ModuleComposition Candidate { get; }
    public ModuleHostView Current { get; }
    public IReadOnlyList<ModuleEntry> Modules => Candidate.ModuleView;
    public ModuleServiceCollection Services => Candidate.Services;
    public ModuleServiceBindings? ManagedServices => Candidate.ManagedServices;
}

public interface IModuleChangeParticipant
{
    ValueTask ConfigureAsync(ModuleChangeContext context, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    ValueTask<ModuleChangePreparation> PrepareAsync(ModuleChangeContext context, CancellationToken cancellationToken);
}

/// <summary>Owns candidate domain state and the real suspension/retirement of its predecessor.</summary>
public abstract class ModuleChangePreparation
{
    public abstract object? State { get; }
    public virtual IReadOnlyCollection<ModuleId> AffectedModules => Array.Empty<ModuleId>();
    public virtual ValueTask SuspendAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
    public virtual ValueTask ResumeAsync() => ValueTask.CompletedTask;
    public virtual ValueTask AbortAsync() => ValueTask.CompletedTask;
    public virtual void StopPrevious() { }
    public virtual ValueTask<ModuleHostResult> CompletePreviousAsync() => ValueTask.FromResult(ModuleHostResult.Success);
}
