using System;

namespace ClientPlugin;

// XML-serialized with the plugin settings; entity IDs are scoped to their world.
public sealed class DockingRotationPreference
{
    public Guid WorldId { get; set; }
    public long GridId { get; set; }
    public long LocalConnectorId { get; set; }
    public long TargetConnectorId { get; set; }
    public int RotationStep { get; set; }
}
