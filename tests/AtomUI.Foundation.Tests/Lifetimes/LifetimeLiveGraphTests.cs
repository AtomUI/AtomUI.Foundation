using System.Runtime.CompilerServices;
using AtomUI.Lifetimes;
using AtomUI.Modularity;
using Xunit;

namespace AtomUI.Foundation.Tests.Lifetimes;

public sealed class LifetimeLiveGraphTests
{
    private static readonly ModuleId Module = ModuleId.FromSerializedValue("LifetimeLiveGraphTests/Owner");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Domain_retirement_preserves_pruned_failure_facts_without_retaining_local_owner(bool factoryFailure)
    {
        var domain = new LifetimeDomain();
        var failure = new InvalidOperationException(factoryFailure ? "factory failed" : "stop failed");
        var releases = 0;
        var (ownerId, references) = await RetireFailedScopeAsync(domain, factoryFailure, failure, () => releases++);

        CollectUntilStable();
        Assert.All(references, reference => Assert.False(reference.IsAlive));

        var independent = domain.CreateScope();
        independent.Own(Module, new Probe(), static _ => ValueTask.CompletedTask);
        Assert.True((await independent.BeginRetirement().FinalCompletion).IsSuccess);

        var retirement = domain.BeginRetirement();
        var result = await retirement.FinalCompletion;

        Assert.False(result.IsSuccess);
        var owner = Assert.Single(result.Owners);
        Assert.Equal(ownerId, owner.Id);
        Assert.Equal(factoryFailure ? LifetimeOwnerState.CleanupFailed : LifetimeOwnerState.Released, owner.State);
        Assert.Empty(owner.Dependencies);
        var recordedFailure = Assert.Single(result.Failures);
        Assert.Equal(ownerId, recordedFailure.OwnerId);
        Assert.Equal(factoryFailure ? "Factory" : "Stop", recordedFailure.Stage);
        Assert.Same(failure, recordedFailure.Exception);
        Assert.Same(result, await domain.BeginRetirement().FinalCompletion);
        Assert.Equal(factoryFailure ? 0 : 1, releases);
    }

    [Fact]
    public async Task Returning_active_capture_shrinks_failure_closure_without_releasing_failed_root()
    {
        var domain = new LifetimeDomain();
        var scope = domain.CreateScope();
        var retainedReleases = 0;
        var independentReleases = 0;
        var failedReleases = 0;
        var failure = new InvalidOperationException("failed root");
        var retained = scope.Own(Module, new Probe(), _ => { retainedReleases++; return ValueTask.CompletedTask; });
        var failed = scope.Own(Module, new Probe(), _ => { failedReleases++; throw failure; });
        failed.Capture(retained);
        Assert.False((await failed.BeginRetirement().FinalCompletion).IsSuccess);
        Assert.Equal(LifetimeOwnerState.Active, retained.State);

        var independent = scope.Own(Module, new Probe(), _ => { independentReleases++; return ValueTask.CompletedTask; });
        var capture = retained.Capture(independent);
        capture.Dispose();

        var result = await independent.BeginRetirement().FinalCompletion.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, independentReleases);
        Assert.Equal(0, retainedReleases);
        Assert.Equal(1, failedReleases);
        Assert.Equal(LifetimeOwnerState.CleanupFailed, failed.State);
        var final = await domain.BeginRetirement().FinalCompletion;
        Assert.False(final.IsSuccess);
        Assert.Same(failure, Assert.Single(final.Failures).Exception);
        Assert.Equal(LifetimeOwnerState.RetainedByFailedOwner, retained.State);
        Assert.Equal(0, retainedReleases);
        Assert.Equal(1, failedReleases);
    }

    [Fact]
    public async Task Successful_prerequisites_are_collectible_while_dependent_remains_active()
    {
        var domain = new LifetimeDomain();
        var scope = domain.CreateScope();
        var dependent = scope.Own(Module, new Probe(), static _ => ValueTask.CompletedTask);
        var references = new List<WeakReference[]>();
        for (var index = 0; index < 128; index++)
            references.Add(await CreateAndRetirePrerequisiteAsync(scope, dependent));

        CollectUntilStable();

        Assert.Equal(LifetimeOwnerState.Active, dependent.State);
        Assert.All(references.SelectMany(static set => set), reference => Assert.False(reference.IsAlive));
        var final = await dependent.BeginRetirement().FinalCompletion;
        Assert.True(final.IsSuccess);
        Assert.Empty(Assert.Single(final.Owners).Dependencies);
        await domain.DisposeAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Shrinking_unrelated_failure_closure_preserves_returned_capture_until_release_succeeds(bool failRelease)
    {
        var domain = new LifetimeDomain();
        var scope = domain.CreateScope();
        var retained = scope.Own(Module, new Probe(), static _ => ValueTask.CompletedTask);
        var failed = scope.Own(Module, new Probe(), static _ => throw new InvalidOperationException("first root"));
        failed.Capture(retained);
        await failed.BeginRetirement().FinalCompletion;
        var temporary = scope.Own(Module, new Probe(), static _ => ValueTask.CompletedTask);
        var temporaryCapture = retained.Capture(temporary);

        var releaseEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dependencyReleases = 0;
        var dependency = scope.Own(Module, new Probe(), _ => { dependencyReleases++; return ValueTask.CompletedTask; });
        LifetimeLease<Probe>? capture = null;
        var consumer = scope.Own(Module, new Probe(), async _ =>
        {
            capture!.Dispose();
            releaseEntered.SetResult();
            await finishRelease.Task;
            if (failRelease) throw new InvalidOperationException("second root");
        });
        capture = consumer.Capture(dependency);
        var retirement = dependency.BeginRetirement();
        await releaseEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        temporaryCapture.Dispose();
        Assert.Equal(0, dependencyReleases);
        Assert.False(retirement.FinalCompletion.IsCompleted);
        finishRelease.SetResult();
        var result = await retirement.FinalCompletion.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(!failRelease, result.IsSuccess);
        Assert.Equal(failRelease ? 0 : 1, dependencyReleases);
        await domain.BeginRetirement().FinalCompletion;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<(long OwnerId, WeakReference[] References)> RetireFailedScopeAsync(
        LifetimeDomain domain, bool factoryFailure, Exception failure, Action released)
    {
        var scope = domain.CreateScope();
        LifetimeOwner<Probe>? owner = null;
        if (factoryFailure)
        {
            Assert.Same(failure, Assert.Throws<InvalidOperationException>(() => scope.Create<Probe>(Module, reserved =>
            {
                owner = reserved;
                throw failure;
            }, _ => { released(); return ValueTask.CompletedTask; })));
        }
        else
        {
            owner = scope.Own(Module, new Probe(), _ => { released(); return ValueTask.CompletedTask; }, _ => throw failure);
        }

        var retirement = scope.BeginRetirement();
        var result = await retirement.FinalCompletion;
        Assert.False(result.IsSuccess);
        Assert.Same(failure, Assert.Single(result.Failures).Exception);
        return (owner!.Id, [new WeakReference(scope), new WeakReference(owner), new WeakReference(retirement)]);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference[]> CreateAndRetirePrerequisiteAsync(LifetimeScope scope, LifetimeOwner dependent)
    {
        var prerequisite = scope.Own(Module, new Probe(), static _ => ValueTask.CompletedTask);
        dependent.WaitForRelease(prerequisite);
        var retirement = prerequisite.BeginRetirement();
        Assert.True((await retirement.FinalCompletion).IsSuccess);
        return [new WeakReference(prerequisite), new WeakReference(retirement)];
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CollectUntilStable()
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
    }

    private sealed class Probe;
}
