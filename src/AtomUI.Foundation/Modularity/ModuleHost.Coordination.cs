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
        var diagnostics = new List<ModuleDiagnostic>();
        try
        {
            InvokeCallback(() => { _options.Lifetime?.Stop(); return true; });
        }
        catch (Exception exception) { diagnostics.Add(FailureDiagnostic(exception, "Stop")); }

        for (var index = _enabledModules.Count - 1; index >= 0; index--)
        {
            var entry = _enabledModules[index];
            if (!_shutdownAttempts.Add(entry)) { continue; }
            try
            {
                var context = new ModuleShutdownContext(entry.Descriptor, Services);
                await InvokeModuleAsync(entry, "Shutdown", () => entry.Module.ShutdownAsync(context, CancellationToken.None), CancellationToken.None).ConfigureAwait(false);
                entry.Phase = ModuleHostPhase.Shutdown;
            }
            catch (Exception exception) { diagnostics.Add(FailureDiagnostic(exception, "Shutdown", entry.Descriptor.Id)); }
        }

        if (_options.Lifetime is not null)
        {
            try
            {
                var result = await InvokeCallbackAsync(() => _options.Lifetime.CompleteShutdownAsync()).ConfigureAwait(false);
                diagnostics.AddRange(result.Diagnostics);
            }
            catch (Exception exception) { diagnostics.Add(FailureDiagnostic(exception, "CompleteShutdown")); }
        }

        var finalResult = CreateResult(diagnostics);
        lock (_stateLock) { _phase = _terminalFailure ? ModuleHostPhase.Failed : ModuleHostPhase.Shutdown; }
        completion.TrySetResult(finalResult);
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
