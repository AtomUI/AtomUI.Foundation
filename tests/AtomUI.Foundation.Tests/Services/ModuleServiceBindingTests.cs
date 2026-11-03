using AtomUI.Modularity;
using AtomUI.Modularity.Services;
using Shouldly;
using Xunit;

namespace AtomUI.Foundation.Tests.Services;

public sealed class ModuleServiceBindingTests
{
    private static readonly ModuleId A = ModuleId.FromSerializedValue("A");
    private static readonly ModuleId B = ModuleId.FromSerializedValue("B");

    [Fact]
    public async Task Candidate_preserves_unchanged_scope_and_isolates_new_binding()
    {
        var runtime = new ModuleServiceRuntime();
        runtime.AddScopedFactory<Resource>(A, _ => new(), _ => ValueTask.CompletedTask);
        runtime.Freeze();
        var scope = runtime.Root.CreateScope();
        using var old = scope.Borrow<Resource>();
        var candidate = runtime.CreateCandidate(runtime.Bindings, []);
        candidate.AddSingletonFactory<Other>(B, _ => new(), _ => ValueTask.CompletedTask);
        candidate.Freeze();
        using var same = candidate.BindScope(scope).Borrow<Resource>();
        same.Value.ShouldBeSameAs(old.Value);
        Should.Throw<InvalidOperationException>(() => scope.Borrow<Other>());
        using var added = candidate.Root.Borrow<Other>();
        var cleanup = candidate.BeginRetirement();
        added.Dispose();
        (await cleanup.FinalCompletion).IsSuccess.ShouldBeTrue();
        old.Dispose(); same.Dispose();
        await runtime.DisposeAsync();
    }

    [Fact]
    public async Task Replacement_keeps_old_reader_and_unaffected_singleton()
    {
        var runtime = new ModuleServiceRuntime();
        runtime.AddSingletonFactory<Resource>(A, _ => new(), _ => ValueTask.CompletedTask);
        runtime.AddSingletonFactory<Other>(B, _ => new(), _ => ValueTask.CompletedTask);
        runtime.Freeze();
        using var old = runtime.Root.Borrow<Resource>();
        using var stable = runtime.Root.Borrow<Other>();
        var candidate = runtime.CreateCandidate(runtime.Bindings, [A]);
        candidate.AddSingletonFactory<Resource>(A, _ => new(), _ => ValueTask.CompletedTask);
        candidate.Freeze();
        using var newer = candidate.Root.Borrow<Resource>();
        using var same = candidate.Root.Borrow<Other>();
        newer.Value.ShouldNotBeSameAs(old.Value);
        same.Value.ShouldBeSameAs(stable.Value);
        using var transition = runtime.PrepareTransition(runtime.Bindings, candidate);
        transition.AffectedModules.ShouldContain(A);
        Should.Throw<InvalidOperationException>(() => runtime.Root.Borrow<Resource>());
        var retired = transition.CompleteRetirement();
        retired.FinalCompletion.IsCompleted.ShouldBeFalse();
        old.Dispose();
        (await retired.FinalCompletion).IsSuccess.ShouldBeTrue();
        newer.Dispose(); same.Dispose(); stable.Dispose();
        await runtime.DisposeAsync();
    }

    [Fact]
    public async Task Retained_consumer_of_removed_service_requires_explicit_replacement()
    {
        var runtime = new ModuleServiceRuntime();
        runtime.AddSingletonFactory<Resource>(A, _ => new(), _ => ValueTask.CompletedTask);
        runtime.AddSingletonFactory<Consumer>(B, context => new(context.GetRequiredService<Resource>()), _ => ValueTask.CompletedTask,
            dependencies: [typeof(Resource)]);
        runtime.Freeze();
        using var consumer = runtime.Root.Borrow<Consumer>();
        var candidate = runtime.CreateCandidate(runtime.Bindings, [A]);
        candidate.AddSingletonFactory<Resource>(A, _ => new(), _ => ValueTask.CompletedTask);
        candidate.Freeze();
        Should.Throw<InvalidOperationException>(() => runtime.PrepareTransition(runtime.Bindings, candidate));
        using var stillAvailable = runtime.Root.Borrow<Resource>();
        consumer.Dispose(); stillAvailable.Dispose();
        await runtime.DisposeAsync();
    }

