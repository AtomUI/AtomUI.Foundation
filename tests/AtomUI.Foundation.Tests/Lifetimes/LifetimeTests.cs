using AtomUI.Lifetimes;
using AtomUI.Modularity;
using Xunit;

namespace AtomUI.Foundation.Tests.Lifetimes;

public class LifetimeTests
{
    private static readonly ModuleId Module = ModuleId.FromSerializedValue("Tests.Lifetimes");

    [Fact]
    public async Task LongCaptureChainRetiresWithoutRecursiveProgression()
    {
        const int ownerCount = 20_000;
        var domain = new LifetimeDomain();
        var scope = domain.CreateScope();
        var releases = 0;
        var owners = new LifetimeOwner<Resource>[ownerCount];
        owners[0] = scope.Own(Module, new Resource(), _ => { releases++; return ValueTask.CompletedTask; });
        for (var index = 1; index < ownerCount; index++)
        {
            owners[index] = scope.Own(Module, new Resource(), _ => { releases++; return ValueTask.CompletedTask; });
            owners[index].Capture(owners[index - 1]);
        }

        var result = await Finish(scope.BeginRetirement());

        Assert.True(result.IsSuccess);
        Assert.Equal(ownerCount, releases);
    }

    [Fact]
    public async Task ResultReaderPreservesOriginalInstanceUntilReturned()
    {
        var domain = new LifetimeDomain();
        var instance = new Resource();
        var owner = domain.CreateScope().Own(Module, instance, value => { value.Released = true; return ValueTask.CompletedTask; });
        var reader = owner.AcquireLease();

        var retirement = owner.BeginRetirement();
        var initial = await retirement.Completion;

        Assert.Same(instance, reader.Value);
        Assert.False(instance.Released);
        Assert.Equal(1, Assert.Single(initial.Owners).Readers);
        Assert.False(retirement.FinalCompletion.IsCompleted);
        Assert.Throws<InvalidOperationException>(() => owner.AcquireLease());
        reader.Dispose();
        reader.Dispose();
        Assert.Throws<ObjectDisposedException>(() => reader.Value);
        Assert.True((await Finish(retirement)).IsSuccess);
        Assert.True(instance.Released);
        Assert.Equal(1, Assert.Single(initial.Owners).Readers);
        Assert.Same(retirement, owner.BeginRetirement());
    }

    [Fact]
    public async Task DomainStopFreezesAllBeforeNotificationsAndDefersRelease()
    {
        var domain = new LifetimeDomain();
        var scope = domain.CreateScope();
        var events = new List<string>();
        LifetimeOwner<Resource>? second = null;
        var first = scope.Own(Module, new Resource(), _ => { events.Add("release-first"); return ValueTask.CompletedTask; }, _ =>
        {
            events.Add("stop-first");
            Assert.Throws<InvalidOperationException>(() => second!.AcquireLease());
            scope.BeginRetirement();
        });
        second = scope.Own(Module, new Resource(), _ => { events.Add("release-second"); return ValueTask.CompletedTask; }, _ => events.Add("stop-second"));

        domain.Stop();

        Assert.Equal(new[] { "stop-first", "stop-second" }, events);
        Assert.Throws<InvalidOperationException>(() => domain.CreateScope());
        Assert.Throws<InvalidOperationException>(() => scope.Own(Module, new Resource(), _ => ValueTask.CompletedTask));
        Assert.Equal(LifetimeOwnerState.Retiring, first.State);
        var retirement = domain.CompleteRetirement();
        Assert.True((await Finish(retirement)).IsSuccess);
        Assert.Equal(4, events.Count);
        Assert.Same(retirement, domain.BeginRetirement());
    }

