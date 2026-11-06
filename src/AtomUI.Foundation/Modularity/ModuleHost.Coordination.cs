namespace AtomUI.Modularity;

public sealed partial class ModuleHost
{
    private ValueTask<ModuleHostResult> StartOperation(
        string stage,
        Func<ValueTask<ModuleHostResult>> action,
        CancellationToken operationCancellation = default,
        bool shareInitialization = false,
        bool rollbackOnFailure = true)
    {
        TaskCompletionSource<ModuleHostResult>? completion = null;
        Task<ModuleHostResult>? shared = null;
        string? rejection = null;
        lock (_stateLock)
        {
            if (_callbackFrame.Value?.IsActive == true)
            {
                rejection = "outside a host callback";
            }
            else if (_shutdownRequested || _phase is ModuleHostPhase.Failed or ModuleHostPhase.Shutdown)
            {
                rejection = "a non-terminal host with no shutdown request";
            }
            else if (_activeOperation is not null)
            {
                if (shareInitialization && _activeStage == "Initialize")
                {
                    shared = _activeOperation;
                }
                else
                {
                    rejection = $"no other running operation (currently '{_activeStage}')";
                }
            }
            else
            {
                completion = NewCompletion();
                _activeOperation = completion.Task;
                _activeStage = stage;
            }
        }

        if (rejection is not null)
        {
            return ValueTask.FromResult(InvalidTransition(stage, rejection));
        }
        if (shared is not null)
        {
            return WaitForOperation(shared, operationCancellation);
        }

        _ = CompleteOperationAsync(stage, action, operationCancellation, rollbackOnFailure, completion!);
        // The initiating token controls the operation itself. Only joining callers cancel their wait.
        return new ValueTask<ModuleHostResult>(completion!.Task);
    }

