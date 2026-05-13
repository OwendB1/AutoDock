using System;
using System.Collections.Generic;
using Sandbox.Engine.Physics;
using Sandbox.Game.Entities;
using Sandbox.Game.World;
using SpaceEngineers.Game.Entities.Blocks;
using VRage.Game.Entity;
using VRageMath;

namespace ClientPlugin;

internal static class AutoLandingPlanner
{
    private sealed class HardpointData
    {
        public MyLandingGear Gear;
        public int LockPositionIndex;
        public Vector3D LocalPosition;
        public Vector3 HalfExtents;
        public Vector3D CurrentWorldPosition;
        public Vector3D HitPosition;
        public Vector3D HitNormal;
        public bool HasHit;
        public double Distance;
    }

    private sealed class CandidateHitData
    {
        public Vector3D TargetWorldPositionNoVertical;
        public Vector3D HitPosition;
        public Vector3D HitNormal;
        public bool HasHit;
        public double Distance;
    }

    private sealed class LandingPlanCandidate
    {
        public AutoLandingPlan Plan;
        public double PositiveGapSum;
        public int YawOffsetDegrees;
    }

    private static readonly List<MyPhysics.HitInfo> RaycastHits = new List<MyPhysics.HitInfo>(32);

    public static bool TryCreatePlan(out AutoLandingPlan plan, out string error)
    {
        plan = null;
        error = null;

        MyCubeGrid grid = MySession.Static?.ControlledGrid;
        if (grid == null || grid.MarkedForClose || grid.Physics == null)
        {
            error = "AutoDock: controlled dynamic grid required for landing.";
            return false;
        }

        if (grid.IsStatic)
        {
            error = "AutoDock: landing needs a dynamic grid.";
            return false;
        }

        if (!DockingMath.TryGetControlledShipController(grid, out MyShipController controller)
            && !DockingMath.TryGetActiveShipController(grid, out controller))
        {
            error = "AutoDock: take control of a ship controller on selected grid first.";
            return false;
        }

        if (!TryGetLandingAxes(grid, controller, out Vector3D downDirection, out Vector3D upDirection, out Vector3D referenceForward, out Vector3D referenceRight))
        {
            error = "AutoDock: cannot resolve landing orientation.";
            return false;
        }

        var gears = new List<MyLandingGear>();
        var hardpoints = new List<HardpointData>();
        if (!CollectHardpoints(grid, downDirection, gears, hardpoints))
        {
            error = "AutoDock: no working landing gear found on controlled grid.";
            return false;
        }

        int hitCount = 0;
        for (int i = 0; i < hardpoints.Count; i++)
        {
            if (hardpoints[i].HasHit)
                hitCount++;
        }

        if (hitCount == 0)
        {
            error = "AutoDock: no terrain detected below landing gear.";
            return false;
        }

        MatrixD currentGridMatrix = grid.PositionComp.WorldMatrixRef;
        Vector3D anchorLocalPosition = GetAnchorLocalPosition(hardpoints);
        Vector3D currentAnchorWorldPosition = Vector3D.Transform(anchorLocalPosition, currentGridMatrix);
        Vector3D targetUpDirection = GetTargetUpDirection(hardpoints, upDirection, referenceRight, referenceForward);
        if (!TrySelectBestAnglePlan(
                grid,
                currentGridMatrix,
                anchorLocalPosition,
                currentAnchorWorldPosition,
                targetUpDirection,
                upDirection,
                downDirection,
                referenceForward,
                referenceRight,
                hardpoints,
                gears,
                out plan))
        {
            error = "AutoDock: cannot resolve landing pose.";
            return false;
        }

        return true;
    }