    [Fact]
    public async Task ReleaseReturnsCaptureBeforeDependencyCanRelease()
    {
        var domain = new LifetimeDomain();
        var scope = domain.CreateScope();
        var dependency = new Resource();
        var released = new List<string>();
        var upstream = scope.Own(Module, dependency, value => { value.Released = true; released.Add("dependency"); return ValueTask.CompletedTask; });
        LifetimeLease<Resource>? capture = null;
        var consumer = scope.Own(Module, new Resource(), _ =>
        {
            released.Add("consumer");
            capture!.Dispose();
            Assert.False(dependency.Released);
            return ValueTask.CompletedTask;
        });
        capture = consumer.Capture(upstream);
        var reader = consumer.AcquireLease();

        var retirement = domain.BeginRetirement();
        var initial = await retirement.Completion;
        Assert.Equal(0, initial.Owners.Single(owner => owner.Id == upstream.Id).Readers);
        Assert.Equal(new[] { upstream.Id }, initial.Owners.Single(owner => owner.Id == consumer.Id).Dependencies);
        Assert.Empty(released);
        reader.Dispose();

        Assert.True((await Finish(retirement)).IsSuccess);
        Assert.Equal(new[] { "consumer", "dependency" }, released);
        Assert.Throws<ObjectDisposedException>(() => capture.Value);
    }

    [Fact]
    public async Task FailedReleaseRetainsReturnedCaptureAndDrainsActualReaders()
    {
        var domain = new LifetimeDomain();
        var scope = domain.CreateScope();
        var dependency = new Resource();
        var cleanupFailure = new InvalidOperationException("consumer cleanup failed");
        var independentReleased = NewSignal();
        var upstream = scope.Own(Module, dependency, value => { value.Released = true; return ValueTask.CompletedTask; });
        LifetimeLease<Resource>? capture = null;
        var attempts = 0;
        var consumer = scope.Own(Module, new Resource(), _ =>
        {
            attempts++;
            capture!.Dispose();
            Assert.False(dependency.Released);
            throw cleanupFailure;
        });
        capture = consumer.Capture(upstream);
        scope.Own(Module, new Resource(), _ => { independentReleased.SetResult(); return ValueTask.CompletedTask; });
        var reader = upstream.AcquireLease();

        var retirement = domain.BeginRetirement();
        await independentReleased.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.False(retirement.FinalCompletion.IsCompleted);
        Assert.Same(dependency, reader.Value);
        Assert.False(dependency.Released);
        reader.Dispose();

        var final = await Finish(retirement);
        Assert.False(final.IsSuccess);
        Assert.Equal(LifetimeOwnerState.CleanupFailed, final.Owners.Single(owner => owner.Id == consumer.Id).State);
        Assert.Equal(LifetimeOwnerState.RetainedByFailedOwner, final.Owners.Single(owner => owner.Id == upstream.Id).State);
        Assert.Equal(new[] { upstream.Id }, final.Owners.Single(owner => owner.Id == consumer.Id).Dependencies);
        Assert.Same(cleanupFailure, Assert.Single(final.Failures).Exception);
        Assert.Equal("Release", Assert.Single(final.Failures).Stage);
        Assert.Same(final, await domain.BeginRetirement().FinalCompletion);
        Assert.Equal(1, attempts);
        Assert.False(dependency.Released);
    }

    [Fact]
    public async Task CaptureAndExplicitReleaseOrderingRejectCombinedCycles()
    {
        var domain = new LifetimeDomain();
        var scope = domain.CreateScope();
        var released = new List<string>();
        var first = scope.Own(Module, new Resource(), _ => { released.Add("first"); return ValueTask.CompletedTask; });
        var second = scope.Own(Module, new Resource(), _ => { released.Add("second"); return ValueTask.CompletedTask; });
        var third = scope.Own(Module, new Resource(), _ => { released.Add("third"); return ValueTask.CompletedTask; });
        first.Capture(second);
        Assert.Throws<InvalidOperationException>(() => second.Capture(first));
        Assert.Throws<InvalidOperationException>(() => first.WaitForRelease(second));
        first.WaitForRelease(third);
        Assert.Throws<InvalidOperationException>(() => third.WaitForRelease(second));
        Assert.Throws<InvalidOperationException>(() => first.Capture(first));
        Assert.Throws<InvalidOperationException>(() => first.WaitForRelease(first));

        Assert.True((await Finish(domain.BeginRetirement())).IsSuccess);
        Assert.Equal(new[] { "third", "first", "second" }, released);
    }

