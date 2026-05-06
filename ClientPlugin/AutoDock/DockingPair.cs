using Sandbox.Game.Entities.Cube;
using VRageMath;

namespace ClientPlugin;

internal sealed class DockingPair
{
    public readonly MyShipConnector Local;
    public readonly MyShipConnector Target;
    public readonly bool HasSavedAlignment;
    public double Distance { get; private set; }
    public bool InRange { get; private set; }
    public bool LockReady { get; private set; }

    public DockingPair(MyShipConnector local, MyShipConnector target, double distance, bool lockReady, bool hasSavedAlignment)
    {
        Local = local;
        Target = target;
        HasSavedAlignment = hasSavedAlignment;
        Distance = distance;
        InRange = DockingMath.IsWithinSearchRange(distance);
        LockReady = lockReady;
    }

    public void RefreshMetrics()
    {
        if (Local == null || Target == null || Local.MarkedForClose || Target.MarkedForClose)
        {
            LockReady = false;
            return;
        }

        Distance = Vector3D.Distance(Local.PositionComp.GetPosition(), Target.PositionComp.GetPosition());
        InRange = DockingMath.IsWithinSearchRange(Distance);
        LockReady = DockingMath.IsLockReady(Local, Target);
    }
}