    [Fact]
    public async Task Failed_candidate_cleanup_keeps_shared_dependency_admitting_readers()
    {
        var runtime = new ModuleServiceRuntime();
        runtime.AddSingletonFactory<Resource>(A, _ => new(), _ => ValueTask.CompletedTask);
        runtime.Freeze();
        var candidate = runtime.CreateCandidate(runtime.Bindings, []);
        candidate.AddSingletonFactory<Consumer>(B, context => new(context.GetRequiredService<Resource>()),
            _ => ValueTask.FromException(new InvalidOperationException("release")), dependencies: [typeof(Resource)]);
        candidate.Freeze();
        using (candidate.Root.Borrow<Consumer>()) { }
        var result = await candidate.BeginRetirement().FinalCompletion;
        result.IsSuccess.ShouldBeFalse();
        using var available = runtime.Root.Borrow<Resource>();
        available.Dispose();
        runtime.Stop();
        (await runtime.CompleteShutdownAsync()).IsSuccess.ShouldBeFalse();
    }

    [Fact]
    public async Task Transition_ignores_scope_owners_already_released()
    {
        var runtime = new ModuleServiceRuntime();
        runtime.AddScopedFactory<Resource>(A, _ => new(), _ => ValueTask.CompletedTask);
        runtime.Freeze();
        var scope = runtime.Root.CreateScope();
        using (scope.Borrow<Resource>()) { }
        await scope.DisposeAsync();
        var candidate = runtime.CreateCandidate(runtime.Bindings, [A]);
        candidate.Freeze();
        using var transition = runtime.PrepareTransition(runtime.Bindings, candidate);
        (await transition.CompleteRetirement().FinalCompletion).IsSuccess.ShouldBeTrue();
        await runtime.DisposeAsync();
    }

    [Fact]
    public async Task Reused_factory_activates_against_original_dependency()
    {
        var runtime = new ModuleServiceRuntime();
        runtime.AddSingletonFactory<Resource>(A, _ => new(), _ => ValueTask.CompletedTask);
        runtime.AddSingletonFactory<Consumer>(B, context => new(context.GetRequiredService<Resource>()), _ => ValueTask.CompletedTask,
            dependencies: [typeof(Resource)]);
        runtime.Freeze();
        var candidate = runtime.CreateCandidate(runtime.Bindings, [A]);
        candidate.AddSingletonFactory<Resource>(A, _ => new(), _ => ValueTask.CompletedTask);
        candidate.Freeze();
        using var consumer = candidate.Root.Borrow<Consumer>();
        using var original = runtime.Root.Borrow<Consumer>();
        consumer.Value.ShouldBeSameAs(original.Value);
        using var dependency = runtime.Root.Borrow<Resource>();
        consumer.Value.Resource.ShouldBeSameAs(dependency.Value);
        Should.Throw<InvalidOperationException>(() => runtime.PrepareTransition(runtime.Bindings, candidate));
        consumer.Dispose(); original.Dispose(); dependency.Dispose();
        (await candidate.BeginRetirement().FinalCompletion).IsSuccess.ShouldBeTrue();
        await runtime.DisposeAsync();
    }

    [Fact]
    public async Task Transition_accounts_for_factory_reserved_before_suspension()
    {
        var runtime = new ModuleServiceRuntime();
        using var entered = new ManualResetEventSlim();
        using var finish = new ManualResetEventSlim();
        var stopped = 0;
        runtime.AddSingletonFactory<Resource>(A, _ => { entered.Set(); finish.Wait(); return new(); },
            _ => ValueTask.CompletedTask, _ => Interlocked.Increment(ref stopped));
        runtime.Freeze();
        var activation = Task.Run(() => Should.Throw<InvalidOperationException>(() => runtime.Root.Borrow<Resource>()));
        entered.Wait(TestContext.Current.CancellationToken);
        var candidate = runtime.CreateCandidate(runtime.Bindings, [A]);
        candidate.Freeze();
        using var transition = runtime.PrepareTransition(runtime.Bindings, candidate);
        var retirement = transition.CompleteRetirement();
        retirement.FinalCompletion.IsCompleted.ShouldBeFalse();
        finish.Set();
        await activation;
        (await retirement.FinalCompletion).IsSuccess.ShouldBeTrue();
        stopped.ShouldBe(1);
        await runtime.DisposeAsync();
    }

