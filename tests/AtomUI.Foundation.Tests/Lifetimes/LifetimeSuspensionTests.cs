using System.Reflection;
using AtomUI.Lifetimes;
using AtomUI.Modularity;
using Xunit;

namespace AtomUI.Foundation.Tests.Lifetimes;

public class LifetimeSuspensionTests
{
    private static object Invoke(LifetimeDomain domain, string method, object argument) =>
        typeof(LifetimeDomain).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null, types: [method == "PrepareRetirement" ? typeof(IEnumerable<LifetimeOwner>) : typeof(LifetimeRetirement)], modifiers: null)!
            .Invoke(domain, [argument])!;

    private static readonly ModuleId Module = ModuleId.FromSerializedValue("Tests.Suspension");
    private sealed class Resource;
    private static LifetimeOwner<Resource> Own(LifetimeScope scope, Func<Resource, ValueTask>? release = null, Action<Resource>? stop = null) =>
        scope.Own(Module, new Resource(), release ?? (_ => ValueTask.CompletedTask), stop);
    private static Task<LifetimeCloseResult> Finish(LifetimeRetirement retirement) =>
        retirement.FinalCompletion.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

    [Fact]
    public void SuspensionFreezesConsumerClosureAndResumesIndependently()
    {
        var domain = new LifetimeDomain();
        var scope = domain.CreateScope();
        var dependency = Own(scope);
        var consumer = Own(scope);
        var other = Own(scope);
        consumer.Capture(dependency);
        using var existing = dependency.AcquireLease();
        using var first = domain.Suspend([dependency]);
        using var second = domain.Suspend([consumer]);
        Assert.Equal(new LifetimeOwner[] { dependency, consumer }, first.Owners);
        Assert.Equal(LifetimeOwnerState.Active, dependency.State);
        Assert.NotNull(existing.Value);
        Assert.Throws<InvalidOperationException>(() => dependency.AcquireLease());
        Assert.Throws<InvalidOperationException>(() => consumer.Capture(other));
        Assert.Throws<InvalidOperationException>(() => other.Capture(dependency));
        first.Dispose();
        using var reopened = dependency.AcquireLease();
        Assert.Throws<InvalidOperationException>(() => consumer.AcquireLease());
        second.Dispose();
        using var reopenedConsumer = consumer.AcquireLease();
    }

    [Fact]
    public void CrossDomainValidationDoesNotPartiallySuspend()
    {
        var domain = new LifetimeDomain();
        var owner = Own(domain.CreateScope());
        var foreign = Own(new LifetimeDomain().CreateScope());
        Assert.Throws<InvalidOperationException>(() => domain.Suspend([owner, foreign]));
        using var reader = owner.AcquireLease();
    }

    [Fact]
    public async Task Active_seed_can_pause_its_already_retiring_consumer_without_reopening_it()
    {
        var domain = new LifetimeDomain();
        var dependency = Own(domain.CreateScope());
        var consumerScope = domain.CreateScope();
        var consumer = Own(consumerScope);
        consumer.Capture(dependency);
        var reader = consumer.AcquireLease();
        var previous = consumerScope.BeginRetirement();
        Assert.Equal(LifetimeOwnerState.Active, dependency.State);
        Assert.Equal(LifetimeOwnerState.Retiring, consumer.State);
        Assert.Throws<InvalidOperationException>(() => domain.Suspend([consumer]));
        using (var suspension = domain.Suspend([dependency]))
        {
            Assert.Contains(consumer, suspension.Owners);
            Assert.Throws<InvalidOperationException>(() => dependency.AcquireLease());
        }
        using (var reopened = dependency.AcquireLease()) Assert.NotNull(reopened.Value);
        Assert.Equal(LifetimeOwnerState.Retiring, consumer.State);
        reader.Dispose();
        Assert.True((await Finish(previous)).IsSuccess);
        Assert.True((await Finish(domain.BeginRetirement())).IsSuccess);
    }

    [Fact]
    public async Task StopNotifiesWholeClosureAndCompleteOpensReleaseAfterReaderReturns()
    {
        var domain = new LifetimeDomain();
        var scope = domain.CreateScope();
        var events = new List<string>();
        LifetimeLease<Resource>? reader = null;
        LifetimeOwner<Resource>? consumer = null;
        var dependency = Own(scope, _ => { events.Add("release dependency"); return ValueTask.CompletedTask; }, _ =>
        {
            events.Add("stop dependency");
            reader!.Dispose();
            Assert.Throws<InvalidOperationException>(() => consumer!.AcquireLease());
        });
        consumer = Own(scope, _ => { events.Add("release consumer"); return ValueTask.CompletedTask; }, _ => events.Add("stop consumer"));
        consumer.Capture(dependency);
        reader = dependency.AcquireLease();
        using var suspension = domain.Suspend([dependency]);
        suspension.Stop();
        Assert.Equal(new[] { "stop dependency", "stop consumer" }, events);
        var retirement = suspension.CompleteRetirement();
        Assert.Same(retirement, suspension.CompleteRetirement());
        Assert.True((await Finish(retirement)).IsSuccess);
        Assert.Equal(new[] { "stop dependency", "stop consumer", "release consumer", "release dependency" }, events);
    }

    [Fact]
    public async Task OverlappingStoppedSuspensionsEachHoldReleaseBarrier()
    {
        var domain = new LifetimeDomain();
        var releases = 0;
        var owner = Own(domain.CreateScope(), _ => { releases++; return ValueTask.CompletedTask; });
        using var first = domain.Suspend([owner]);
        using var second = domain.Suspend([owner]);
        first.Stop();
        second.Stop();
        var retirement = first.CompleteRetirement();
        Assert.Equal(0, releases);
        Assert.False(retirement.FinalCompletion.IsCompleted);
        second.Dispose();
        Assert.Equal(0, releases);
        second.CompleteRetirement();
        Assert.True((await Finish(retirement)).IsSuccess);
        Assert.Equal(1, releases);
    }

    [Fact]
    public async Task SuspensionTicketKeepsMembersRetiredByAnotherOperation()
    {
        var domain = new LifetimeDomain();
        var owner = Own(domain.CreateScope());
        using var suspension = domain.Suspend([owner]);
        Assert.True((await Finish(owner.BeginRetirement())).IsSuccess);
        var result = await Finish(suspension.CompleteRetirement());
        Assert.Equal(owner.Id, Assert.Single(result.Owners).Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentCallsWaitForAllStopNotifications(bool complete)
    {
        var domain = new LifetimeDomain();
        var scope = domain.CreateScope();
        using var entered = new ManualResetEventSlim();
        using var proceed = new ManualResetEventSlim();
        using var secondEntered = new ManualResetEventSlim();
        var secondStopped = false;
        var first = Own(scope, stop: _ => { entered.Set(); proceed.Wait(TestContext.Current.CancellationToken); });
        var second = Own(scope, stop: _ => secondStopped = true);
        using var suspension = domain.Suspend([first, second]);
        var firstCall = Task.Run(suspension.Stop, TestContext.Current.CancellationToken);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        var secondCall = Task.Run(() =>
        {
            secondEntered.Set();
            if (complete) suspension.CompleteRetirement();
            else suspension.Stop();
        }, TestContext.Current.CancellationToken);
        Assert.True(secondEntered.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        try
        {
            await Assert.ThrowsAsync<TimeoutException>(() => secondCall.WaitAsync(TimeSpan.FromMilliseconds(200), TestContext.Current.CancellationToken));
            Assert.False(secondStopped);
        }
        finally { proceed.Set(); }
        await Task.WhenAll(firstCall, secondCall).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.True(secondStopped);
        Assert.True((await Finish(suspension.CompleteRetirement())).IsSuccess);
    }

    [Fact]
    public async Task StopCallbackCanReenterAndTicketWaitsForNotifications()
    {
        var domain = new LifetimeDomain();
        var scope = domain.CreateScope();
        LifetimeSuspension? suspension = null;
        LifetimeRetirement? ticket = null;
        var secondStopped = false;
        var first = Own(scope, stop: _ =>
        {
            suspension!.Stop();
            ticket = suspension.CompleteRetirement();
            Assert.False(ticket.Completion.IsCompleted);
            Assert.False(secondStopped);
        });
        var second = Own(scope, stop: _ => secondStopped = true);
        using (suspension = domain.Suspend([first, second]))
        {
            suspension.Stop();
            Assert.True(secondStopped);
            Assert.Same(ticket, suspension.CompleteRetirement());
            Assert.True((await Finish(ticket!)).IsSuccess);
        }
    }

    [Fact]
    public async Task LeaseDelaysReleaseAfterComplete()
    {
        var domain = new LifetimeDomain();
        var owner = Own(domain.CreateScope());
        var reader = owner.AcquireLease();
        using var suspension = domain.Suspend([owner]);
        var retirement = suspension.CompleteRetirement();
        Assert.False(retirement.FinalCompletion.IsCompleted);
        Assert.NotNull(reader.Value);
        reader.Dispose();
        Assert.True((await Finish(retirement)).IsSuccess);
    }

    [Fact]
    public async Task ReservedFactoryCanHandoffAfterStop()
    {
        var domain = new LifetimeDomain();
        var scope = domain.CreateScope();
        var entered = new TaskCompletionSource<LifetimeOwner<Resource>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var proceed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var events = new List<string>();
        var creation = Task.Run(() => scope.Create<Resource>(Module, owner =>
        {
            entered.SetResult(owner);
            proceed.Task.GetAwaiter().GetResult();
            return new Resource();
        }, _ => { events.Add("release"); return ValueTask.CompletedTask; }, _ => events.Add("stop")), TestContext.Current.CancellationToken);
        var reserved = await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        using var suspension = domain.Suspend([reserved]);
        var retirement = suspension.CompleteRetirement();
        Assert.False(retirement.FinalCompletion.IsCompleted);
        proceed.SetResult();
        Assert.Same(reserved, await creation.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.True((await Finish(retirement)).IsSuccess);
        Assert.Equal(new[] { "stop", "release" }, events);
    }

    [Fact]
    public async Task BatchRetirementIncludesFailedFactoryReservation()
    {
        var domain = new LifetimeDomain();
        var scope = domain.CreateScope();
        LifetimeOwner<Resource>? failed = null;
        var error = new InvalidOperationException("factory failed");
        Assert.Same(error, Assert.Throws<InvalidOperationException>(() => scope.Create<Resource>(Module, owner =>
        {
            failed = owner;
            throw error;
        }, _ => ValueTask.CompletedTask)));
        var ticket = (LifetimeRetirement)Invoke(domain, "PrepareRetirement", new LifetimeOwner[] { failed! });
        Invoke(domain, "DispatchRetirement", ticket);
        Assert.False(ticket.FinalCompletion.IsCompleted);
        Assert.Same(ticket, Invoke(domain, "CompleteRetirement", ticket));
        var result = await Finish(ticket);
        Assert.Equal(failed!.Id, Assert.Single(result.Owners).Id);
        Assert.Same(error, Assert.Single(result.Failures).Exception);
    }

    [Fact]
    public async Task BatchTicketCanBePublishedBeforeReentrantStopCallback()
    {
        var domain = new LifetimeDomain();
        var scope = domain.CreateScope();
        LifetimeRetirement? published = null;
        LifetimeRetirement? observed = null;
        var released = false;
        var first = Own(scope, _ => { released = true; return ValueTask.CompletedTask; }, _ =>
        {
            observed = (LifetimeRetirement)Invoke(domain, "CompleteRetirement", published!);
            Assert.False(released);
            Assert.False(published!.Completion.IsCompleted);
        });
        var ticket = (LifetimeRetirement)Invoke(domain, "PrepareRetirement", new LifetimeOwner[] { first });
        published = ticket;
        Assert.Throws<InvalidOperationException>(() => first.AcquireLease());
        Invoke(domain, "DispatchRetirement", ticket);
        Assert.Same(ticket, observed);
        Assert.Same(ticket, Invoke(domain, "CompleteRetirement", ticket));
        Assert.True((await Finish(ticket)).IsSuccess);
        Assert.True(released);
    }

    [Fact]
    public async Task FailedCandidateKeepsCaptureWithoutClosingSharedService()
    {
        var domain = new LifetimeDomain();
        var shared = Own(domain.CreateScope());
        var candidate = Own(domain.CreateScope(), _ => throw new InvalidOperationException("failed"));
        candidate.Capture(shared);
        using var suspension = domain.Suspend([candidate]);
        Assert.False((await Finish(suspension.CompleteRetirement())).IsSuccess);
        Assert.Equal(LifetimeOwnerState.Active, shared.State);
        using var reader = shared.AcquireLease();
    }
}