    [Fact]
    public async Task ClosingConsumerScopeReturnsOnlyItsCaptureAndSourceRetirementClosesOtherConsumers()
    {
        var domain = new LifetimeDomain();
        var sourceScope = domain.CreateScope();
        var firstScope = domain.CreateScope();
        var secondScope = domain.CreateScope();
        var source = new Resource();
        var releases = 0;
        var sourceOwner = sourceScope.Own(Module, source, _ => { releases++; return ValueTask.CompletedTask; });
        var first = firstScope.Own(Module, new Resource(), _ => ValueTask.CompletedTask);
        var second = secondScope.Own(Module, new Resource(), _ => ValueTask.CompletedTask);
        first.Capture(sourceOwner);
        second.Capture(sourceOwner);
        var secondReader = second.AcquireLease();

        Assert.True((await Finish(firstScope.BeginRetirement())).IsSuccess);
        Assert.Equal(LifetimeOwnerState.Active, sourceOwner.State);
        Assert.Equal(0, releases);
        using (var sourceReader = sourceOwner.AcquireLease()) Assert.Same(source, sourceReader.Value);
        var retirement = sourceScope.BeginRetirement();
        Assert.Equal(LifetimeOwnerState.Retiring, second.State);
        Assert.Throws<InvalidOperationException>(() => second.AcquireLease());
        Assert.Equal(0, releases);
        secondReader.Dispose();

        Assert.True((await Finish(retirement)).IsSuccess);
        Assert.Equal(1, releases);
    }

    [Fact]
    public async Task DoubleAdoptionDoesNotReleaseOrReplaceOriginalAuthority()
    {
        var domain = new LifetimeDomain();
        var originalScope = domain.CreateScope();
        var aliasScope = domain.CreateScope();
        var value = new Resource();
        var releases = 0;
        var owner = originalScope.Own(Module, value, _ => { releases++; return ValueTask.CompletedTask; });

        Assert.Throws<InvalidOperationException>(() => aliasScope.Own<object>(Module, value, _ => throw new Exception("must never execute")));
        Assert.Equal(0, releases);
        using (var reader = owner.AcquireLease()) Assert.Same(value, reader.Value);
        Assert.True((await Finish(domain.BeginRetirement())).IsSuccess);
        Assert.Equal(1, releases);
    }

    [Fact]
    public async Task SuspendedReleaseDoesNotDelayIndependentCleanup()
    {
        var domain = new LifetimeDomain();
        var scope = domain.CreateScope();
        var releaseStarted = NewSignal();
        var finishRelease = NewSignal();
        var independentReleased = NewSignal();
        scope.Own(Module, new Resource(), async _ => { releaseStarted.SetResult(); await finishRelease.Task; });
        scope.Own(Module, new Resource(), _ => { independentReleased.SetResult(); return ValueTask.CompletedTask; });

        var retirement = scope.BeginRetirement();
        await releaseStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await independentReleased.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.False(retirement.FinalCompletion.IsCompleted);
        finishRelease.SetResult();
        Assert.True((await Finish(retirement)).IsSuccess);
    }

