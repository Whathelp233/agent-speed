using AgentSpeed.Linux.Platform;
using AvaloniaPriority = Avalonia.Threading.DispatcherPriority;

namespace AgentSpeed.Linux.Tests;

public sealed class DispatcherTests
{
    [Theory]
    [InlineData(DispatcherPriority.Normal, AvaloniaPriority.Normal)]
    [InlineData(DispatcherPriority.Background, AvaloniaPriority.Background)]
    public void MapsSharedTimerPrioritiesToAvalonia(DispatcherPriority shared, AvaloniaPriority avalonia)
        => Assert.Equal(avalonia, DispatcherTimer.MapPriority(shared));

    [Fact]
    public void TimersUseThePassedLinuxDispatcher()
    {
        var dispatcher = new Dispatcher();
        var timer = new DispatcherTimer(DispatcherPriority.Normal, dispatcher);

        Assert.Same(dispatcher.NativeDispatcher, timer.NativeDispatcher);
        Assert.False(timer.IsEnabled);
    }

    [Fact]
    public void IntervalConstructorStillStartsTheTimerImmediately()
    {
        var dispatcher = new Dispatcher();
        var timer = new DispatcherTimer(TimeSpan.FromHours(1), DispatcherPriority.Background, (_, _) => { }, dispatcher);
        try { Assert.True(timer.IsEnabled); }
        finally { timer.Stop(); }
    }
}
