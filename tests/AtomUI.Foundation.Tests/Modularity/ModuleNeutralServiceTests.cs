using AtomUI.Modularity;
using Xunit;

namespace AtomUI.Foundation.Tests.Modularity;

public sealed class ModuleNeutralServiceTests
{
    [Fact]
    public void TypeDescriptors_AreExportableWithoutConstructingOrResolvingInstances()
    {
        var services = new ModuleServiceCollection();
        services.AddSingleton<IService, UnconstructibleService>();
        services.AddScoped<ScopedService, ScopedService>();
        services.AddTransient<TransientService, TransientService>();
        services.Freeze();
        services.Freeze();

        Assert.Equal(new[] { ModuleServiceLifetime.Singleton, ModuleServiceLifetime.Scoped, ModuleServiceLifetime.Transient }, services.Descriptors.Select(descriptor => descriptor.Lifetime));
        Assert.All(services.Descriptors, descriptor => { Assert.NotNull(descriptor.ImplementationType); Assert.Null(descriptor.Instance); });
        Assert.Throws<InvalidOperationException>(() => services.GetRequiredService<IService>());
        Assert.Throws<InvalidOperationException>(() => services.AddSingleton(1));
        Assert.Throws<InvalidOperationException>(() => services.Remove<IService>());
        Assert.Throws<InvalidOperationException>(services.Clear);
    }

    [Fact]
    public void InstanceRegistration_PreservesValuesAndDelegatesWithoutCallingDelegate()
    {
        var calls = 0;
        Func<int> instance = () => { calls++; return 7; };
        var services = new ModuleServiceCollection();
        services.AddSingleton(instance);
        services.AddSingleton(42);
        services.Freeze();

        Assert.Same(instance, services.GetRequiredService<Func<int>>());
        Assert.Equal(42, services.GetRequiredService<int>());
        Assert.Equal(0, calls);
        Assert.All(services.Descriptors, descriptor => { Assert.Null(descriptor.ImplementationType); Assert.NotNull(descriptor.Instance); });
    }

    [Fact]
    public void ReplacementRemovalAndClear_KeepLastRegistrationAuthoritative()
    {
        var services = new ModuleServiceCollection();
        services.AddSingleton<IService>(new Service());
        services.AddTransient<IService, Service>();
        Assert.Single(services.Descriptors);
        Assert.Throws<InvalidOperationException>(() => services.GetRequiredService<IService>());
        var replacement = new Service();
        services.ReplaceSingleton<IService>(replacement);
        Assert.Same(replacement, services.GetRequiredService<IService>());
        Assert.Equal(ModuleServiceLifetime.Singleton, Assert.Single(services.Descriptors).Lifetime);
        Assert.True(services.Remove<IService>());
        Assert.False(services.Remove<IService>());
        Assert.Empty(services.Descriptors);
        services.AddSingleton(12);
        services.Clear();
        Assert.Empty(services.Descriptors);
        Assert.Throws<InvalidOperationException>(() => services.GetRequiredService<int>());
    }

    [Fact]
    public void Descriptor_RequiresExactlyOneAssignableImplementationOrInstance()
    {
        Assert.Throws<ArgumentException>(() => new ModuleServiceDescriptor(typeof(IService), ModuleServiceLifetime.Singleton));
        Assert.Throws<ArgumentException>(() => new ModuleServiceDescriptor(typeof(IService), ModuleServiceLifetime.Singleton, typeof(Service), new Service()));
        Assert.Throws<ArgumentException>(() => new ModuleServiceDescriptor(typeof(IService), ModuleServiceLifetime.Singleton, typeof(string)));
        Assert.Throws<ArgumentException>(() => new ModuleServiceDescriptor(typeof(IService), ModuleServiceLifetime.Singleton, instance: new object()));
    }

    [Fact]
    public async Task NeutralHost_LeavesCallerOwnedInstanceAndModuleDisposalUnderCallerControl()
    {
        var instance = new CallerOwnedService();
        var module = new NeutralModule(instance);
        var host = new ModuleHost([ModuleRegistration.For(ModuleDescriptor.For<NeutralModule>("neutral"), () => module)], new ModuleHostOptions([typeof(NeutralModule)]));
        Assert.True((await host.InitializeAsync(TestContext.Current.CancellationToken)).IsSuccess);

        Assert.True((await host.ShutdownAsync(TestContext.Current.CancellationToken)).IsSuccess);

        Assert.Same(instance, host.Services.GetRequiredService<CallerOwnedService>());
        Assert.Equal(0, instance.Disposals);
        Assert.Equal(0, module.Disposals);
        Assert.Equal(1, module.Shutdowns);
    }

    private interface IService;
    private sealed class Service : IService;
    private sealed class ScopedService;
    private sealed class TransientService;
    private sealed class UnconstructibleService(int required) : IService { public int Required { get; } = required; }
    private sealed class CallerOwnedService : IDisposable { public int Disposals { get; private set; } public void Dispose() => Disposals++; }
    private sealed class NeutralModule(CallerOwnedService instance) : ModuleBase, IDisposable
    {
        public int Disposals { get; private set; }
        public int Shutdowns { get; private set; }
        public override void ConfigureServices(ModuleServiceConfigurationContext context) => context.Services.AddSingleton(instance);
        public override void Shutdown(ModuleShutdownContext context) => Shutdowns++;
        public void Dispose() => Disposals++;
    }
}