    [Fact]
    public async Task FactoryReservationSurvivesScopeRetirementBeforeResultArrives()
    {
        var domain = new LifetimeDomain();
        var scope = domain.CreateScope();
        var entered = NewSignal();
        var finishFactory = NewSignal();
        LifetimeOwner<Resource>? reserved = null;
        var instance = new Resource();
        var events = new List<string>();
        var creation = Task.Run(() => scope.Create<Resource>(Module, owner =>
        {
            reserved = owner;
            entered.SetResult();
            finishFactory.Task.GetAwaiter().GetResult();
            return instance;
        }, value => { value.Released = true; events.Add("release"); return ValueTask.CompletedTask; }, _ => events.Add("stop")));
        // Also observes a factory exception promptly if reservation itself is broken.
        await Task.WhenAny(entered.Task, creation).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        if (!entered.Task.IsCompleted) await creation;
        Assert.NotNull(reserved);
        Assert.Throws<InvalidOperationException>(() => reserved.AcquireLease());

        var retirement = scope.BeginRetirement();
        var initial = await retirement.Completion;
        Assert.Equal(reserved.Id, Assert.Single(initial.Owners).Id);
        Assert.False(retirement.FinalCompletion.IsCompleted);
        Assert.Empty(events);
        finishFactory.SetResult();
        Assert.Same(reserved, await creation.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));