    private static bool CollectHardpoints(MyCubeGrid grid, Vector3D downDirection, List<MyLandingGear> gears, List<HardpointData> hardpoints)
    {
        MatrixD inverseGridMatrix = MatrixD.Invert(grid.PositionComp.WorldMatrixRef);
        foreach (MyLandingGear gear in grid.GetFatBlocks<MyLandingGear>())
        {
            if (gear == null || gear.MarkedForClose || !gear.IsWorking || gear.LockPositions == null || gear.LockPositions.Length == 0)
                continue;

            gears.Add(gear);
            for (int i = 0; i < gear.LockPositions.Length; i++)
            {
                MatrixD lockPosition = gear.LockPositions[i];
                gear.GetBoxFromMatrix(lockPosition, out Vector3 halfExtents, out Vector3D currentWorldPosition, out _);
                Vector3D localPosition = Vector3D.Transform(currentWorldPosition, inverseGridMatrix);
                bool hasHit = TryRaycastTerrain(grid, currentWorldPosition, downDirection, out Vector3D hitPosition, out Vector3D hitNormal, out double distance);
                hardpoints.Add(new HardpointData
                {
                    Gear = gear,
                    LockPositionIndex = i,
                    LocalPosition = localPosition,
                    HalfExtents = halfExtents,
                    CurrentWorldPosition = currentWorldPosition,
                    HitPosition = hitPosition,
                    HitNormal = hitNormal,
                    HasHit = hasHit,
                    Distance = distance
                });
            }
        }

        return gears.Count > 0 && hardpoints.Count > 0;
    }

    private static bool TryGetLandingAxes(
        MyCubeGrid grid,
        MyShipController controller,
        out Vector3D downDirection,
        out Vector3D upDirection,
        out Vector3D referenceForward,
        out Vector3D referenceRight)
    {
        downDirection = controller.GetNaturalGravity();
        if (downDirection.LengthSquared() < AutoDockConstants.AutoLandingMinGravity * AutoDockConstants.AutoLandingMinGravity)
            downDirection = -controller.WorldMatrix.Up;
        if (downDirection.LengthSquared() < AutoDockConstants.MinConnectorDistanceSquared)
            downDirection = -grid.WorldMatrix.Up;
        if (downDirection.LengthSquared() < AutoDockConstants.MinConnectorDistanceSquared)
        {
            upDirection = Vector3D.Zero;
            referenceForward = Vector3D.Zero;
            referenceRight = Vector3D.Zero;
            return false;
        }

        downDirection.Normalize();
        upDirection = -downDirection;
        referenceForward = RejectFromAxis(controller.WorldMatrix.Forward, upDirection);
        if (referenceForward.LengthSquared() < AutoDockConstants.MinConnectorDistanceSquared)
            referenceForward = RejectFromAxis(grid.WorldMatrix.Forward, upDirection);
        if (referenceForward.LengthSquared() < AutoDockConstants.MinConnectorDistanceSquared)
            referenceForward = RejectFromAxis(controller.WorldMatrix.Right, upDirection);
        if (referenceForward.LengthSquared() < AutoDockConstants.MinConnectorDistanceSquared)
        {
            referenceRight = Vector3D.Zero;
            return false;
        }

        referenceForward.Normalize();
        referenceRight = Vector3D.Cross(referenceForward, upDirection);
        if (referenceRight.LengthSquared() < AutoDockConstants.MinConnectorDistanceSquared)
            return false;

        referenceRight.Normalize();
        return true;
    }

    private static Vector3D GetAnchorLocalPosition(IReadOnlyList<HardpointData> hardpoints)
    {
        Vector3D sum = Vector3D.Zero;
        int count = 0;
        for (int i = 0; i < hardpoints.Count; i++)
        {
            sum += hardpoints[i].LocalPosition;
            count++;
        }

        return count > 0 ? sum / count : Vector3D.Zero;
    }

