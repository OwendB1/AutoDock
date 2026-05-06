using VRageMath;

namespace ClientPlugin;

internal sealed class DockingTarget
{
    public readonly MatrixD WorldMatrix;
    public readonly Vector3D ConstraintPosition;
    public readonly bool DockingReady;

    public DockingTarget(MatrixD worldMatrix, Vector3D constraintPosition, bool dockingReady)
    {
        WorldMatrix = worldMatrix;
        ConstraintPosition = constraintPosition;
        DockingReady = dockingReady;
    }
}
