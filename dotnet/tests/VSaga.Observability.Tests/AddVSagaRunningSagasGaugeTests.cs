using VSaga.Observability;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace VSaga.Observability.Tests;

/// <summary>ADR 0013's registration: opt-in, validated, and idempotent. Touches no gauge, so it may run beside the gauge cases.</summary>
public sealed class AddVSagaRunningSagasGaugeTests
{
    [Fact]
    public void ByDefault_NothingIsRegistered_SoAnUpgradeAddsNoQuery()
    {
        var services = new ServiceCollection();
        services.AddVSagaOpenTelemetry();

        Assert.DoesNotContain(services, d => d.ImplementationType == typeof(RunningSagasRefreshHostedService));
    }

    [Fact]
    public void Calling_RegistersOneHostedService_WithTheDefaultInterval()
    {
        var services = new ServiceCollection();

        services.AddVSagaRunningSagasGauge();

        Assert.Single(services, d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(RunningSagasRefreshHostedService));
        using var provider = services.BuildServiceProvider();
        Assert.Equal(TimeSpan.FromSeconds(30), provider.GetRequiredService<RunningSagasOptions>().RefreshInterval);
    }

    [Fact]
    public void CallingTwice_KeepsOneService_AndTheLastOptions()
    {
        var services = new ServiceCollection();

        services.AddVSagaRunningSagasGauge(o => o.RefreshInterval = TimeSpan.FromSeconds(5));
        services.AddVSagaRunningSagasGauge(o => o.RefreshInterval = TimeSpan.FromMinutes(2));

        Assert.Single(services, d => d.ServiceType == typeof(IHostedService));
        using var provider = services.BuildServiceProvider();
        Assert.Equal(TimeSpan.FromMinutes(2), provider.GetRequiredService<RunningSagasOptions>().RefreshInterval);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(86_401)]
    public void AnIntervalTheGaugeCannotWorkWith_Throws(int seconds)
    {
        var services = new ServiceCollection();

        var thrown = Assert.Throws<ArgumentOutOfRangeException>(() => services.AddVSagaRunningSagasGauge(o => o.RefreshInterval = TimeSpan.FromSeconds(seconds)));

        Assert.Equal("RefreshInterval", thrown.ParamName);
        Assert.Empty(services);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(86_400)]
    public void TheBoundsThemselves_AreAccepted(int seconds)
    {
        var services = new ServiceCollection();

        services.AddVSagaRunningSagasGauge(o => o.RefreshInterval = TimeSpan.FromSeconds(seconds));

        Assert.Single(services, d => d.ServiceType == typeof(IHostedService));
    }
}