    [Fact]
    public async Task Shared_scope_stop_cannot_retire_retained_scope_service()
    {
        var runtime = new ModuleServiceRuntime();
        runtime.AddScopedFactory<Resource>(A, _ => new(), _ => ValueTask.CompletedTask);
        runtime.AddScopedFactory<Other>(B, _ => new(), _ => ValueTask.CompletedTask);
        runtime.Freeze();
        var source = runtime.Root.CreateScope();
        var target = runtime.Root.CreateScope();
        target.ShareFrom<Resource>(source);
        using var other = target.Borrow<Other>();
        var candidate = runtime.CreateCandidate(runtime.Bindings, [A]);
        candidate.Freeze();
        Should.Throw<InvalidOperationException>(() => runtime.PrepareTransition(runtime.Bindings, candidate));
        using var stillAvailable = target.Borrow<Other>();
        other.Dispose(); stillAvailable.Dispose();
        await runtime.DisposeAsync();
    }

    [Fact]
    public async Task Candidate_share_into_existing_scope_is_rejected_before_mutation()
    {
        var runtime = new ModuleServiceRuntime();
        runtime.AddScopedFactory<Other>(A, _ => new(), _ => ValueTask.CompletedTask);
        runtime.Freeze();
        var source = runtime.Root.CreateScope();
        var target = runtime.Root.CreateScope();
        var candidate = runtime.CreateCandidate(runtime.Bindings, []);
        candidate.AddScopedFactory<Resource>(B, _ => new(), _ => ValueTask.CompletedTask);
        candidate.Freeze();
        Should.Throw<InvalidOperationException>(() => candidate.BindScope(target).ShareFrom<Resource>(candidate.BindScope(source)));
        (await candidate.BeginRetirement().FinalCompletion).IsSuccess.ShouldBeTrue();
        using var available = target.Borrow<Other>();
        available.Dispose();
        await runtime.DisposeAsync();
    }

    [Fact]
    public async Task Candidate_retirement_is_idempotent_and_reports_failed_factory()
    {
        var runtime = new ModuleServiceRuntime();
        runtime.Freeze();
        var candidate = runtime.CreateCandidate(runtime.Bindings, []);
        candidate.AddSingletonFactory<Resource>(A, _ => throw new InvalidOperationException("factory"), _ => ValueTask.CompletedTask);
        candidate.Freeze();
        Should.Throw<InvalidOperationException>(() => candidate.Root.Borrow<Resource>());
        var first = candidate.BeginRetirement();
        candidate.BeginRetirement().ShouldBeSameAs(first);
        (await first.FinalCompletion).Failures.ShouldContain(failure => failure.Stage == "Factory");
        runtime.Stop();
        (await runtime.CompleteShutdownAsync()).IsSuccess.ShouldBeFalse();
    }

    [Fact]
    public async Task Candidate_owned_scope_releases_fresh_reused_scoped_instance_only()
    {
        var released = 0;
        var runtime = new ModuleServiceRuntime();
        runtime.AddScopedFactory<Resource>(A, _ => new(), _ => { released++; return ValueTask.CompletedTask; });
        runtime.Freeze();
        var currentScope = runtime.Root.CreateScope();
        using var current = currentScope.Borrow<Resource>();
        var candidate = runtime.CreateCandidate(runtime.Bindings, []);
        candidate.Freeze();
        var privateScope = candidate.Root.CreateScope();
        using (privateScope.Borrow<Resource>()) { }
        (await candidate.BeginRetirement().FinalCompletion).IsSuccess.ShouldBeTrue();
        released.ShouldBe(1);
        using var stillCurrent = currentScope.Borrow<Resource>();
        stillCurrent.Value.ShouldBeSameAs(current.Value);
        current.Dispose(); stillCurrent.Dispose();
        await runtime.DisposeAsync();
        released.ShouldBe(2);
    }

