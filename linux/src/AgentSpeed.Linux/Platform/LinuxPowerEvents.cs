using Tmds.DBus;

namespace AgentSpeed.Linux.Platform;

[DBusInterface("org.freedesktop.login1.Manager")]
public interface ILoginManager : IDBusObject
{
    Task<IDisposable> WatchPrepareForSleepAsync(Action<bool> handler);
}

internal sealed class LinuxPowerEvents(LinuxApp app) : IDisposable
{
    private Connection? _connection;
    private IDisposable? _watch;
    public async Task StartAsync()
    {
        try
        {
            _connection = new Connection(Address.System!); await _connection.ConnectAsync();
            var proxy = _connection.CreateProxy<ILoginManager>("org.freedesktop.login1", new ObjectPath("/org/freedesktop/login1"));
            _watch = await proxy.WatchPrepareForSleepAsync(sleeping => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (app.Quitting) return;
                if (sleeping) LinuxOrbPositionLifecycle.Suspend(app.FlushOrbPositions, app.Traffic.Monitor.Flush, app.Settings.Flush);
                else { app.Cost.Prices.OnResume(); app.Cost.Rates.OnResume(); app.SyncOrbs(); }
            }));
        }
        catch (Exception ex) { AgentSpeed.App.AppLog.Warn("Suspend notifications are unavailable.", ex); }
    }
    public void Dispose() { _watch?.Dispose(); _connection?.Dispose(); }
}
