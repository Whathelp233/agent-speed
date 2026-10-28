using AgentSpeed.Presentation.Layout;
using AgentSpeed.Presentation.Settings.Orbs;

namespace AgentSpeed.Linux.Platform;

/// <summary>Adapts the Linux window layer to the shared orb placement rules.</summary>
internal static class LinuxOrbLayout
{
    internal static OrbLayoutResult PlaceNew(
        OrbsDocument document,
        IReadOnlyList<OrbDisplay> displays,
        IReadOnlyDictionary<string, PointD>? centers,
        string newId,
        string? fromId) => OrbArrangement.PlaceNew(document, displays, centers, newId, fromId);
}