    [Fact]
    public async Task Sharing_does_not_alias_different_generation_registrations()
    {
        var runtime = new ModuleServiceRuntime();
        runtime.AddScopedFactory<Resource>(A, _ => new(), _ => ValueTask.CompletedTask);
        runtime.Freeze();
        var originalScope = runtime.Root.CreateScope();
        var candidate = runtime.CreateCandidate(runtime.Bindings, [A]);
        candidate.AddScopedFactory<Resource>(A, _ => new(), _ => ValueTask.CompletedTask);
        candidate.Freeze();
        var target = candidate.Root.CreateScope();
        Should.Throw<InvalidOperationException>(() => target.ShareFrom<Resource>(originalScope));
        (await candidate.BeginRetirement().FinalCompletion).IsSuccess.ShouldBeTrue();
        await runtime.DisposeAsync();
    }

    [Fact]
    public async Task Candidate_stop_notifies_before_complete_enables_release()
    {
        var stopped = 0;
        var released = 0;
        var runtime = new ModuleServiceRuntime();
        runtime.Freeze();
        var candidate = runtime.CreateCandidate(runtime.Bindings, []);
        candidate.AddSingletonFactory<Resource>(A, _ => new(), _ => { released++; return ValueTask.CompletedTask; }, _ => stopped++);
        candidate.Freeze();
        using (candidate.Root.Borrow<Resource>()) { }
        candidate.Stop();
        candidate.Stop();
        stopped.ShouldBe(1);
        released.ShouldBe(0);
        var ticket = candidate.CompleteRetirement();
        candidate.BeginRetirement().ShouldBeSameAs(ticket);
        (await ticket.FinalCompletion).IsSuccess.ShouldBeTrue();
        released.ShouldBe(1);
        await runtime.DisposeAsync();
    }

    [Fact]
    public async Task Cancelled_transition_allows_retry_after_inflight_factory_capture_is_blocked()
    {
        using var entered = new ManualResetEventSlim();
        using var proceed = new ManualResetEventSlim();
        var attempts = 0;
        var runtime = new ModuleServiceRuntime();
        runtime.AddSingletonFactory<Resource>(A, _ => new(), _ => ValueTask.CompletedTask);
        runtime.AddSingletonFactory<Consumer>(B, context =>
        {
            if (Interlocked.Increment(ref attempts) == 1) { entered.Set(); proceed.Wait(); }
            return new Consumer(context.GetRequiredService<Resource>());
        }, _ => ValueTask.CompletedTask, dependencies: [typeof(Resource)]);
        runtime.Freeze();
        var activation = Task.Run(() => Should.Throw<InvalidOperationException>(() => runtime.Root.Borrow<Consumer>()));
        entered.Wait(TestContext.Current.CancellationToken);
        var candidate = runtime.CreateCandidate(runtime.Bindings, [A, B]);
        candidate.Freeze();
        using var transition = runtime.PrepareTransition(runtime.Bindings, candidate);
        proceed.Set();
        await activation;
        transition.Dispose();
        using var retried = runtime.Root.Borrow<Consumer>();
        attempts.ShouldBe(2);
        retried.Dispose();
        runtime.Stop();
        await runtime.CompleteShutdownAsync();
    }

    [Fact]
    public async Task Cancelled_transition_allows_retry_when_constructing_owner_was_suspended()
    {
        using var entered = new ManualResetEventSlim();
        using var proceed = new ManualResetEventSlim();
        var attempts = 0;
        var runtime = new ModuleServiceRuntime();
        runtime.AddSingletonFactory<Resource>(B, _ => new(), _ => ValueTask.CompletedTask);
        runtime.AddSingletonFactory<Consumer>(A, context =>
        {
            if (Interlocked.Increment(ref attempts) == 1) { entered.Set(); proceed.Wait(); }
            return new Consumer(context.GetRequiredService<Resource>());
        }, _ => ValueTask.CompletedTask, dependencies: [typeof(Resource)]);
        runtime.Freeze();
        using (runtime.Root.Borrow<Resource>()) { }
        var activation = Task.Run(() => Should.Throw<InvalidOperationException>(() => runtime.Root.Borrow<Consumer>()));
        entered.Wait(TestContext.Current.CancellationToken);
        var candidate = runtime.CreateCandidate(runtime.Bindings, [A]);
        candidate.Freeze();
        using var transition = runtime.PrepareTransition(runtime.Bindings, candidate);
        proceed.Set();
        await activation;
        transition.Dispose();
        using var retried = runtime.Root.Borrow<Consumer>();
        attempts.ShouldBe(2);
        retried.Dispose();
        runtime.Stop();
        await runtime.CompleteShutdownAsync();
    }

