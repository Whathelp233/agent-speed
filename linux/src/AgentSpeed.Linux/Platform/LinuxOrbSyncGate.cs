namespace AgentSpeed.Linux.Platform;

/// <summary>Prevents settings events raised during orb synchronization from recursively mutating orb windows.</summary>
internal sealed class LinuxOrbSyncGate
{
    private bool _active;
    private bool _repositionRequested;

    internal bool TryEnter(bool repositionExisting)
    {
        if (_active)
        {
            _repositionRequested |= repositionExisting;
            return false;
        }

        _active = true;
        _repositionRequested = repositionExisting;
        return true;
    }

    internal bool TakeRepositionRequest()
    {
        var requested = _repositionRequested;
        _repositionRequested = false;
        return requested;
    }

    internal bool HasRepositionRequest => _repositionRequested;

    internal void RequestReposition(bool requested) => _repositionRequested |= requested;

    internal void Exit() => _active = false;
}
