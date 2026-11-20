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
    /// <summary>The ship is backing away along the approach axis to gain room for a turn.</summary>
    public readonly bool Retreating;
    /// <summary>An obstacle limits the retreat, so the remaining turn happens slowly at the reachable distance.</summary>
    public readonly bool ClearanceLimited;
    /// <summary>World direction of the retreat (away from the target connector along its approach axis).</summary>
    public readonly Vector3D RetreatDirection;
    /// <summary>Current distance between the connectors along the approach axis.</summary>
    public readonly double AxialDistance;

    private DockingTarget(MatrixD worldMatrix, Vector3D constraintPosition, bool dockingReady,
        bool finalApproachActive, bool holdAxialPosition, double maxAngularVelocity,
        bool retreating, bool clearanceLimited, Vector3D retreatDirection, double axialDistance)
    {
        WorldMatrix = worldMatrix;
        ConstraintPosition = constraintPosition;
        DockingReady = dockingReady;
        FinalApproachActive = finalApproachActive;
        HoldAxialPosition = holdAxialPosition;
        MaxAngularVelocity = maxAngularVelocity;
        Retreating = retreating;
        ClearanceLimited = clearanceLimited;
        RetreatDirection = retreatDirection;
        AxialDistance = axialDistance;
    }

    /// <param name="centerOfMassLocal">
    /// Grid-local center of mass. When known, the turning radius is the distance from it to the farthest
    /// bounds corner; otherwise the full bounds diagonal is used as a worst-case radius.
    /// </param>
    /// <param name="maxAxialReach">
    /// Largest distance from the target along the approach axis that is free of obstacles.
    /// </param>
    public static DockingTarget Create(
        MatrixD currentGridMatrix,
        MatrixD targetOrientation,
        BoundingBox gridBounds,
        Vector3D currentConstraintPosition,
        Vector3D finalConstraintPosition,
        Vector3D approachAxis,
        bool finalApproachStarted,
        Vector3D? centerOfMassLocal = null,
        double maxAxialReach = double.PositiveInfinity)
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
        // orientationError is 4·sin(angle/2), so a hull point at distance r from the rotation center moves by the
        // chord r·orientationError/2. The ship turns around its center of mass, so r is the farthest bounds corner.
        double rotationRadius = Math.Max(1.0, GetRotationRadius(gridBounds, centerOfMassLocal));
        double rotationSweep = rotationRadius * orientationError * 0.5;
        double clearancePadding = AutoDockConstants.AutoDockPositionTolerance + AutoDockConstants.AutoDockLinearBrakePadding;
        double rotationClearance = rotationSweep + Math.Min(clearancePadding, rotationSweep);

        // Rock or another grid behind the ship: back off only as far as it is free, then turn slowly there.
        bool clearanceLimited = false;
        if (rotationClearance > maxAxialReach)
        {
            rotationClearance = Math.Max(0.0, maxAxialReach);
            clearanceLimited = true;
        }

        double reachTolerance = clearanceLimited ? AutoDockConstants.AutoDockFinalApproachEntryDistanceTolerance : 0.0;
        bool canRotate = rotationClearance <= axialDistance + reachTolerance;
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
            targetDistance = clearanceLimited
                ? rotationClearance
                : rotationClearance + AutoDockConstants.AutoDockFinalApproachEntryDistanceTolerance;
            targetOrientation = currentGridMatrix;
        }
        Vector3D targetConstraintPosition = finalApproachActive
            ? finalConstraintPosition
            : finalConstraintPosition + approachAxis * (approachSign * targetDistance);

        // Close to the target the outermost hull point turns at no more than RotationHullSpeed (m/s).
        double hullSpeedLimit = AutoDockConstants.RotationHullSpeed / rotationRadius;
        double maxAngularVelocity = AutoDockConstants.AutoDockMaxAngularVelocity;
        if (finalApproachActive || clearanceLimited
            || axialDistance <= alignmentDistance + AutoDockConstants.AutoDockFinalApproachEntryDistanceTolerance)
            maxAngularVelocity = Math.Min(maxAngularVelocity, hullSpeedLimit);
        if (clearanceLimited)
            maxAngularVelocity = Math.Min(maxAngularVelocity, hullSpeedLimit * 0.5);
        if (!canRotate)
            maxAngularVelocity = 0.0;

        Vector3D localConstraintPosition = Vector3D.Transform(currentConstraintPosition, MatrixD.Invert(currentGridMatrix));
        MatrixD targetMatrix = targetOrientation;
        targetMatrix.Translation = targetConstraintPosition - Vector3D.TransformNormal(localConstraintPosition, targetOrientation);
        bool dockingReady = Vector3D.DistanceSquared(currentConstraintPosition, finalConstraintPosition)
                            <= AutoDockConstants.AutoDockPositionTolerance * AutoDockConstants.AutoDockPositionTolerance
                            && orientationError <= AutoDockConstants.AutoDockOrientationTolerance;
        return new DockingTarget(targetMatrix, targetConstraintPosition, dockingReady, finalApproachActive,
            !finalApproachActive && canRotate && targetDistance == axialDistance, maxAngularVelocity,
            !canRotate, clearanceLimited, approachAxis * approachSign, axialDistance);
    }

    private static double GetRotationRadius(BoundingBox bounds, Vector3D? centerOfMassLocal)
    {
        if (!centerOfMassLocal.HasValue || !centerOfMassLocal.Value.IsValid())
            return bounds.Size.Length();

        Vector3D center = centerOfMassLocal.Value;
        double radiusSquared = 0.0;
        foreach (Vector3 corner in bounds.GetCorners())
            radiusSquared = Math.Max(radiusSquared, Vector3D.DistanceSquared(corner, center));
        return Math.Sqrt(radiusSquared);
    }
}