    [Fact]
    public async Task Candidate_retirement_during_full_shutdown_joins_global_ticket()
    {
        var runtime = new ModuleServiceRuntime();
        runtime.Freeze();
        var candidate = runtime.CreateCandidate(runtime.Bindings, []);
        candidate.AddSingletonFactory<Resource>(A, _ => new(), _ => ValueTask.CompletedTask);
        candidate.Freeze();
        using var lease = candidate.Root.Borrow<Resource>();
        var shutdown = runtime.CompleteShutdownAsync().AsTask();
        shutdown.IsCompleted.ShouldBeFalse();
        candidate.Stop();
        var retirement = candidate.CompleteRetirement();
        retirement.FinalCompletion.IsCompleted.ShouldBeFalse();
        lease.Dispose();
        (await retirement.FinalCompletion).IsSuccess.ShouldBeTrue();
        (await shutdown).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task Concurrent_transition_stop_waits_until_all_stop_notifications_finish()
    {
        using var firstEntered = new ManualResetEventSlim();
        using var releaseFirst = new ManualResetEventSlim();
        var secondStopped = false;
        var runtime = new ModuleServiceRuntime();
        runtime.AddSingletonFactory<Resource>(A, _ => new(), _ => ValueTask.CompletedTask,
            _ => { firstEntered.Set(); releaseFirst.Wait(); });
        runtime.AddSingletonFactory<Other>(A, _ => new(), _ => ValueTask.CompletedTask,
            _ => secondStopped = true);
        runtime.Freeze();
        using (runtime.Root.Borrow<Resource>()) { }
        using (runtime.Root.Borrow<Other>()) { }
        var candidate = runtime.CreateCandidate(runtime.Bindings, [A]);
        candidate.Freeze();
        using var transition = runtime.PrepareTransition(runtime.Bindings, candidate);
        var first = Task.Run(transition.Stop, TestContext.Current.CancellationToken);
        firstEntered.Wait(TestContext.Current.CancellationToken);
        var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = Task.Run(() => { secondEntered.SetResult(); transition.Stop(); }, TestContext.Current.CancellationToken);
        await secondEntered.Task;
        var returnedEarly = await Task.WhenAny(second, Task.Delay(100, TestContext.Current.CancellationToken)) == second;
        releaseFirst.Set();
        await Task.WhenAll(first, second);
        returnedEarly.ShouldBeFalse();
        secondStopped.ShouldBeTrue();
        (await transition.CompleteRetirement().FinalCompletion).IsSuccess.ShouldBeTrue();
        await runtime.DisposeAsync();
    }

    [Fact]
    public async Task Stop_callback_can_reenter_transition_stop()
    {
        ModuleServiceTransition? transition = null;
        var runtime = new ModuleServiceRuntime();
        runtime.AddSingletonFactory<Resource>(A, _ => new(), _ => ValueTask.CompletedTask, _ => transition!.Stop());
        runtime.Freeze();
        using (runtime.Root.Borrow<Resource>()) { }
        var candidate = runtime.CreateCandidate(runtime.Bindings, [A]);
        candidate.Freeze();
        using (transition = runtime.PrepareTransition(runtime.Bindings, candidate))
        {
            var stop = Task.Run(transition.Stop, TestContext.Current.CancellationToken);
            (await Task.WhenAny(stop, Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken))).ShouldBeSameAs(stop);
            await stop;
            (await transition.CompleteRetirement().FinalCompletion).IsSuccess.ShouldBeTrue();
        }
        await runtime.DisposeAsync();
    }