        Assert.True((await Finish(retirement)).IsSuccess);
        Assert.True(instance.Released);
        Assert.Equal(new[] { "stop", "release" }, events);
    }

    [Fact]
    public async Task FactoryCanCaptureDependenciesAndCannotExposeUnconstructedInstance()
    {
        var domain = new LifetimeDomain();
        var scope = domain.CreateScope();
        var dependency = new Resource();
        var upstream = scope.Own(Module, dependency, value => { value.Released = true; return ValueTask.CompletedTask; });
        var instance = new Resource();
        var owner = scope.Create<Resource>(Module, reserved =>
        {
            Assert.Throws<InvalidOperationException>(() => reserved.AcquireLease());
            Assert.Same(dependency, reserved.Capture(upstream).Value);
            return instance;
        }, _ => { Assert.False(dependency.Released); return ValueTask.CompletedTask; });

        using (var reader = owner.AcquireLease()) Assert.Same(instance, reader.Value);
        Assert.True((await Finish(domain.BeginRetirement())).IsSuccess);
        Assert.True(dependency.Released);
    }

    [Fact]
    public async Task FailedFactoryReturnsProvisionalCapturesWithoutReleasingExistingInstance()
    {
        var domain = new LifetimeDomain();
        var scope = domain.CreateScope();
        var upstream = scope.Own(Module, new Resource(), value => { value.Released = true; return ValueTask.CompletedTask; });
        var exception = new InvalidOperationException("factory failed before ownership handoff");
        var incorrectReleaseCalls = 0;

        Assert.Same(exception, Assert.Throws<InvalidOperationException>(() => scope.Create<Resource>(Module, reserved =>
        {
            reserved.Capture(upstream);
            throw exception;
        }, _ => { incorrectReleaseCalls++; return ValueTask.CompletedTask; })));
        var final = await Finish(domain.BeginRetirement());

        Assert.False(final.IsSuccess);
        Assert.Equal(0, incorrectReleaseCalls);
        Assert.Equal(LifetimeOwnerState.Released, upstream.State);
        Assert.Same(exception, Assert.Single(final.Failures).Exception);
        Assert.Equal("Factory", Assert.Single(final.Failures).Stage);
    }

    [Fact]
    public async Task FailedFactoryPrerequisiteRetainsOwnersWaitingForItsRelease()
    {
        var domain = new LifetimeDomain();
        var scope = domain.CreateScope();
        var failure = new InvalidOperationException("factory failed");
        var dependentReleaseCalls = 0;
        var dependent = scope.Own(Module, new Resource(), _ => { dependentReleaseCalls++; return ValueTask.CompletedTask; });
        LifetimeOwner<Resource>? failed = null;
        Assert.Same(failure, Assert.Throws<InvalidOperationException>(() => scope.Create<Resource>(Module, owner =>
        {
            failed = owner;
            dependent.WaitForRelease(owner);
            throw failure;
        }, _ => ValueTask.CompletedTask)));

        var final = await Finish(domain.BeginRetirement());

        Assert.False(final.IsSuccess);
        Assert.Equal(LifetimeOwnerState.CleanupFailed, failed!.State);
        Assert.Equal(LifetimeOwnerState.RetainedByFailedOwner, dependent.State);
        Assert.Equal(0, dependentReleaseCalls);
    }

    [Fact]
    public async Task NewCaptureFromFailureBlockedOwnerExtendsRetainedClosure()
    {
        var domain = new LifetimeDomain();
        var failedScope = domain.CreateScope();
        var dependentScope = domain.CreateScope();
        var dependencyScope = domain.CreateScope();
        var failure = new InvalidOperationException("release failed");
        var failed = failedScope.Own(Module, new Resource(), _ => throw failure);
        var dependent = dependentScope.Own(Module, new Resource(), _ => ValueTask.CompletedTask);
        dependent.WaitForRelease(failed);

        var failedRetirement = await Finish(failedScope.BeginRetirement());
        Assert.False(failedRetirement.IsSuccess);
        Assert.Equal(LifetimeOwnerState.Active, dependent.State);
        var lateDependency = dependencyScope.Own(Module, new Resource(), _ => ValueTask.CompletedTask);
        dependent.Capture(lateDependency);

        var final = await Finish(domain.BeginRetirement());

        Assert.False(final.IsSuccess);
        Assert.Equal(LifetimeOwnerState.RetainedByFailedOwner, dependent.State);
        Assert.Equal(LifetimeOwnerState.RetainedByFailedOwner, lateDependency.State);
    }

    [Fact]
    public async Task NewReleaseOrderingFromFailureBlockedPrerequisiteExtendsRetainedClosure()
    {
        var domain = new LifetimeDomain();
        var failedScope = domain.CreateScope();
        var dependentScope = domain.CreateScope();
        var orderedScope = domain.CreateScope();
        var failure = new InvalidOperationException("release failed");
        var failed = failedScope.Own(Module, new Resource(), _ => throw failure);
        var dependent = dependentScope.Own(Module, new Resource(), _ => ValueTask.CompletedTask);
        dependent.WaitForRelease(failed);

        var failedRetirement = await Finish(failedScope.BeginRetirement());
        Assert.False(failedRetirement.IsSuccess);
        Assert.Equal(LifetimeOwnerState.Active, dependent.State);
        var ordered = orderedScope.Own(Module, new Resource(), _ => ValueTask.CompletedTask);
        ordered.WaitForRelease(dependent);

        var final = await domain.BeginRetirement().FinalCompletion.WaitAsync(
            TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        Assert.False(final.IsSuccess);
        Assert.Equal(LifetimeOwnerState.RetainedByFailedOwner, dependent.State);
        Assert.Equal(LifetimeOwnerState.RetainedByFailedOwner, ordered.State);
    }

    [Fact]
    public async Task ScopeDisposeAfterDomainFailureReturnsTheStoredFailure()
    {
        var domain = new LifetimeDomain();
        var scope = domain.CreateScope();
        var failure = new InvalidOperationException("cleanup failed");
        scope.Own(Module, new Resource(), _ => throw failure);

        var domainResult = await Finish(domain.BeginRetirement());
        Assert.False(domainResult.IsSuccess);

        var exception = await Assert.ThrowsAsync<LifetimeCleanupException>(() => scope.DisposeAsync().AsTask());

        Assert.Same(domainResult, exception.Result);
        Assert.Same(failure, Assert.Single(exception.Result.Failures).Exception);
    }

    [Fact]
    public async Task FactoryReturningAlreadyOwnedAliasRejectsNewAuthority()
    {
        var domain = new LifetimeDomain();
        var scope = domain.CreateScope();
        var instance = new Resource();
        var releases = 0;
        var original = scope.Own(Module, instance, _ => { releases++; return ValueTask.CompletedTask; });

        Assert.Throws<InvalidOperationException>(() => scope.Create<object>(Module, _ => instance, _ => throw new Exception("alias release must not run")));
        using (var reader = original.AcquireLease()) Assert.Same(instance, reader.Value);
        await Finish(domain.BeginRetirement());
        Assert.Equal(1, releases);
    }

    [Fact]
    public async Task NullFactoryResultIsRecordedWithoutCallingStopOrRelease()
    {
        var domain = new LifetimeDomain();
        var scope = domain.CreateScope();
        var stopCalls = 0;
        var releaseCalls = 0;
        Assert.Throws<InvalidOperationException>(() => scope.Create<Resource>(Module, _ => null!,
            _ => { releaseCalls++; return ValueTask.CompletedTask; }, _ => stopCalls++));

        var final = await Finish(domain.BeginRetirement());
        Assert.False(final.IsSuccess);
        Assert.Equal(0, stopCalls);
        Assert.Equal(0, releaseCalls);
        Assert.Equal(LifetimeOwnerState.CleanupFailed, Assert.Single(final.Owners).State);
    }

    [Fact]
    public async Task CallbacksRunOutsideGateAndStopFailuresDoNotSkipIndependentWork()
    {
        var domain = new LifetimeDomain();
        var scope = domain.CreateScope();
        LifetimeOwner<Resource>? first = null;
        var exception = new InvalidOperationException("stop failed");
        var stops = 0;
        var releases = 0;
        first = scope.Create<Resource>(Module, _ =>
        {
            Task.Run(() => domain.CreateScope()).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken).GetAwaiter().GetResult();
            return new Resource();
        }, async _ =>
        {
            await Task.Run(() => first!.State).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            releases++;
        }, _ =>
        {
            Task.Run(() => first!.State).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken).GetAwaiter().GetResult();
            stops++;
            throw exception;
        });
        scope.Own(Module, new Resource(), _ => { releases++; return ValueTask.CompletedTask; }, _ => stops++);

        var final = await Finish(domain.BeginRetirement());
        Assert.False(final.IsSuccess);
        Assert.Equal(2, stops);
        Assert.Equal(2, releases);
        Assert.All(final.Owners, owner => Assert.Equal(LifetimeOwnerState.Released, owner.State));
        Assert.Same(exception, Assert.Single(final.Failures).Exception);
        Assert.Equal("Stop", Assert.Single(final.Failures).Stage);
    }

    [Fact]
    public async Task ReentrantDomainStopDefersReleasesAlreadySelectedByScopeScheduler()
    {
        var domain = new LifetimeDomain();
        var scope = domain.CreateScope();
        var releases = new List<string>();
        scope.Own(Module, new Resource(), _ =>
        {
            releases.Add("first");
            domain.Stop();
            return ValueTask.CompletedTask;
        });
        scope.Own(Module, new Resource(), _ => { releases.Add("second"); return ValueTask.CompletedTask; });

        var scopeRetirement = scope.BeginRetirement();
        Assert.Equal(new[] { "first" }, releases);
        Assert.False(scopeRetirement.FinalCompletion.IsCompleted);
        Assert.True((await Finish(domain.CompleteRetirement())).IsSuccess);
        Assert.Equal(new[] { "first", "second" }, releases);
    }

    [Fact]
    public async Task LastReaderReturnedDuringStopCannotReleaseBeforeAllNotifications()
    {
        var domain = new LifetimeDomain();
        var scope = domain.CreateScope();
        var events = new List<string>();
        LifetimeLease<Resource>? reader = null;
        var first = scope.Own(Module, new Resource(), _ => { events.Add("release-first"); return ValueTask.CompletedTask; }, _ =>
        {
            events.Add("stop-first");
            reader!.Dispose();
        });
        scope.Own(Module, new Resource(), _ => { events.Add("release-second"); return ValueTask.CompletedTask; }, _ => events.Add("stop-second"));
        reader = first.AcquireLease();

        Assert.True((await Finish(scope.BeginRetirement())).IsSuccess);
        Assert.Equal(new[] { "stop-first", "stop-second", "release-first", "release-second" }, events);
    }

    [Fact]
    public async Task FailedOwnerRetainsTransitiveCaptureAndOrderingClosure()
    {
        var domain = new LifetimeDomain();
        var scope = domain.CreateScope();
        var failure = new InvalidOperationException("cleanup failed");
        var dependency = scope.Own(Module, new Resource(), _ => throw new Exception("retained dependency must not release"));
        var middle = scope.Own(Module, new Resource(), _ => throw new Exception("retained middle must not release"));
        var failed = scope.Own(Module, new Resource(), _ => throw failure);
        var ordered = scope.Own(Module, new Resource(), _ => throw new Exception("failed prerequisite must block release"));
        middle.Capture(dependency);
        failed.Capture(middle);
        ordered.WaitForRelease(failed);

        var final = await Finish(domain.BeginRetirement());
        Assert.False(final.IsSuccess);
        Assert.Same(failure, Assert.Single(final.Failures).Exception);
        Assert.Equal(LifetimeOwnerState.CleanupFailed, failed.State);
        Assert.Equal(LifetimeOwnerState.RetainedByFailedOwner, dependency.State);
        Assert.Equal(LifetimeOwnerState.RetainedByFailedOwner, middle.State);
        Assert.Equal(LifetimeOwnerState.RetainedByFailedOwner, ordered.State);
    }

    [Fact]
    public async Task ScopeRetirementFreezesIncomingConsumerClosureBeforeFirstStop()
    {
        var domain = new LifetimeDomain();
        var sourceScope = domain.CreateScope();
        var consumerScope = domain.CreateScope();
        LifetimeOwner<Resource>? consumer = null;
        var source = sourceScope.Own(Module, new Resource(), _ => ValueTask.CompletedTask, _ =>
            Assert.Throws<InvalidOperationException>(() => consumer!.AcquireLease()));
        consumer = consumerScope.Own(Module, new Resource(), _ => ValueTask.CompletedTask);
        consumer.Capture(source);

        Assert.True((await Finish(sourceScope.BeginRetirement())).IsSuccess);
        Assert.Equal(LifetimeOwnerState.Released, consumer.State);
    }

    [Fact]
    public async Task CrossDomainCaptureAndOrderingRejectWithoutChangingOwners()
    {
        var firstDomain = new LifetimeDomain();
        var secondDomain = new LifetimeDomain();
        var first = firstDomain.CreateScope().Own(Module, new Resource(), _ => ValueTask.CompletedTask);
        var second = secondDomain.CreateScope().Own(Module, new Resource(), _ => ValueTask.CompletedTask);

        Assert.Throws<InvalidOperationException>(() => first.Capture(second));
        Assert.Throws<InvalidOperationException>(() => first.WaitForRelease(second));
        Assert.True((await Finish(firstDomain.BeginRetirement())).IsSuccess);
        Assert.True((await Finish(secondDomain.BeginRetirement())).IsSuccess);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AsyncDisposeReportsCleanupFailureWithStableRetirementResult(bool disposeDomain)
    {
        var domain = new LifetimeDomain();
        var scope = domain.CreateScope();
        var cleanupFailure = new InvalidOperationException("cleanup failed");
        var releases = 0;
        scope.Own(Module, new Resource(), _ => { releases++; throw cleanupFailure; });
        var retirement = disposeDomain ? domain.BeginRetirement() : scope.BeginRetirement();
        var final = await Finish(retirement);
        Assert.False(final.IsSuccess);

        var exception = await Assert.ThrowsAsync<LifetimeCleanupException>(async () =>
        {
            if (disposeDomain) await domain.DisposeAsync();
            else await scope.DisposeAsync();
        });
        Assert.Same(final, exception.Result);
        Assert.Same(cleanupFailure, Assert.Single(exception.Result.Failures).Exception);
        Assert.Equal(1, releases);
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static Task<LifetimeCloseResult> Finish(LifetimeRetirement retirement) =>
        retirement.FinalCompletion.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

    private sealed class Resource
    {
        public bool Released { get; set; }
    }
}
