using System.Runtime.CompilerServices;
using AtomUI.Lifetimes;
using AtomUI.Modularity;
using Xunit;

namespace AtomUI.Foundation.Tests.Lifetimes;

public sealed class LifetimeRetentionTests
{
    private static readonly ModuleId Owner = ModuleId.FromSerializedValue("LifetimeRetentionTests/Owner");

    [Fact]
    public async Task Completed_scope_retirement_releases_domain_bookkeeping()
    {
        var domain = new LifetimeDomain();
        var references = new List<WeakReference[]>();
        for (var index = 0; index < 64; index++)
        {
            references.Add(await CreateAndRetireScopeAsync(domain));
        }

        CollectUntilStable();

        foreach (var reference in references.SelectMany(static set => set))
        {
            Assert.False(reference.IsAlive);
        }

        await domain.DisposeAsync();
    }

    [Fact]
    public async Task Factory_failures_before_handoff_do_not_leave_owner_records()
    {
        var domain = new LifetimeDomain();
        var scope = domain.CreateScope();
        for (var index = 0; index < 64; index++)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                scope.Create<Probe>(Owner, static _ => throw new InvalidOperationException("construction failed"),
                    static _ => ValueTask.CompletedTask);
                await Task.CompletedTask;
            });
        }

        var result = await scope.BeginRetirement().FinalCompletion;

        Assert.False(result.IsSuccess);
        Assert.Equal(64, result.Failures.Count);
        Assert.All(result.Owners, owner => Assert.Equal(LifetimeOwnerState.CleanupFailed, owner.State));
        await domain.DisposeAsync();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference[]> CreateAndRetireScopeAsync(LifetimeDomain domain)
    {
        var scope = domain.CreateScope();
        var owner = scope.Own(Owner, new Probe(), static _ => ValueTask.CompletedTask);
        var retirement = scope.BeginRetirement();
        Assert.True((await retirement.FinalCompletion).IsSuccess);

        return
        [
            new WeakReference(scope),
            new WeakReference(owner),
            new WeakReference(retirement),
        ];
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
