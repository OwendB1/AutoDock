using System;
using VRageMath;

namespace ClientPlugin;

internal sealed class DockingTarget
{
    public readonly MatrixD WorldMatrix;
    public readonly Vector3D ConstraintPosition;
    public readonly bool DockingReady;
    public readonly bool FinalApproachActive;
    public readonly bool HoldAxialPosition;
    public readonly double MaxAngularVelocity;

    private DockingTarget(MatrixD worldMatrix, Vector3D constraintPosition, bool dockingReady,
        bool finalApproachActive, bool holdAxialPosition, double maxAngularVelocity)
    {
        WorldMatrix = worldMatrix;
        ConstraintPosition = constraintPosition;
        DockingReady = dockingReady;
        FinalApproachActive = finalApproachActive;
        HoldAxialPosition = holdAxialPosition;
        MaxAngularVelocity = maxAngularVelocity;
    }

    public static DockingTarget Create(
        MatrixD currentGridMatrix,
        MatrixD targetOrientation,
        BoundingBox gridBounds,
        Vector3D currentConstraintPosition,
        Vector3D finalConstraintPosition,
        Vector3D approachAxis,
        bool finalApproachStarted)
    {
        Vector3 size = gridBounds.Size;
        double gridLength = Math.Max(size.X, Math.Max(size.Y, size.Z));
        double stagingDistance = Math.Max(AutoDockConstants.AutoDockFinalApproachDistance, Math.Min(10.0, gridLength * 0.1));
        double signedAxialOffset = Vector3D.Dot(currentConstraintPosition - finalConstraintPosition, approachAxis);
        double axialDistance = Math.Abs(signedAxialOffset);
        double approachSign = signedAxialOffset < 0.0 ? -1.0 : 1.0;
        Vector3D planarError = finalConstraintPosition - currentConstraintPosition + approachAxis * signedAxialOffset;

        // Matches the existing small-angle error scale without reporting a half-turn as aligned.
        double orientationError = Math.Sqrt(2.0 * (
            (currentGridMatrix.Right - targetOrientation.Right).LengthSquared()
            + (currentGridMatrix.Up - targetOrientation.Up).LengthSquared()
            + (currentGridMatrix.Forward - targetOrientation.Forward).LengthSquared()));
        // The bounds diagonal covers hull points relative to either the connector or center of mass.
        double rotationRadius = size.Length();
        double rotationSweep = rotationRadius * orientationError * 0.5;
        double clearancePadding = AutoDockConstants.AutoDockPositionTolerance + AutoDockConstants.AutoDockLinearBrakePadding;
        double rotationClearance = rotationSweep + Math.Min(clearancePadding, rotationSweep);
        bool canRotate = rotationClearance <= axialDistance;
        bool aligned = planarError.LengthSquared()
                       <= AutoDockConstants.AutoDockFinalApproachPlanarTolerance * AutoDockConstants.AutoDockFinalApproachPlanarTolerance
                       && orientationError <= AutoDockConstants.AutoDockFinalApproachOrientationThreshold;
        bool finalApproachActive = canRotate && aligned
                                   && (finalApproachStarted || axialDistance <= stagingDistance + AutoDockConstants.AutoDockFinalApproachEntryDistanceTolerance);

        double alignmentDistance = Math.Max(stagingDistance, rotationClearance);
        double targetDistance = Math.Min(axialDistance, alignmentDistance);
        if (!canRotate)
        {
            // Gain clearance before turning; do not rotate while retreating beside the target.
            targetDistance = rotationClearance + AutoDockConstants.AutoDockFinalApproachEntryDistanceTolerance;
            targetOrientation = currentGridMatrix;
        }
        Vector3D targetConstraintPosition = finalApproachActive
            ? finalConstraintPosition
            : finalConstraintPosition + approachAxis * (approachSign * targetDistance);

        double maxAngularVelocity = AutoDockConstants.AutoDockMaxAngularVelocity;
        if (finalApproachActive || axialDistance <= alignmentDistance + AutoDockConstants.AutoDockFinalApproachEntryDistanceTolerance)
            maxAngularVelocity = Math.Min(maxAngularVelocity, 0.5 / Math.Max(1.0, rotationRadius));
        if (!canRotate)
            maxAngularVelocity = 0.0;

        Vector3D localConstraintPosition = Vector3D.Transform(currentConstraintPosition, MatrixD.Invert(currentGridMatrix));
        MatrixD targetMatrix = targetOrientation;
        targetMatrix.Translation = targetConstraintPosition - Vector3D.TransformNormal(localConstraintPosition, targetOrientation);
        bool dockingReady = Vector3D.DistanceSquared(currentConstraintPosition, finalConstraintPosition)
                            <= AutoDockConstants.AutoDockPositionTolerance * AutoDockConstants.AutoDockPositionTolerance
                            && orientationError <= AutoDockConstants.AutoDockOrientationTolerance;
        return new DockingTarget(targetMatrix, targetConstraintPosition, dockingReady, finalApproachActive,
            !finalApproachActive && canRotate && targetDistance == axialDistance, maxAngularVelocity);
    }
}