    private static bool TrySelectBestAnglePlan(
        MyCubeGrid grid,
        MatrixD currentGridMatrix,
        Vector3D anchorLocalPosition,
        Vector3D currentAnchorWorldPosition,
        Vector3D targetUpDirection,
        Vector3D upDirection,
        Vector3D downDirection,
        Vector3D referenceForward,
        Vector3D referenceRight,
        IReadOnlyList<HardpointData> hardpoints,
        IReadOnlyList<MyLandingGear> gears,
        out AutoLandingPlan plan)
    {
        plan = null;
        LandingPlanCandidate bestCandidate = null;
        for (int yawOffsetDegrees = 0; yawOffsetDegrees <= AutoDockConstants.AutoLandingMaxYawDegrees; yawOffsetDegrees++)
        {
            if (yawOffsetDegrees == 0)
            {
                if (TryCreateCandidatePlan(
                        grid,
                        currentGridMatrix,
                        anchorLocalPosition,
                        currentAnchorWorldPosition,
                        targetUpDirection,
                        upDirection,
                        downDirection,
                        referenceForward,
                        referenceRight,
                        hardpoints,
                        gears,
                        0,
                        out LandingPlanCandidate zeroYawCandidate)
                    && (bestCandidate == null || IsBetterPlanCandidate(zeroYawCandidate, bestCandidate)))
                {
                    bestCandidate = zeroYawCandidate;
                }

                continue;
            }

            if (TryCreateCandidatePlan(
                    grid,
                    currentGridMatrix,
                    anchorLocalPosition,
                    currentAnchorWorldPosition,
                    targetUpDirection,
                    upDirection,
                    downDirection,
                    referenceForward,
                    referenceRight,
                    hardpoints,
                    gears,
                    yawOffsetDegrees,
                    out LandingPlanCandidate positiveYawCandidate)
                && (bestCandidate == null || IsBetterPlanCandidate(positiveYawCandidate, bestCandidate)))
            {
                bestCandidate = positiveYawCandidate;
            }

            if (TryCreateCandidatePlan(
                    grid,
                    currentGridMatrix,
                    anchorLocalPosition,
                    currentAnchorWorldPosition,
                    targetUpDirection,
                    upDirection,
                    downDirection,
                    referenceForward,
                    referenceRight,
                    hardpoints,
                    gears,
                    -yawOffsetDegrees,
                    out LandingPlanCandidate negativeYawCandidate)
                && (bestCandidate == null || IsBetterPlanCandidate(negativeYawCandidate, bestCandidate)))
            {
                bestCandidate = negativeYawCandidate;
            }
        }

        if (bestCandidate == null)
            return false;

        plan = bestCandidate.Plan;
        return true;
    }

