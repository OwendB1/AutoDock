using System;
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
    public readonly double ReadyLockDistance;
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
        double readyLockDistance,
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
        ReadyLockDistance = readyLockDistance;
        TargetContactExpected = targetContactExpected;
    }
}

internal sealed class LandingHullClearanceSample
{
    public readonly Vector3D HullWorldPosition;
    public readonly Vector3D TerrainWorldPosition;
    public readonly bool HasTerrainReference;
    public readonly double Clearance;
    public readonly bool IsInsideVoxel;
    public readonly bool IsNearHardpoint;
    public readonly bool ViolatesClearance;

    public LandingHullClearanceSample(
        Vector3D hullWorldPosition,
        Vector3D terrainWorldPosition,
        bool hasTerrainReference,
        double clearance,
        bool isInsideVoxel,
        bool isNearHardpoint,
        bool violatesClearance)
    {
        HullWorldPosition = hullWorldPosition;
        TerrainWorldPosition = terrainWorldPosition;
        HasTerrainReference = hasTerrainReference;
        Clearance = clearance;
        IsInsideVoxel = isInsideVoxel;
        IsNearHardpoint = isNearHardpoint;
        ViolatesClearance = violatesClearance;
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
    public readonly IReadOnlyList<LandingHullClearanceSample> HullClearanceSamples;
    public readonly IReadOnlyList<MyLandingGear> Gears;
    public readonly int ExpectedReadyGearCount;
    public readonly int ExpectedReadyHardpointCount;
    public readonly double MinHullClearance;
    public readonly bool HullClearanceOk;
    public readonly int HullIntersectionCount;

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
        IReadOnlyList<LandingHullClearanceSample> hullClearanceSamples,
        IReadOnlyList<MyLandingGear> gears,
        int expectedReadyGearCount,
        int expectedReadyHardpointCount,
        double minHullClearance,
        bool hullClearanceOk,
        int hullIntersectionCount)
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
        HullClearanceSamples = hullClearanceSamples;
        Gears = gears;
        ExpectedReadyGearCount = expectedReadyGearCount;
        ExpectedReadyHardpointCount = expectedReadyHardpointCount;
        MinHullClearance = minHullClearance;
        HullClearanceOk = hullClearanceOk;
        HullIntersectionCount = hullIntersectionCount;
    }

    public bool HasTerrainContacts => ExpectedReadyHardpointCount > 0;
    public double AnchorDistance => Vector3D.Distance(CurrentAnchorWorldPosition, TargetAnchorWorldPosition);
    public Vector3D GetCurrentAnchorWorldPosition()
    {
        if (Grid?.PositionComp == null || Grid.MarkedForClose)
            return CurrentAnchorWorldPosition;

        return Vector3D.Transform(AnchorLocalPosition, Grid.PositionComp.WorldMatrixRef);
    }

    public int TotalGearCount => Gears?.Count ?? 0;
    public int DisplayGearCount => TotalGearCount > 0 ? TotalGearCount : 1;
    public string HullClearanceText => double.IsInfinity(MinHullClearance)
        ? "open hull clearance"
        : $"hull clearance {MinHullClearance:0.00} m";
    public string LandingSummaryText => $"{ExpectedReadyGearCount}/{DisplayGearCount} gear(s) expected, {HullClearanceText}";
}
