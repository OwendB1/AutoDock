using System.Collections.Generic;
using Sandbox.Game.Entities;
using SpaceEngineers.Game.Entities.Blocks;
using VRageMath;

namespace ClientPlugin;

internal sealed class LandingHardpointSample
{
    public readonly MyLandingGear Gear;
    public readonly int LockPositionIndex;
    public readonly Vector3D LocalPosition;
    public readonly Vector3 HalfExtents;
    public readonly Vector3D CurrentWorldPosition;
    public readonly Vector3D TargetWorldPosition;
    public readonly Vector3D HitPosition;
    public readonly Vector3D HitNormal;
    public readonly bool HasHit;
    public readonly double Distance;
    public readonly double TargetGap;
    public readonly bool TargetContactExpected;

    public LandingHardpointSample(
        MyLandingGear gear,
        int lockPositionIndex,
        Vector3D localPosition,
        Vector3 halfExtents,
        Vector3D currentWorldPosition,
        Vector3D targetWorldPosition,
        Vector3D hitPosition,
        Vector3D hitNormal,
        bool hasHit,
        double distance,
        double targetGap,
        bool targetContactExpected)
    {
        Gear = gear;
        LockPositionIndex = lockPositionIndex;
        LocalPosition = localPosition;
        HalfExtents = halfExtents;
        CurrentWorldPosition = currentWorldPosition;
        TargetWorldPosition = targetWorldPosition;
        HitPosition = hitPosition;
        HitNormal = hitNormal;
        HasHit = hasHit;
        Distance = distance;
        TargetGap = targetGap;
        TargetContactExpected = targetContactExpected;
    }
}

internal sealed class AutoLandingPlan
{
    public readonly MyCubeGrid Grid;
    public readonly MatrixD CurrentGridMatrix;
    public readonly MatrixD TargetGridMatrix;
    public readonly Vector3D UpDirection;
    public readonly Vector3D DownDirection;
    public readonly Vector3D AnchorLocalPosition;
    public readonly Vector3D CurrentAnchorWorldPosition;
    public readonly Vector3D TargetAnchorWorldPosition;
    public readonly IReadOnlyList<LandingHardpointSample> Hardpoints;
    public readonly IReadOnlyList<MyLandingGear> Gears;
    public readonly int ExpectedReadyGearCount;
    public readonly int ExpectedReadyHardpointCount;
    public readonly double MinHullClearance;
    public readonly bool HullClearanceOk;

    public AutoLandingPlan(
        MyCubeGrid grid,
        MatrixD currentGridMatrix,
        MatrixD targetGridMatrix,
        Vector3D upDirection,
        Vector3D downDirection,
        Vector3D anchorLocalPosition,
        Vector3D currentAnchorWorldPosition,
        Vector3D targetAnchorWorldPosition,
        IReadOnlyList<LandingHardpointSample> hardpoints,
        IReadOnlyList<MyLandingGear> gears,
        int expectedReadyGearCount,
        int expectedReadyHardpointCount,
        double minHullClearance,
        bool hullClearanceOk)
    {
        Grid = grid;
        CurrentGridMatrix = currentGridMatrix;
        TargetGridMatrix = targetGridMatrix;
        UpDirection = upDirection;
        DownDirection = downDirection;
        AnchorLocalPosition = anchorLocalPosition;
        CurrentAnchorWorldPosition = currentAnchorWorldPosition;
        TargetAnchorWorldPosition = targetAnchorWorldPosition;
        Hardpoints = hardpoints;
        Gears = gears;
        ExpectedReadyGearCount = expectedReadyGearCount;
        ExpectedReadyHardpointCount = expectedReadyHardpointCount;
        MinHullClearance = minHullClearance;
        HullClearanceOk = hullClearanceOk;
    }

    public bool HasTerrainContacts => ExpectedReadyHardpointCount > 0;
    public double AnchorDistance => Vector3D.Distance(CurrentAnchorWorldPosition, TargetAnchorWorldPosition);
}