    private static bool TryCreateCandidatePlan(
        MyCubeGrid grid,
        MatrixD currentGridMatrix,
        Vector3D anchorLocalPosition,
        Vector3D currentAnchorWorldPosition,
        Vector3D targetUpDirection,
        Vector3D upDirection,
        Vector3D downDirection,
        Vector3D referenceForward,
        Vector3D referenceRight,
        IReadOnlyList<HardpointData> hardpoints,
        IReadOnlyList<MyLandingGear> gears,
        int yawOffsetDegrees,
        out LandingPlanCandidate candidate)
    {
        candidate = null;
        MatrixD targetGridMatrix = CreateTargetGridMatrix(targetUpDirection, referenceForward, referenceRight, yawOffsetDegrees * Math.PI / 180.0);
        Vector3D targetAnchorNoTranslation = Vector3D.Transform(anchorLocalPosition, targetGridMatrix);
        Vector3D provisionalTranslation = currentAnchorWorldPosition - targetAnchorNoTranslation;

        var candidateHits = new List<CandidateHitData>(hardpoints.Count);
        double targetVerticalOffset = double.NegativeInfinity;
        int hitCount = 0;
        for (int i = 0; i < hardpoints.Count; i++)
        {
            HardpointData hardpoint = hardpoints[i];
            Vector3D targetWorldPositionNoVertical = provisionalTranslation + Vector3D.Transform(hardpoint.LocalPosition, targetGridMatrix);
            bool hasHit = TryRaycastTerrain(grid, targetWorldPositionNoVertical, downDirection, out Vector3D hitPosition, out Vector3D hitNormal, out double distance);
            candidateHits.Add(new CandidateHitData
            {
                TargetWorldPositionNoVertical = targetWorldPositionNoVertical,
                HitPosition = hitPosition,
                HitNormal = hitNormal,
                HasHit = hasHit,
                Distance = distance
            });

            if (!hasHit)
                continue;

            hitCount++;
            double requiredOffset =
                Vector3D.Dot(hitPosition, upDirection)
                - Vector3D.Dot(targetWorldPositionNoVertical, upDirection)
                - AutoDockConstants.AutoLandingTargetOverlap;
            if (requiredOffset > targetVerticalOffset)
                targetVerticalOffset = requiredOffset;
        }

        if (hitCount == 0 || double.IsNegativeInfinity(targetVerticalOffset) || double.IsNaN(targetVerticalOffset) || double.IsInfinity(targetVerticalOffset))
            return false;

        targetGridMatrix.Translation = provisionalTranslation + upDirection * targetVerticalOffset;

        var samples = new List<LandingHardpointSample>(hardpoints.Count);
        var expectedContactGearIds = new HashSet<long>();
        int expectedReadyHardpointCount = 0;
        double positiveGapSum = 0.0;
        for (int i = 0; i < hardpoints.Count; i++)
        {
            HardpointData hardpoint = hardpoints[i];
            CandidateHitData candidateHit = candidateHits[i];
            Vector3D targetWorldPosition = candidateHit.TargetWorldPositionNoVertical + upDirection * targetVerticalOffset;
            double targetGap = candidateHit.HasHit
                ? Vector3D.Dot(targetWorldPosition - candidateHit.HitPosition, upDirection)
                : double.PositiveInfinity;
            bool targetContactExpected = candidateHit.HasHit && targetGap <= AutoDockConstants.AutoLandingContactTolerance;
            if (targetContactExpected)
            {
                expectedReadyHardpointCount++;
                expectedContactGearIds.Add(hardpoint.Gear.EntityId);
            }

            if (candidateHit.HasHit && targetGap > 0.0)
                positiveGapSum += targetGap;

            samples.Add(new LandingHardpointSample(
                hardpoint.Gear,
                hardpoint.LockPositionIndex,
                hardpoint.LocalPosition,
                hardpoint.HalfExtents,
                hardpoint.CurrentWorldPosition,
                targetWorldPosition,
                candidateHit.HitPosition,
                candidateHit.HitNormal,
                candidateHit.HasHit,
                candidateHit.Distance,
                targetGap,
                targetContactExpected));
        }

        EvaluateHullClearance(
            grid,
            targetGridMatrix,
            upDirection,
            downDirection,
            samples,
            out List<LandingHullClearanceSample> hullClearanceSamples,
            out double minHullClearance,
            out bool hullClearanceOk,
            out int hullIntersectionCount);
        candidate = new LandingPlanCandidate
        {
            Plan = new AutoLandingPlan(
                grid,
                currentGridMatrix,
                targetGridMatrix,
                upDirection,
                downDirection,
                anchorLocalPosition,
                currentAnchorWorldPosition,
                Vector3D.Transform(anchorLocalPosition, targetGridMatrix),
                samples,
                hullClearanceSamples,
                gears,
                expectedContactGearIds.Count,
                expectedReadyHardpointCount,
                minHullClearance,
                hullClearanceOk,
                hullIntersectionCount),
            PositiveGapSum = positiveGapSum,
            YawOffsetDegrees = yawOffsetDegrees
        };
        return true;
    }

    private static bool IsBetterPlanCandidate(LandingPlanCandidate candidate, LandingPlanCandidate currentBest)
    {
        if (candidate.Plan.HullClearanceOk != currentBest.Plan.HullClearanceOk)
            return candidate.Plan.HullClearanceOk;

        if (candidate.Plan.ExpectedReadyGearCount != currentBest.Plan.ExpectedReadyGearCount)
            return candidate.Plan.ExpectedReadyGearCount > currentBest.Plan.ExpectedReadyGearCount;

        if (candidate.Plan.ExpectedReadyHardpointCount != currentBest.Plan.ExpectedReadyHardpointCount)
            return candidate.Plan.ExpectedReadyHardpointCount > currentBest.Plan.ExpectedReadyHardpointCount;

        bool candidateInfiniteClearance = double.IsInfinity(candidate.Plan.MinHullClearance);
        bool currentInfiniteClearance = double.IsInfinity(currentBest.Plan.MinHullClearance);
        if (candidateInfiniteClearance != currentInfiniteClearance)
            return candidateInfiniteClearance;

        if (!candidateInfiniteClearance && Math.Abs(candidate.Plan.MinHullClearance - currentBest.Plan.MinHullClearance) > 1e-6)
            return candidate.Plan.MinHullClearance > currentBest.Plan.MinHullClearance;

        int candidateYawDistanceDegrees = GetYawDistanceDegrees(candidate.YawOffsetDegrees);
        int currentYawDistanceDegrees = GetYawDistanceDegrees(currentBest.YawOffsetDegrees);
        if (candidateYawDistanceDegrees != currentYawDistanceDegrees)
            return candidateYawDistanceDegrees < currentYawDistanceDegrees;

        if (Math.Abs(candidate.PositiveGapSum - currentBest.PositiveGapSum) > 1e-6)
            return candidate.PositiveGapSum < currentBest.PositiveGapSum;

        return candidate.Plan.AnchorDistance < currentBest.Plan.AnchorDistance;
    }

