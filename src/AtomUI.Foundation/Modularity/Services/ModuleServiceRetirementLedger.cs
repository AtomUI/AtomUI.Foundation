using AtomUI.Lifetimes;

namespace AtomUI.Modularity.Services;

/// <summary>
/// The runtime's single index of service retirement work that final shutdown must still account
/// for: live (not yet finally retired) non-root scopes keyed by the identity of the bindings that
/// created them (never the bindings object, so a live scope does not root a historical view),
/// pending or failed candidate retirements, and the global shutdown batch. Successful final
/// retirement removes an entry; Pending and failed entries stay until final shutdown. Objects keep
/// their own public receipts; this ledger is not a second release authority.
/// All members except the Remove* methods are called under <see cref="ModuleServiceRuntime.Gate"/>.
/// </summary>
internal sealed class ModuleServiceRetirementLedger(object gate)
{
    private readonly HashSet<ModuleServiceScope.ScopeState> _scopes = [];
    private readonly Dictionary<object, HashSet<ModuleServiceScope.ScopeState>> _scopesByOrigin = [];
    private readonly Dictionary<LifetimeRetirement, LifetimeRetirement> _candidates = [];

    /// <summary>The global domain batch once runtime shutdown has started.</summary>
    internal LifetimeRetirement? Shutdown { get; set; }

    internal int ScopeCount => _scopes.Count;
    internal int CandidateCount => _candidates.Count;

    /// <summary>Indexes a new non-root scope under the identity of the bindings that created it.</summary>
    internal void AddScope(ModuleServiceScope.ScopeState scope)
    {
        _scopes.Add(scope);
        if (!_scopesByOrigin.TryGetValue(scope.Origin, out var scopes)) _scopesByOrigin.Add(scope.Origin, scopes = []);
        scopes.Add(scope);
        scope.Ledger = this;
    }

    /// <summary>Removes a successfully retired scope. Runs from the receipt's final bookkeeping, outside other locks.</summary>
    internal void RemoveScope(ModuleServiceScope.ScopeState scope)
    {
        lock (gate)
        {
            if (!_scopes.Remove(scope)) return;
            if (_scopesByOrigin.TryGetValue(scope.Origin, out var scopes) && scopes.Remove(scope) && scopes.Count == 0)
                _scopesByOrigin.Remove(scope.Origin);
            scope.Ledger = null;
        }
    }

    internal ModuleServiceScope.ScopeState[] ScopesOf(ModuleServiceBindings bindings) =>
        _scopesByOrigin.TryGetValue(bindings.Identity, out var scopes) ? scopes.ToArray() : [];

    internal ModuleServiceScope.ScopeState[] LiveScopes() => _scopes.ToArray();

    internal void AddCandidate(LifetimeRetirement receipt, LifetimeRetirement batch) => _candidates.Add(receipt, batch);

    /// <summary>Removes a successfully completed candidate receipt.</summary>
    internal void RemoveCandidate(LifetimeRetirement receipt)
    {
        lock (gate) _candidates.Remove(receipt);
    }

    internal KeyValuePair<LifetimeRetirement, LifetimeRetirement>[] Candidates() => _candidates.ToArray();
}