    private async Task CompleteOperationAsync(
        string stage,
        Func<ValueTask<ModuleHostResult>> action,
        CancellationToken operationCancellation,
        bool rollbackOnFailure,
        TaskCompletionSource<ModuleHostResult> completion)
    {
        ModuleHostResult? result = null;
        OperationCanceledException? cancellation = null;
        try
        {
            result = await action().ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (operationCancellation.IsCancellationRequested)
        {
            if (rollbackOnFailure)
            {
                await RollBackAsync().ConfigureAwait(false);
            }
            cancellation = exception;
        }
        catch (Exception exception)
        {
            var serviceDiagnostics = FindServiceValidationDiagnostics(exception);
            result = CreateResult(serviceDiagnostics.Count == 0
                ? [FailureDiagnostic(exception, stage)]
                : serviceDiagnostics);
            if (rollbackOnFailure)
            {
                var cleanup = await RollBackAsync().ConfigureAwait(false);
                if (cleanup.Diagnostics.Count != 0)
                {
                    result = new ModuleHostResult(result.Diagnostics.Concat(cleanup.Diagnostics));
                }
            }
        }

        lock (_stateLock)
        {
            _activeOperation = null;
            _activeStage = null;
        }
        if (cancellation is not null)
        {
            completion.TrySetCanceled(cancellation.CancellationToken);
        }
        else
        {
            completion.TrySetResult(result!);
        }
    }

    private async Task<ModuleHostResult> RollBackAsync()
    {
        TaskCompletionSource<ModuleHostResult> shutdown;
        lock (_stateLock)
        {
            _terminalFailure = true;
            _shutdownRequested = true;
            shutdown = _shutdownOperation ??= NewCompletion();
        }
        var result = await EnsureCleanupAsync().ConfigureAwait(false);
        shutdown.TrySetResult(result);
        return result;
    }

    private async Task ShutdownWhenIdleAsync(Task<ModuleHostResult>? active, TaskCompletionSource<ModuleHostResult> completion)
    {
        if (active is not null)
        {
            try { await active.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        completion.TrySetResult(await EnsureCleanupAsync().ConfigureAwait(false));
    }

    private Task<ModuleHostResult> EnsureCleanupAsync()
    {
        TaskCompletionSource<ModuleHostResult> completion;
        var start = false;
        lock (_stateLock)
        {
            if (_cleanupOperation is null)
            {
                _cleanupOperation = NewCompletion();
                start = true;
            }
            completion = _cleanupOperation;
        }
        if (start) { _ = CompleteCleanupAsync(completion); }
        return completion.Task;
    }

    private async Task CompleteCleanupAsync(TaskCompletionSource<ModuleHostResult> completion)
    {
        // Final shutdown retires the current generation through the same scheduler as a committed
        // change: Stop, then domain completion alongside the module drain, then managed services.
        var diagnostics = new List<ModuleDiagnostic>();
        diagnostics.AddRange((await RunRetirementAsync(CurrentRetirementPlan(), receipt: null).ConfigureAwait(false)).Diagnostics);

        Task<ModuleHostResult>[] retired;
        lock (_stateLock) retired = _retirements.Select(retirement => retirement.FinalCompletion).ToArray();
        foreach (var result in await Task.WhenAll(retired).ConfigureAwait(false)) diagnostics.AddRange(result.Diagnostics);

        var finalResult = CreateResult(diagnostics);
        lock (_stateLock) { _phase = _terminalFailure ? ModuleHostPhase.Failed : ModuleHostPhase.Shutdown; }
        completion.TrySetResult(finalResult);
    }

    private async Task<ModuleHostResult> CleanupEntriesAsync(IEnumerable<ModuleEntry> entries, ModuleServiceRegistry services)
    {
        var ordered = entries.ToArray();
        var dependents = ordered.ToDictionary(entry => entry.Descriptor.Id, _ => new List<ModuleEntry>());
        foreach (var entry in ordered)
            foreach (var dependency in entry.Descriptor.Dependencies)
                if (dependents.TryGetValue(dependency.ModuleId, out var consumers)) consumers.Add(entry);

        // Schedule the whole set. A reader of one independent module may only be
        // returned by another module's Shutdown. Required consumers still finish
        // their callbacks before the corresponding dependency starts Shutdown.
        var operations = new Dictionary<ModuleEntry, Task<ModuleHostResult>>();
        foreach (var entry in Enumerable.Reverse(ordered))
        {
            var prerequisites = Task.WhenAll(dependents[entry.Descriptor.Id].Select(consumer => operations[consumer]));
            operations.Add(entry, ShutdownEntryAsync(entry, services, prerequisites));
        }
        var diagnostics = new List<ModuleDiagnostic>();
        foreach (var result in await Task.WhenAll(operations.Values).ConfigureAwait(false)) diagnostics.AddRange(result.Diagnostics);
        return new ModuleHostResult(diagnostics);
    }

    private Task<ModuleHostResult> ShutdownEntryAsync(ModuleEntry entry, ModuleServiceRegistry services, Task prerequisites)
    {
        TaskCompletionSource<ModuleHostResult> completion;
        Task readers;
        lock (_stateLock)
        {
            if (entry.ShutdownOperation is not null) return entry.ShutdownOperation;
            completion = NewCompletion();
            entry.ShutdownOperation = completion.Task;
            entry.Closing = true;
            readers = entry.Readers == 0 ? Task.CompletedTask : entry.ReadersExited!.Task;
        }
        _ = CompleteEntryShutdownAsync(entry, services, Task.WhenAll(readers, prerequisites), completion);
        return completion.Task;
    }

    private async Task CompleteEntryShutdownAsync(ModuleEntry entry, ModuleServiceRegistry services, Task readers,
        TaskCompletionSource<ModuleHostResult> completion)
    {
        await readers.ConfigureAwait(false);
        try
        {
            var context = new ModuleShutdownContext(entry.Descriptor, services);
            await InvokeModuleAsync(entry, "Shutdown", () => entry.Module.ShutdownAsync(context, CancellationToken.None), CancellationToken.None).ConfigureAwait(false);
            entry.Phase = ModuleHostPhase.Shutdown;
            completion.TrySetResult(ModuleHostResult.Success);
        }
        catch (Exception exception) { completion.TrySetResult(CreateResult([FailureDiagnostic(exception, "Shutdown", entry.Descriptor.Id)])); }
    }

    private async ValueTask InvokeModuleAsync(ModuleEntry entry, string stage, Func<ValueTask> action, CancellationToken cancellationToken)
    {
        try
        {
            await InvokeCallbackAsync(async () => { await action().ConfigureAwait(false); return true; }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) { throw new ModuleLifecycleExecutionException(entry.Descriptor.Id, stage, exception); }
    }

    private T InvokeCallback<T>(Func<T> action)
    {
        var previous = _callbackFrame.Value;
        var frame = new CallbackFrame();
        _callbackFrame.Value = frame;
        try { return action(); }
        finally
        {
            frame.IsActive = false;
            _callbackFrame.Value = previous;
        }
    }

    private async ValueTask<T> InvokeCallbackAsync<T>(Func<ValueTask<T>> action)
    {
        var previous = _callbackFrame.Value;
        var frame = new CallbackFrame();
        _callbackFrame.Value = frame;
        try { return await action().ConfigureAwait(false); }
        finally
        {
            frame.IsActive = false;
            _callbackFrame.Value = previous;
        }
    }

    private static TaskCompletionSource<ModuleHostResult> NewCompletion() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static ValueTask<ModuleHostResult> WaitForOperation(Task<ModuleHostResult> task, CancellationToken cancellationToken)
    {
        return new ValueTask<ModuleHostResult>(cancellationToken.CanBeCanceled ? task.WaitAsync(cancellationToken) : task);
    }

    private sealed class CallbackFrame
    {
        public volatile bool IsActive = true;
    }

}