    private static int GetYawDistanceDegrees(int yawOffsetDegrees)
    {
        return Math.Abs(yawOffsetDegrees);
    }

    private static Vector3D GetTargetUpDirection(
        IReadOnlyList<HardpointData> hardpoints,
        Vector3D fallbackUpDirection,
        Vector3D referenceRight,
        Vector3D referenceForward)
    {
        Vector3D centroid = Vector3D.Zero;
        Vector3D averageNormal = Vector3D.Zero;
        int hitCount = 0;
        for (int i = 0; i < hardpoints.Count; i++)
        {
            if (!hardpoints[i].HasHit)
                continue;

            centroid += hardpoints[i].HitPosition;
            averageNormal += hardpoints[i].HitNormal;
            hitCount++;
        }

        if (hitCount == 0)
            return fallbackUpDirection;

        centroid /= hitCount;
        Vector3D terrainUp = fallbackUpDirection;
        if (TryFitSurfaceUp(hardpoints, centroid, fallbackUpDirection, referenceRight, referenceForward, out Vector3D fittedUp))
            terrainUp = fittedUp;

        if (averageNormal.LengthSquared() >= AutoDockConstants.MinConnectorDistanceSquared)
        {
            averageNormal.Normalize();
            if (Vector3D.Dot(averageNormal, terrainUp) < 0.0)
                averageNormal = -averageNormal;

            terrainUp = terrainUp * 0.65 + averageNormal * 0.35;
        }

        if (terrainUp.LengthSquared() < AutoDockConstants.MinConnectorDistanceSquared)
            return fallbackUpDirection;

        terrainUp.Normalize();
        if (Vector3D.Dot(terrainUp, fallbackUpDirection) < 0.0)
            terrainUp = -terrainUp;
        return terrainUp;
    }

    private static bool TryFitSurfaceUp(
        IReadOnlyList<HardpointData> hardpoints,
        Vector3D centroid,
        Vector3D fallbackUpDirection,
        Vector3D referenceRight,
        Vector3D referenceForward,
        out Vector3D fittedUp)
    {
        fittedUp = Vector3D.Zero;
        double sumXx = 0.0;
        double sumXy = 0.0;
        double sumYy = 0.0;
        double sumXh = 0.0;
        double sumYh = 0.0;
        int hitCount = 0;
        for (int i = 0; i < hardpoints.Count; i++)
        {
            if (!hardpoints[i].HasHit)
                continue;

            Vector3D delta = hardpoints[i].HitPosition - centroid;
            double x = Vector3D.Dot(delta, referenceRight);
            double y = Vector3D.Dot(delta, referenceForward);
            double h = Vector3D.Dot(delta, fallbackUpDirection);
            sumXx += x * x;
            sumXy += x * y;
            sumYy += y * y;
            sumXh += x * h;
            sumYh += y * h;
            hitCount++;
        }

        if (hitCount < 3)
            return false;

        double determinant = sumXx * sumYy - sumXy * sumXy;
        if (Math.Abs(determinant) < 1e-6)
            return false;

        double slopeRight = (sumXh * sumYy - sumYh * sumXy) / determinant;
        double slopeForward = (sumYh * sumXx - sumXh * sumXy) / determinant;
        fittedUp = fallbackUpDirection - referenceRight * slopeRight - referenceForward * slopeForward;
        if (fittedUp.LengthSquared() < AutoDockConstants.MinConnectorDistanceSquared)
            return false;

        fittedUp.Normalize();
        return true;
    }

    private static MatrixD CreateTargetGridMatrix(Vector3D targetUpDirection, Vector3D preferredForward, Vector3D preferredRight)
    {
        return CreateTargetGridMatrix(targetUpDirection, preferredForward, preferredRight, 0.0);
    }