    [Fact]
    public async Task Cold_retained_factory_with_replaced_dependency_rejects_transition()
    {
        var count = 0;
        var runtime = new ModuleServiceRuntime();
        runtime.AddSingletonFactory<Resource>(A, _ => new(), _ => ValueTask.CompletedTask);
        runtime.AddSingletonFactory<Consumer>(B, context => { count++; return new(context.GetRequiredService<Resource>()); },
            _ => ValueTask.CompletedTask, dependencies: [typeof(Resource)]);
        runtime.Freeze();
        var candidate = runtime.CreateCandidate(runtime.Bindings, [A]);
        candidate.AddSingletonFactory<Resource>(A, _ => new(), _ => ValueTask.CompletedTask);
        candidate.Freeze();
        Should.Throw<InvalidOperationException>(() => runtime.PrepareTransition(runtime.Bindings, candidate));
        count.ShouldBe(0);
        await runtime.DisposeAsync();
    }

    [Fact]
    public async Task Published_candidate_registration_can_share_into_existing_scope()
    {
        var runtime = new ModuleServiceRuntime();
        var a = ModuleRegistration.For(ModuleDescriptor.For<BaseModule>("base"), () => new BaseModule());
        var b = ModuleRegistration.For(ModuleDescriptor.For<SharingModule>("sharing"), () => new SharingModule());
        var options = new ModuleHostOptions([typeof(BaseModule)]) { Lifetime = runtime };
        var host = new ModuleHost([a], options);
        (await host.InitializeAsync(TestContext.Current.CancellationToken)).IsSuccess.ShouldBeTrue();
        var target = runtime.Root.CreateScope();
        var result = await host.ChangeAsync(new ModuleChangeRequest(host.Current, [a, b],
            new ModuleHostOptions([typeof(BaseModule), typeof(SharingModule)]) { Lifetime = runtime }), TestContext.Current.CancellationToken);
        result.Status.ShouldBe(ModuleChangeStatus.Committed);
        var source = runtime.Root.CreateScope();
        var targetView = runtime.Bindings.BindScope(target);
        targetView.ShareFrom<Resource>(source);
        using (var first = targetView.Borrow<Resource>())
        using (var second = source.Borrow<Resource>()) first.Value.ShouldBeSameAs(second.Value);
        await host.ShutdownAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Retired_registration_and_factory_capture_are_collectible_while_host_remains_active()
    {
        var (host, weak) = await BuildHostWithRetiredFactory();
        for (var i = 0; i < 5 && weak.IsAlive; i++)
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        }
        weak.IsAlive.ShouldBeFalse();
        host.Phase.ShouldBe(ModuleHostPhase.Initialized);
        await host.ShutdownAsync(TestContext.Current.CancellationToken);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference RegisterPayload(ModuleServiceRuntime runtime)
    {
        var payload = new object();
        var weak = new WeakReference(payload);
        runtime.AddSingletonFactory<Resource>(ModuleId.FromModuleType<PayloadModule>(), _ =>
        {
            GC.KeepAlive(payload);
            return new Resource();
        }, _ => ValueTask.CompletedTask);
        return weak;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static async Task<(ModuleHost Host, WeakReference Weak)> BuildHostWithRetiredFactory()
    {
        var runtime = new ModuleServiceRuntime();
        var weak = RegisterPayload(runtime);
        var a = ModuleRegistration.For(ModuleDescriptor.For<BaseModule>("base"), () => new BaseModule());
        var b = ModuleRegistration.For(ModuleDescriptor.For<PayloadModule>("payload"), () => new PayloadModule());
        var host = new ModuleHost([a, b], new ModuleHostOptions([typeof(BaseModule), typeof(PayloadModule)]) { Lifetime = runtime });
        (await host.InitializeAsync(TestContext.Current.CancellationToken)).IsSuccess.ShouldBeTrue();
        using (runtime.Root.Borrow<Resource>()) { }
        var result = await host.ChangeAsync(new ModuleChangeRequest(host.Current, [a],
            new ModuleHostOptions([typeof(BaseModule)]) { Lifetime = runtime }), TestContext.Current.CancellationToken);
        result.Status.ShouldBe(ModuleChangeStatus.Committed);
        (await result.Retirement.FinalCompletion).IsSuccess.ShouldBeTrue();
        return (host, weak);
    }

    private sealed class PayloadModule : ModuleBase;
    private sealed class BaseModule : ModuleBase;
    private sealed class SharingModule : ModuleBase
    {
        public override void ConfigureServices(ModuleServiceConfigurationContext context) =>
            context.ManagedServices!.AddScopedFactory<Resource>(ModuleId.FromModuleType<SharingModule>(), _ => new(), _ => ValueTask.CompletedTask);
    }

    [Fact]
    public async Task Transition_receipt_waits_for_already_retiring_scope_reader()
    {
        var runtime = new ModuleServiceRuntime();
        runtime.AddScopedFactory<Resource>(A, _ => new(), _ => ValueTask.CompletedTask);
        runtime.Freeze();
        var scope = runtime.Root.CreateScope();
        using var lease = scope.Borrow<Resource>();
        var scopeRetirement = scope.BeginRetirement();
        var candidate = runtime.CreateCandidate(runtime.Bindings, [A]);
        candidate.Freeze();
        using var transition = runtime.PrepareTransition(runtime.Bindings, candidate);
        var retirement = transition.CompleteRetirement();
        retirement.FinalCompletion.IsCompleted.ShouldBeFalse();
        lease.Dispose();
        (await scopeRetirement.FinalCompletion).IsSuccess.ShouldBeTrue();
        var result = await retirement.FinalCompletion;
        result.IsSuccess.ShouldBeTrue();
        result.Owners.ShouldContain(owner => owner.ModuleId == A);
        await runtime.DisposeAsync();
    }

    [Fact]
    public async Task Transition_receipt_includes_already_failed_scope_owner()
    {
        var runtime = new ModuleServiceRuntime();
        runtime.AddScopedFactory<Resource>(A, _ => new(), _ => ValueTask.FromException(new InvalidOperationException("release failed")));
        runtime.Freeze();
        var scope = runtime.Root.CreateScope();
        using (scope.Borrow<Resource>()) { }
        (await scope.BeginRetirement().FinalCompletion).IsSuccess.ShouldBeFalse();
        var candidate = runtime.CreateCandidate(runtime.Bindings, [A]);
        candidate.Freeze();
        using var transition = runtime.PrepareTransition(runtime.Bindings, candidate);
        var result = await transition.CompleteRetirement().FinalCompletion;
        result.IsSuccess.ShouldBeFalse();
        result.Failures.ShouldContain(failure => failure.ModuleId == A && failure.Stage == "Release");
        runtime.Stop();
        (await runtime.CompleteShutdownAsync()).IsSuccess.ShouldBeFalse();
    }

    [Fact]
    public async Task Transition_suspends_active_dependency_with_already_retiring_consumer()
    {
        var runtime = new ModuleServiceRuntime();
        runtime.AddScopedFactory<Resource>(A, _ => new(), _ => ValueTask.CompletedTask);
        runtime.AddScopedFactory<Consumer>(B, context => new(context.GetRequiredService<Resource>()),
            _ => ValueTask.CompletedTask, dependencies: [typeof(Resource)]);
        runtime.Freeze();
        var source = runtime.Root.CreateScope();
        var target = runtime.Root.CreateScope();
        target.ShareFrom<Resource>(source);
        using var lease = target.Borrow<Consumer>();
        var targetRetirement = target.BeginRetirement();
        var candidate = runtime.CreateCandidate(runtime.Bindings, [A, B]);
        candidate.Freeze();
        using var transition = runtime.PrepareTransition(runtime.Bindings, candidate);
        var retirement = transition.CompleteRetirement();
        retirement.FinalCompletion.IsCompleted.ShouldBeFalse();
        lease.Dispose();
        (await targetRetirement.FinalCompletion).IsSuccess.ShouldBeTrue();
        var result = await retirement.FinalCompletion;
        result.IsSuccess.ShouldBeTrue();
        result.Owners.ShouldContain(owner => owner.ModuleId == A);
        result.Owners.ShouldContain(owner => owner.ModuleId == B);
        await runtime.DisposeAsync();
    }

    private sealed record Consumer(Resource Resource);
    private sealed class Resource;
    private sealed class Other;
}
