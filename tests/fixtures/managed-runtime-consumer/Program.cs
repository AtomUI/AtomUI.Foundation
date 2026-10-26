using System;
using System.Collections.Generic;
using System.Linq;
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
#if NET8_0
        Console.WriteLine("MANAGED_RUNTIME_CONSUMER_OK net8.0");
#elif NET10_0
        Console.WriteLine("MANAGED_RUNTIME_CONSUMER_OK net10.0");
#else
        throw new PlatformNotSupportedException("This consumer fixture targets net8.0 and net10.0.");
#endif
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

    private static void Ensure(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
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
}