    private static MatrixD CreateTargetGridMatrix(
        Vector3D targetUpDirection,
        Vector3D preferredForward,
        Vector3D preferredRight,
        double angleOffsetRadians)
    {
        Vector3D baseForward = RejectFromAxis(preferredForward, targetUpDirection);
        if (baseForward.LengthSquared() < AutoDockConstants.MinConnectorDistanceSquared)
            baseForward = Vector3D.Cross(preferredRight, targetUpDirection);
        if (baseForward.LengthSquared() < AutoDockConstants.MinConnectorDistanceSquared)
            baseForward = Vector3D.Cross(Vector3D.Right, targetUpDirection);
        if (baseForward.LengthSquared() < AutoDockConstants.MinConnectorDistanceSquared)
            baseForward = Vector3D.Cross(Vector3D.Forward, targetUpDirection);
        if (baseForward.LengthSquared() < AutoDockConstants.MinConnectorDistanceSquared)
            baseForward = RejectFromAxis(Vector3D.Up, targetUpDirection);
        if (baseForward.LengthSquared() < AutoDockConstants.MinConnectorDistanceSquared)
            baseForward = Vector3D.Right;

        baseForward.Normalize();
        Vector3D baseRight = Vector3D.Cross(baseForward, targetUpDirection);
        if (baseRight.LengthSquared() < AutoDockConstants.MinConnectorDistanceSquared)
            baseRight = RejectFromAxis(preferredRight, targetUpDirection);
        if (baseRight.LengthSquared() < AutoDockConstants.MinConnectorDistanceSquared)
            baseRight = Vector3D.Cross(baseForward, Vector3D.Up);
        if (baseRight.LengthSquared() < AutoDockConstants.MinConnectorDistanceSquared)
            baseRight = Vector3D.Cross(baseForward, Vector3D.Right);
        if (baseRight.LengthSquared() < AutoDockConstants.MinConnectorDistanceSquared)
            baseRight = Vector3D.Cross(baseForward, Vector3D.Forward);
        if (baseRight.LengthSquared() < AutoDockConstants.MinConnectorDistanceSquared)
            baseRight = Vector3D.Forward;

        baseRight.Normalize();
        Vector3D targetForward = baseForward;
        if (Math.Abs(angleOffsetRadians) > 1e-9)
        {
            targetForward = baseForward * Math.Cos(angleOffsetRadians) + baseRight * Math.Sin(angleOffsetRadians);
            if (targetForward.LengthSquared() < AutoDockConstants.MinConnectorDistanceSquared)
                targetForward = baseForward;
        }

        targetForward.Normalize();
        MatrixD targetGridMatrix = MatrixD.CreateWorld(Vector3D.Zero, targetForward, targetUpDirection);
        targetGridMatrix.Translation = Vector3D.Zero;
        return targetGridMatrix;
    }

