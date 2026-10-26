namespace AgentSpeed.Linux.Platform;

// The shared application controllers only need posting and UI timers, never WPF objects.
internal enum DispatcherPriority { Normal, Background }

internal sealed class Dispatcher
{
    public void BeginInvoke(Action action) => Avalonia.Threading.Dispatcher.UIThread.Post(action);
}

internal sealed class DispatcherTimer
{
    private readonly Avalonia.Threading.DispatcherTimer _timer = new();
    public DispatcherTimer(DispatcherPriority priority, Dispatcher dispatcher) { }
    public DispatcherTimer(TimeSpan interval, DispatcherPriority priority, EventHandler tick, Dispatcher dispatcher)
    {
        Interval = interval;
        Tick += tick;
        Start();
    }
    public TimeSpan Interval { get => _timer.Interval; set => _timer.Interval = value; }
    public event EventHandler? Tick { add => _timer.Tick += value; remove => _timer.Tick -= value; }
    public void Start() => _timer.Start();
    public void Stop() => _timer.Stop();
}
