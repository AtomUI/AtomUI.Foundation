using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using AtomUI.Lifetimes;
using AtomUI.Modularity;
using AtomUI.Modularity.Services;

await ManagedRuntimeConsumer.RunAsync();

internal static class ManagedRuntimeConsumer
{
    private static readonly ModuleId Owner = ModuleId.FromModuleType(typeof(ProbeModule));

    public static async Task RunAsync()
    {
        await VerifySharedScopeRetirementAsync();
        await VerifyHostRetirementAsync();
        await VerifyFailedOwnerClosureAsync();
        await VerifyModuleChangesAsync();
        await VerifyPublishedDomainLeaseRetirementAsync();
        await VerifyScopeRetirementBookkeepingAsync();
#if NET8_0
        Console.WriteLine("MANAGED_RUNTIME_CONSUMER_OK net8.0");
#elif NET10_0
        Console.WriteLine("MANAGED_RUNTIME_CONSUMER_OK net10.0");
#else
        throw new PlatformNotSupportedException("This consumer fixture targets net8.0 and net10.0.");
#endif
    }

    private static async Task VerifyScopeRetirementBookkeepingAsync()
    {
        var runtime = new ModuleServiceRuntime();
        var releases = 0;
        runtime.AddScopedFactory<Model>(Owner, _ => new(), _ => { releases++; return ValueTask.CompletedTask; });
        runtime.Freeze();
        var origin = runtime.CreateCandidate(runtime.Bindings, []); origin.Freeze();
        var rebound = runtime.CreateCandidate(origin, []); rebound.Freeze();
        var receipts = new List<WeakReference>();
        for (var index = 0; index < 128; index++)
            receipts.Add(CloseScope(origin, rebound));
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Ensure(releases == 128, "Every real scope service must Release exactly once.");
        Ensure(receipts.All(receipt => !receipt.IsAlive), $"Successful scopes must not leave retirement metadata in a live binding view (retained={receipts.Count(receipt => receipt.IsAlive)}).");
        await runtime.DisposeAsync();

        var failedRuntime = new ModuleServiceRuntime();
        var failure = new InvalidOperationException("scope-release");
        failedRuntime.AddScopedFactory<Model>(Owner, _ => new(), _ => ValueTask.FromException(failure));
        failedRuntime.Freeze();
        var candidate = failedRuntime.CreateCandidate(failedRuntime.Bindings, []); candidate.Freeze();
        var scope = candidate.Root.CreateScope();
        var lease = scope.Borrow<Model>();
        var closing = scope.BeginRetirement();
        Ensure(!closing.FinalCompletion.IsCompleted, "Scope retirement must wait for its actual lease.");
        lease.Dispose();
        Ensure(!(await closing.FinalCompletion).IsSuccess, "The scope must preserve its real Release failure.");
        var final = await candidate.BeginRetirement().FinalCompletion;
        Ensure(!final.IsSuccess && final.Failures.Any(item => ReferenceEquals(item.Exception, failure)),
            "Candidate retirement must include the original failed local scope receipt.");
        Ensure(!(await failedRuntime.CompleteShutdownAsync()).IsSuccess, "Global shutdown must retain the failed scope result.");
        Console.WriteLine("SCOPE_RETIREMENT_CONSUMER_OK scopes=128 releases=128 failedReceipt=1");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CloseScope(ModuleServiceBindings origin, ModuleServiceBindings rebound)
    {
        var scope = origin.Root.CreateScope();
        using (scope.Borrow<Model>()) { }
        var ticket = rebound.BindScope(scope).BeginRetirement();
        // Exit the producer frame before the dedicated GC observation; an awaited
        // helper can otherwise keep its last completed ticket on the continuation stack.
        Ensure(ticket.FinalCompletion.GetAwaiter().GetResult().IsSuccess, "Rebound scope retirement must succeed.");
        return new WeakReference(ticket);
    }

    private static async Task VerifySharedScopeRetirementAsync()
    {
        var runtime = new ModuleServiceRuntime();
        var factoryCalls = 0;
        var releaseCalls = 0;
        runtime.AddScopedFactory<Model>(Owner, _ => { factoryCalls++; return new(); }, _ =>
        {
            releaseCalls++;
            return ValueTask.CompletedTask;
        });
        runtime.Freeze();

        var modelScope = runtime.Root.CreateScope();
        var firstView = runtime.Root.CreateScope();
        var secondView = runtime.Root.CreateScope();
        firstView.ShareFrom<Model>(modelScope);
        secondView.ShareFrom<Model>(modelScope);
        var firstReader = firstView.Borrow<Model>();
        var secondReader = secondView.Borrow<Model>();
        var sharedModel = firstReader.Value;
        Ensure(ReferenceEquals(sharedModel, secondReader.Value), "View scopes must borrow the original Model owner.");
        Ensure(factoryCalls == 1, "Sharing must not create a second Model instance.");

        firstReader.Dispose();
        await firstView.DisposeAsync();
        Ensure(releaseCalls == 0, "Closing one View must leave the shared Model alive.");

        var retirement = modelScope.BeginRetirement();
        Ensure(!retirement.FinalCompletion.IsCompleted, "The Model must wait for the remaining View lease.");
        Ensure(ReferenceEquals(sharedModel, secondReader.Value), "An admitted View lease must retain the original Model.");
        secondReader.Dispose();
        var result = await retirement.FinalCompletion;

        Ensure(result.IsSuccess, "The shared owner closure must finish successfully.");
        Ensure(releaseCalls == 1, "The shared Model must have exactly one release owner.");
        await secondView.DisposeAsync();
        await modelScope.DisposeAsync();
        await runtime.DisposeAsync();
    }

    private static async Task VerifyHostRetirementAsync()
    {
        var runtime = new ModuleServiceRuntime();
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = new List<string>();
        Model? model = null;
        runtime.AddSingletonFactory<Model>(Owner, _ => model = new(), value =>
        {
            value.ReleaseCalls++;
            calls.Add("release");
            return ValueTask.CompletedTask;
        }, _ =>
        {
            calls.Add("stop");
            stopped.TrySetResult();
        });

        var registration = ModuleRegistration.For(
            ModuleDescriptor.For<ProbeModule>("Probe"),
            () => new ProbeModule(() => calls.Add("module-shutdown")));
        var options = new ModuleHostOptionsBuilder()
            .Enable<ProbeModule>()
            .UseLifetime(runtime)
            .Build();
        var host = new ModuleHost([registration], options);
        Ensure((await host.InitializeAsync()).IsSuccess, "The managed host must initialize.");

        var resultReader = runtime.Root.Borrow<Model>();
        var shutdown = host.ShutdownAsync().AsTask();
        await stopped.Task;
        Ensure(!shutdown.IsCompleted, "Host shutdown must wait for its live result lease.");
        Ensure(model!.ReleaseCalls == 0, "Stop must not release a service with an active reader.");
        Ensure(ReferenceEquals(model, resultReader.Value), "The original service must stay available to the reader.");
        resultReader.Dispose();

        Ensure((await shutdown).IsSuccess, "Host shutdown must complete after the final reader leaves.");
        Ensure(calls.SequenceEqual(["stop", "module-shutdown", "release"]), "Stop, module shutdown and Release must be ordered.");
        await runtime.DisposeAsync();
    }

    private static async Task VerifyFailedOwnerClosureAsync()
    {
        var runtime = new ModuleServiceRuntime();
        var dependency = new Model();
        var releaseAttempts = 0;
        runtime.AddScopedFactory<Model>(Owner, _ => dependency, _ =>
        {
            dependency.ReleaseCalls++;
            return ValueTask.CompletedTask;
        });
        runtime.AddScopedFactory<Consumer>(Owner,
            context => new(context.GetRequiredService<Model>()),
            _ =>
            {
                releaseAttempts++;
                throw new InvalidOperationException("consumer release failed");
            },
            dependencies: [typeof(Model)]);
        runtime.Freeze();
        runtime.Root.Borrow<Consumer>().Dispose();

        var retirement = runtime.Root.BeginRetirement();
        var result = await retirement.FinalCompletion;
        Ensure(!result.IsSuccess, "A failed Release must stay visible in the final result.");
        Ensure(result.Owners.Any(owner => owner.State == LifetimeOwnerState.RetainedByFailedOwner),
            "The failed owner's dependency closure must remain retained.");
        Ensure(dependency.ReleaseCalls == 0, "A dependency used by the failed owner must not be released early.");
        Ensure(releaseAttempts == 1, "A failed release callback must not be repeated.");

        try
        {
            await runtime.DisposeAsync();
            throw new InvalidOperationException("Runtime disposal must preserve the cleanup failure.");
        }
        catch (ModuleServiceShutdownException exception)
        {
            Ensure(exception.Diagnostics.Any(diagnostic =>
                    diagnostic.Code == ModuleServiceDiagnosticCodes.OwnerRetainedAfterCleanupFailure),
                "Shutdown diagnostics must identify retained dependency owners.");
        }
        Ensure(releaseAttempts == 1, "Repeated shutdown must not retry a failed owner.");
    }

    private static async Task VerifyPublishedDomainLeaseRetirementAsync()
    {
        var shutdowns = 0;
        var a = ModuleRegistration.For(ModuleDescriptor.For<ProbeModule>("A"), () => new ProbeModule(static () => { }));
        var b = ModuleRegistration.For(ModuleDescriptor.For<LeaseModule>("B"), () => new LeaseModule(() => shutdowns++));
        var host = new ModuleHost([a, b], new ModuleHostOptions([typeof(ProbeModule), typeof(LeaseModule)]));
        Ensure((await host.InitializeAsync()).IsSuccess, "Domain lease host must initialize.");
        var lease = host.AcquireView([ModuleId.FromModuleType<LeaseModule>()]);
        var oldState = new LeaseDomainState(lease);
        var first = await host.ChangeAsync(new ModuleChangeRequest(host.Current, [a, b],
            new ModuleHostOptions([typeof(ProbeModule), typeof(LeaseModule)]),
            participant: new PreparedParticipant(new PublishedLeasePreparation(oldState))));
        Ensure(first.IsCommitted && ReferenceEquals(first.View.State, oldState), "The real old lease holder must first be published.");
        Ensure((await first.Retirement.FinalCompletion).IsSuccess, "Initial domain publication must retire its empty predecessor.");
        var preparation = new PublishedLeasePreparation(new ChangeState("new"), lease);
        var result = await host.ChangeAsync(new ModuleChangeRequest(host.Current, [a],
            new ModuleHostOptions([typeof(ProbeModule)]), participant: new PreparedParticipant(preparation)));
        try
        {
            Ensure(result.IsCommitted && preparation.Stopped, "The change must commit and dispatch Stop.");
            Ensure((await result.Retirement.FinalCompletion.WaitAsync(TimeSpan.FromSeconds(5))).IsSuccess,
                "Domain final cleanup must run alongside the module reader drain.");
            Ensure(preparation.CompleteEntered && shutdowns == 1, "Only domain final cleanup may return the old module lease.");
            Console.WriteLine("DOMAIN_RETIREMENT_CONSUMER_OK published=1 complete=1 leaseReturned=1 Bshutdown=1");
        }
        finally
        {
            lease.Dispose();
            await host.ShutdownAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static async Task VerifyModuleChangesAsync()
    {
        var runtime = new ModuleServiceRuntime();
        var aCreated = 0; var bCreated = 0; var dCreated = 0;
        var failInitialization = false;
        var a = ModuleRegistration.For(ModuleDescriptor.For<StableModule>("Stable"), () => { aCreated++; return new StableModule(); });
        var b = ModuleRegistration.For(ModuleDescriptor.For<FeatureModule>("Feature"), () => { bCreated++; return new FeatureModule(failInitialization); });
        var d = ModuleRegistration.For(ModuleDescriptor.For<UnselectedModule>("Disabled"), () => { dCreated++; return new UnselectedModule(); });
        var host = new ModuleHost([a], new ModuleHostOptions([typeof(StableModule)]) { Lifetime = runtime });
        Ensure((await host.InitializeAsync()).IsSuccess, "Initial change consumer host must initialize.");
        var baseline = host.Current;
        var originalModule = baseline.Modules.Single().Module;
        var originalSingleton = runtime.Root.Borrow<StableService>();
        var modelScope = runtime.Root.CreateScope();
        var originalModel = modelScope.Borrow<Model>();
        var added = await host.ChangeAsync(new ModuleChangeRequest(baseline, [a, b, d],
            new ModuleHostOptions([typeof(StableModule), typeof(FeatureModule)]), participant: new ChangeParticipant("enabled")));
        Ensure(added.Status == ModuleChangeStatus.Committed, "Adding Feature must commit on the same Host.");
        Ensure(ReferenceEquals(originalModule, host.EnabledModules.Single(entry => entry.Module is StableModule).Module), "Stable module identity must survive.");
        Ensure(((ChangeState)added.View.State!).Name == "enabled" && added.View.Modules.Count == 2, "Domain and module views must commit together.");
        Ensure(added.View.Services.GetRequiredService<ChangeMetadata>().Name == "enabled", "Neutral service contributions must belong to the same published view.");
        using (var singleton = runtime.Root.Borrow<StableService>())
            Ensure(ReferenceEquals(originalSingleton.Value, singleton.Value), "Stable Singleton must keep its actual owner.");
        using (var model = added.View.ManagedServices!.BindScope(modelScope).Borrow<Model>())
            Ensure(ReferenceEquals(originalModel.Value, model.Value), "Existing Model scope must not be rebuilt.");

        var oldView = host.AcquireView([ModuleId.FromModuleType<FeatureModule>()]);
        var reader = runtime.Root.Borrow<FeatureService>();
        var originalFeature = reader.Value;
        var removed = await host.ChangeAsync(new ModuleChangeRequest(host.Current, [a, b, d],
            new ModuleHostOptions([typeof(StableModule)]), participant: new ChangeParticipant("disabled")));
        Ensure(removed.Status == ModuleChangeStatus.Committed && removed.View.Modules.Count == 1, "Feature removal must publish once.");
        Ensure(removed.View.Services.GetRequiredService<ChangeMetadata>().Name == "disabled", "Superseded neutral contributions must be replaced privately.");
        Ensure(originalFeature.Stops == 1 && originalFeature.Releases == 0, "Stop must preserve actual readers.");
        Ensure(!removed.Retirement.FinalCompletion.IsCompleted && ReferenceEquals(reader.Value, originalFeature), "Old result must retain its original service.");
        oldView.Dispose();
        reader.Dispose();
        Ensure((await removed.Retirement.FinalCompletion).IsSuccess && originalFeature.Releases == 1, "Final release must follow real exit.");
        var stableView = host.Current;
        var rejected = await host.ChangeAsync(new ModuleChangeRequest(stableView, [a, b, d],
            new ModuleHostOptions([typeof(StableModule), typeof(FeatureModule)]), participant: new ChangeParticipant("rejected", reject: true)));
        Ensure(rejected.Status == ModuleChangeStatus.Rejected && ReferenceEquals(stableView, host.Current), "Domain rejection must preserve the original combined view.");
        Ensure((await rejected.Retirement.FinalCompletion).IsSuccess, "Rejected candidates must clean their real resources.");
        failInitialization = true;
        var aborted = await host.ChangeAsync(new ModuleChangeRequest(host.Current, [a, b, d],
            new ModuleHostOptions([typeof(StableModule), typeof(FeatureModule)]), participant: new ChangeParticipant("scope-abort")));
        Ensure(aborted.Status == ModuleChangeStatus.Rejected && ReferenceEquals(stableView, host.Current), "Initialization failure must preserve the old composition.");
        Ensure((await aborted.Retirement.FinalCompletion).IsSuccess, "Domain abort must be able to await its private Foundation service scope.");
        var stale = await host.ChangeAsync(new ModuleChangeRequest(baseline, [a], new ModuleHostOptions([typeof(StableModule)])));
        Ensure(stale.Status == ModuleChangeStatus.StaleBaseline, "Old baselines cannot overwrite a newer view.");
        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel();
            var canceled = await host.ChangeAsync(new ModuleChangeRequest(host.Current, [a], new ModuleHostOptions([typeof(StableModule)])), cancellation.Token);
            Ensure(canceled.Status == ModuleChangeStatus.Canceled, "Precommit cancellation must not publish.");
        }
        Ensure(aCreated == 1 && bCreated == 3 && dCreated == 0, "Actual module factories must match committed and rejected candidates only.");
        originalSingleton.Dispose(); originalModel.Dispose();
        await modelScope.DisposeAsync();
        Ensure((await host.ShutdownAsync()).IsSuccess, "The same Host must close current and all old resources.");
        Console.WriteLine("FOUNDATION_CHANGE_CONSUMER_OK A=1 B=3 D=0 preserved=1 joint=1 rejected=1 retired=1 abort=1");
    }

    private sealed class StableModule : ModuleBase
    {
        public override void ConfigureServices(ModuleServiceConfigurationContext context)
        {
            context.ManagedServices!.AddSingletonFactory(ModuleId.FromModuleType<StableModule>(), _ => new StableService(), static _ => ValueTask.CompletedTask);
            context.ManagedServices.AddScopedFactory(ModuleId.FromModuleType<StableModule>(), _ => new Model(), static _ => ValueTask.CompletedTask);
        }
    }
    private sealed class FeatureModule(bool failInitialization) : ModuleBase
    {
        public override void ConfigureServices(ModuleServiceConfigurationContext context)
        {
            context.ManagedServices!.AddSingletonFactory(
                ModuleId.FromModuleType<FeatureModule>(), activation => new FeatureService(activation.GetRequiredService<StableService>()),
                value => { value.Releases++; return ValueTask.CompletedTask; }, value => value.Stops++, typeof(StableService));
            context.ManagedServices.AddScopedFactory(ModuleId.FromModuleType<FeatureModule>(),
                activation => new FeatureOperation(activation.GetRequiredService<FeatureService>()),
                static _ => ValueTask.CompletedTask, dependencies: [typeof(FeatureService)]);
        }
        public override void Initialize(ModuleInitializationContext context)
        {
            if (failInitialization) throw new InvalidOperationException("private initialization failed");
        }
    }
    private sealed class UnselectedModule : ModuleBase;
    private sealed class StableService;
    private sealed class FeatureService(StableService dependency)
    {
        public StableService Dependency { get; } = dependency;
        public int Stops;
        public int Releases;
    }
    private sealed record ChangeState(string Name);
    private sealed record LeaseDomainState(ModuleHostLease Lease);
    private sealed class PreparedParticipant(ModuleChangePreparation preparation) : IModuleChangeParticipant
    {
        public ValueTask<ModuleChangePreparation> PrepareAsync(ModuleChangeContext context, CancellationToken cancellationToken) => ValueTask.FromResult(preparation);
    }
    private sealed class PublishedLeasePreparation(object state, ModuleHostLease? previousLease = null) : ModuleChangePreparation
    {
        public override object State => state;
        public bool Stopped;
        public bool CompleteEntered;
        public override void StopPrevious() => Stopped = true;
        public override ValueTask<ModuleHostResult> CompletePreviousAsync()
        {
            CompleteEntered = true;
            previousLease?.Dispose();
            return ValueTask.FromResult(ModuleHostResult.Success);
        }
    }
    private sealed record ChangeMetadata(string Name);
    private sealed record FeatureOperation(FeatureService Service);
    private sealed class ChangePreparation(string name) : ModuleChangePreparation { public override object State { get; } = new ChangeState(name); }
    private sealed class ScopeChangePreparation(ModuleServiceScope scope) : ModuleChangePreparation
    {
        public override object State { get; } = new ChangeState("scope-abort");
        public override async ValueTask AbortAsync() => await scope.DisposeAsync();
    }
    private sealed class ChangeParticipant(string name, bool reject = false) : IModuleChangeParticipant
    {
        public ValueTask ConfigureAsync(ModuleChangeContext context, CancellationToken cancellationToken)
        {
            context.Services.ReplaceSingleton(new ChangeMetadata(name));
            return ValueTask.CompletedTask;
        }
        public ValueTask<ModuleChangePreparation> PrepareAsync(ModuleChangeContext context, CancellationToken cancellationToken)
        {
            if (name == "scope-abort")
            {
                var scope = context.ManagedServices!.Root.CreateScope();
                using var operation = scope.Borrow<FeatureOperation>();
                return ValueTask.FromResult<ModuleChangePreparation>(new ScopeChangePreparation(scope));
            }
            if (name != "disabled")
            {
                using var feature = context.ManagedServices!.Root.Borrow<FeatureService>();
                Ensure(feature.Value.Dependency is not null, "Candidate factory must capture the original stable service.");
            }
            if (reject) throw new InvalidOperationException("domain validation refused candidate");
            return ValueTask.FromResult<ModuleChangePreparation>(new ChangePreparation(name));
        }
    }

    private sealed class Model
    {
        public int ReleaseCalls { get; set; }
    }

    private sealed record Consumer(Model Model);

    private sealed class ProbeModule(Action shutdown) : ModuleBase
    {
        public override void Shutdown(ModuleShutdownContext context) => shutdown();
    }
    private sealed class LeaseModule(Action shutdown) : ModuleBase
    {
        public override void Shutdown(ModuleShutdownContext context) => shutdown();
    }
}