    private static void EvaluateHullClearance(
        MyCubeGrid grid,
        MatrixD targetGridMatrix,
        Vector3D upDirection,
        Vector3D downDirection,
        IReadOnlyList<LandingHardpointSample> hardpoints,
        out List<LandingHullClearanceSample> hullClearanceSamples,
        out double minHullClearance,
        out bool hullClearanceOk,
        out int hullIntersectionCount)
    {
        hullClearanceSamples = new List<LandingHullClearanceSample>(13);
        minHullClearance = double.PositiveInfinity;
        hullClearanceOk = true;
        hullIntersectionCount = 0;
        BoundingBox localAabb = grid.PositionComp.LocalAABB;
        Vector3 min = localAabb.Min;
        Vector3 max = localAabb.Max;
        Vector3 center = localAabb.Center;
        var samples = new List<Vector3D>(13)
        {
            new Vector3D(min.X, min.Y, min.Z),
            new Vector3D(min.X, min.Y, max.Z),
            new Vector3D(min.X, max.Y, min.Z),
            new Vector3D(min.X, max.Y, max.Z),
            new Vector3D(max.X, min.Y, min.Z),
            new Vector3D(max.X, min.Y, max.Z),
            new Vector3D(max.X, max.Y, min.Z),
            new Vector3D(max.X, max.Y, max.Z),
            new Vector3D(center.X, min.Y, center.Z),
            new Vector3D(min.X, min.Y, center.Z),
            new Vector3D(max.X, min.Y, center.Z),
            new Vector3D(center.X, min.Y, min.Z),
            new Vector3D(center.X, min.Y, max.Z)
        };

        for (int i = 0; i < samples.Count; i++)
        {
            Vector3D sampleWorldPosition = Vector3D.Transform(samples[i], targetGridMatrix);
            bool isNearHardpoint = IsNearHardpoint(sampleWorldPosition, hardpoints);
            bool isInsideVoxel = MyEntities.IsInsideVoxel(
                sampleWorldPosition,
                sampleWorldPosition + upDirection * AutoDockConstants.AutoLandingHullClearance,
                out _);
            bool hasHit = TryRaycastTerrain(grid, sampleWorldPosition, downDirection, out Vector3D hitPosition, out _, out _);
            double clearance = hasHit
                ? Vector3D.Dot(sampleWorldPosition - hitPosition, upDirection)
                : isInsideVoxel
                    ? -AutoDockConstants.AutoLandingHullClearance
                    : double.PositiveInfinity;
            if (clearance < minHullClearance)
                minHullClearance = clearance;

            bool violatesClearance = isInsideVoxel
                || (!isNearHardpoint && hasHit && clearance < AutoDockConstants.AutoLandingHullClearance);
            if (violatesClearance)
            {
                hullClearanceOk = false;
                hullIntersectionCount++;
            }

            hullClearanceSamples.Add(new LandingHullClearanceSample(
                sampleWorldPosition,
                hasHit ? hitPosition : sampleWorldPosition,
                hasHit,
                clearance,
                isInsideVoxel,
                isNearHardpoint,
                violatesClearance));
        }
    }

    private static bool IsNearHardpoint(Vector3D sampleWorldPosition, IReadOnlyList<LandingHardpointSample> hardpoints)
    {
        double maxDistanceSquared = AutoDockConstants.AutoLandingHullHardpointExclusionRadius * AutoDockConstants.AutoLandingHullHardpointExclusionRadius;
        for (int i = 0; i < hardpoints.Count; i++)
        {
            if (Vector3D.DistanceSquared(sampleWorldPosition, hardpoints[i].TargetWorldPosition) <= maxDistanceSquared)
                return true;
        }

        return false;
    }

    private static bool TryRaycastTerrain(
        MyCubeGrid grid,
        Vector3D worldPosition,
        Vector3D downDirection,
        out Vector3D hitPosition,
        out Vector3D hitNormal,
        out double distance)
    {
        hitPosition = Vector3D.Zero;
        hitNormal = Vector3D.Zero;
        distance = double.PositiveInfinity;
        if (grid == null || downDirection.LengthSquared() < AutoDockConstants.MinConnectorDistanceSquared)
            return false;

        Vector3D start = worldPosition - downDirection * AutoDockConstants.AutoLandingRayStartOffset;
        Vector3D end = worldPosition + downDirection * AutoDockConstants.AutoLandingRaycastRange;
        RaycastHits.Clear();
        MyPhysics.CastRay(start, end, RaycastHits, 15);
        try
        {
            MyEntity ignoredTopMostParent = grid.GetTopMostParent();
            for (int i = 0; i < RaycastHits.Count; i++)
            {
                MyPhysics.HitInfo hit = RaycastHits[i];
                if (!(hit.HkHitInfo.GetHitEntity() is MyEntity entity))
                    continue;

                if (entity.GetTopMostParent() == ignoredTopMostParent)
                    continue;

                if (!(entity is MyVoxelBase))
                    continue;

                hitPosition = hit.Position;
                hitNormal = hit.HkHitInfo.Normal;
                if (hitNormal.LengthSquared() >= AutoDockConstants.MinConnectorDistanceSquared)
                    hitNormal.Normalize();
                else
                    hitNormal = -downDirection;

                distance = Vector3D.Dot(hitPosition - worldPosition, downDirection);
                return true;
            }

            return false;
        }
        finally
        {
            RaycastHits.Clear();
        }
    }

    private static Vector3D RejectFromAxis(Vector3D vector, Vector3D axis)
    {
        return vector - axis * Vector3D.Dot(vector, axis);
    }
}
