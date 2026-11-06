using AtomUI.Lifetimes;
using AtomUI.Modularity;
using AtomUI.Modularity.Services;
using Xunit;

namespace AtomUI.Foundation.Tests.Optimization;

/// <summary>
/// Fixed-seed interleaving guards required by the 2026-11-05 optimization proposal (W4).
/// They pass on the current implementation and must keep passing through P1-P3.
/// A failure message always carries the seed so the interleaving can be replayed.
/// </summary>
public sealed class ConcurrencyRegressionTests
{
    private static readonly ModuleId Owner = ModuleId.FromSerializedValue("Tests.Concurrency");

    // Nondeterministically red until O16 is fixed: an admitted Scoped factory whose scope
    // starts retiring mid-construction reports a spurious Factory failure. The deterministic
    // reproduction is P0RedBaselineTests.O16_*. Remove this trait together with that fix.
    [Theory]
    [Trait("Category", "P0Red")]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(20261105)]
    public async Task Borrow_and_scope_retirement_interleavings_release_each_owner_once(int seed)
    {
        var runtime = new ModuleServiceRuntime();
        var created = 0;
        var released = 0;
        var violations = 0;
        runtime.AddSingletonFactory<Shared>(Owner, _ => new(), Release);
        runtime.AddScopedFactory<Scoped>(Owner, context =>
        {
            Interlocked.Increment(ref created);
            return new(context.GetRequiredService<Shared>());
        }, Release, dependencies: [typeof(Shared)]);
        runtime.Freeze();
        var token = TestContext.Current.CancellationToken;

        for (var round = 0; round < 64; round++)
        {
            var scope = runtime.Root.CreateScope();
            var random = new Random(HashCode.Combine(seed, round));
            var retireAt = random.Next(0, 16);
            var workers = Enumerable.Range(0, 4).Select(worker =>
            {
                var local = new Random(HashCode.Combine(seed, round, worker));
                return Task.Run(() =>
                {
                    for (var step = 0; step < 16; step++)
                    {
                        if (worker == 0 && step == retireAt) scope.BeginRetirement();
                        LifetimeLease<Scoped>? lease = null;
                        try { lease = scope.Borrow<Scoped>(); }
                        catch (ObjectDisposedException) { }
                        catch (InvalidOperationException) { }
                        if (lease is null) continue;
                        for (var spin = local.Next(0, 4); spin > 0; spin--)
                        {
                            if (lease.Value.Released || lease.Value.Dependency.Released) Interlocked.Increment(ref violations);
                            Thread.Yield();
                        }
                        lease.Dispose();
                    }
                }, token);
            }).ToArray();
            await Task.WhenAll(workers);
            var result = await scope.BeginRetirement().FinalCompletion.WaitAsync(TimeSpan.FromSeconds(10), token);
            Assert.True(result.IsSuccess, $"Scope retirement failed (seed={seed}, round={round}): " + Describe(result));
        }

        await runtime.DisposeAsync();
        Assert.Equal(0, violations);
        Assert.Equal(created + 1, Volatile.Read(ref released));

        ValueTask Release(Resource resource)
        {
            if (resource.Released) Interlocked.Increment(ref violations);
            resource.Released = true;
            Interlocked.Increment(ref released);
            return ValueTask.CompletedTask;
        }
    }

    [Theory]
    [InlineData(3)]
    [InlineData(20261105)]
    public async Task Module_changes_and_view_readers_never_observe_a_shutdown_module(int seed)
    {
        var token = TestContext.Current.CancellationToken;
        var instances = new List<Feature>();
        var core = ModuleRegistration.For(ModuleDescriptor.For<Core>("Core"), () => new Core());
        var feature = ModuleRegistration.For(
            ModuleDescriptor.For<Feature>("Feature", [ModuleDependencyDescriptor.Required<Core>()]),
            () => { var instance = new Feature(); lock (instances) instances.Add(instance); return instance; });
        var host = new ModuleHost([core, feature], new ModuleHostOptions([typeof(Core), typeof(Feature)]));
        Assert.True((await host.InitializeAsync(token)).IsSuccess);

        var violations = 0;
        using var stop = new CancellationTokenSource();
        var readers = Enumerable.Range(0, 3).Select(reader => Task.Run(() =>
        {
            var local = new Random(HashCode.Combine(seed, reader));
            while (!stop.IsCancellationRequested)
            {
                ModuleHostLease lease;
                try { lease = host.AcquireView(); }
                catch (InvalidOperationException) { Thread.Yield(); continue; }
                using (lease)
                    for (var spin = local.Next(1, 4); spin > 0; spin--)
                    {
                        if (lease.View.Modules.Any(entry => entry.Phase == ModuleHostPhase.Shutdown))
                            Interlocked.Increment(ref violations);
                        Thread.Yield();
                    }
            }
        }, token)).ToArray();

        var retirements = new List<ModuleChangeRetirement>();
        for (var change = 0; change < 24; change++)
        {
            var roots = change % 2 == 0 ? new[] { typeof(Core) } : new[] { typeof(Core), typeof(Feature) };
            var result = await host.ChangeAsync(new(host.Current, [core, feature], new ModuleHostOptions(roots)), token);
            Assert.True(result.IsCommitted, $"Change {change} was not committed (seed={seed}): {result.Status}.");
            retirements.Add(result.Retirement);
        }

        stop.Cancel();
        await Task.WhenAll(readers);
        foreach (var retirement in retirements)
            Assert.True((await retirement.FinalCompletion.WaitAsync(TimeSpan.FromSeconds(10), token)).IsSuccess);
        Assert.True((await host.ShutdownAsync(token)).IsSuccess);

        Assert.Equal(0, violations);
        Assert.Equal(13, instances.Count);
        Assert.All(instances, instance => Assert.Equal(1, instance.Shutdowns));
    }

    private static string Describe(LifetimeCloseResult result) =>
        string.Join("; ", result.Owners.Select(owner => $"owner {owner.Id} {owner.State} readers={owner.Readers} constructing={owner.IsConstructing} deps=[{string.Join(',', owner.Dependencies)}]"))
        + " | failures: " + string.Join("; ", result.Failures.Select(failure => $"{failure.OwnerId} {failure.Stage} {failure.Exception.GetType().Name}: {failure.Exception.Message}"));

    private class Resource
    {
        public volatile bool Released;
    }

    private sealed class Shared : Resource;

    private sealed class Scoped(Shared dependency) : Resource
    {
        public Shared Dependency { get; } = dependency;
    }

    private sealed class Core : ModuleBase;

    private sealed class Feature : ModuleBase
    {
        public int Shutdowns;
        public override void Shutdown(ModuleShutdownContext context) => Interlocked.Increment(ref Shutdowns);
    }
}
