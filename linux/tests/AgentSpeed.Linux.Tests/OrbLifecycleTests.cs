using AgentSpeed.Linux.Platform;

namespace AgentSpeed.Linux.Tests;

public sealed class OrbLifecycleTests
{
    [Fact]
    public void PositionFlushReentryCannotMutateTheClosingWindowCollection()
    {
        var gate = new LinuxOrbSyncGate();
        var windows = new Dictionary<string, int>(StringComparer.Ordinal) { ["orb"] = 1 };
        var operations = new List<string>();
        var syncPasses = 0;

        Assert.True(gate.TryEnter(repositionExisting: false));
        syncPasses++;
        try
        {
            LinuxOrbPositionLifecycle.Close(
                retainPosition: true,
                flushPosition: () =>
                {
                    Assert.DoesNotContain("orb", windows.Keys);
                    operations.Add("flush");
                    // Settings.Update raises Changed synchronously; its SyncOrbs call must not re-enter.
                    if (gate.TryEnter(repositionExisting: false))
                    {
                        try { syncPasses++; windows["orb"] = 2; }
                        finally { gate.Exit(); }
                    }
                },
                close: () => operations.Add("close"),
                removeFromLiveWindows: () => { windows.Remove("orb"); operations.Add("remove"); });
        }
        finally { gate.Exit(); }

        Assert.Equal(["remove", "flush", "close"], operations);
        Assert.Equal(1, syncPasses);
        Assert.Empty(windows);
    }
}
