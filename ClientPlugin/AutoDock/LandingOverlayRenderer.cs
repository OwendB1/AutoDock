using System;
using System.Collections.Generic;
using SpaceEngineers.Game.Entities.Blocks;
using VRage.Game;
using VRage.Utils;
using VRageMath;
using VRageRender;

namespace ClientPlugin;

internal static class LandingOverlayRenderer
{
    private struct OrderedPoint
    {
        public double Angle;
        public Vector3D BasePosition;
        public Vector3D Position;
    }

    private static readonly MyStringId SquareMaterial = MyStringId.GetOrCompute("Square");
    private const double HullProjectionLift = 0.35;
    private const double TerrainProjectionLift = 0.55;
    private const double HullProjectionMarkerSize = 0.18;
    private const double TerrainProjectionMarkerSize = 0.24;
    private const float HullProjectionThickness = 0.065f;
    private const float TerrainProjectionThickness = 0.085f;
    private const float ProjectionPostThickness = 0.035f;
    private const float ProjectionIntensity = 2.8f;
    private const float ViolationIntensity = 3.2f;
    private const float HardpointIntensity = 2.2f;

    public static void DrawPreview(AutoLandingPlan plan)
    {
        Draw(plan, active: false);
    }

    public static void DrawActive(AutoLandingPlan plan)
    {
        Draw(plan, active: true);
    }

    private static void Draw(AutoLandingPlan plan, bool active)
    {
        if (plan == null || plan.Gears == null || plan.Hardpoints == null)
            return;

        Color gearColor = plan.HullClearanceOk
            ? (active ? new Color(0f, 1f, 0.25f, 0.65f) : new Color(0f, 0.85f, 1f, 0.55f))
            : new Color(1f, 0.15f, 0.1f, 0.65f);
        for (int i = 0; i < plan.Gears.Count; i++)
            DrawLandingGearBox(plan.Gears[i], gearColor);

        Color hullProjectionColor = plan.HullClearanceOk
            ? (active ? new Color(0.35f, 1f, 1f, 1f) : new Color(0.55f, 1f, 1f, 1f))
            : new Color(1f, 0.3f, 0.2f, 1f);
        Color terrainProjectionColor = plan.HullClearanceOk
            ? (active ? new Color(0.2f, 1f, 0.3f, 1f) : new Color(0.45f, 1f, 0.55f, 1f))
            : new Color(1f, 0.75f, 0.15f, 1f);
        DrawProjectedArea(plan, useTargetPositions: true, hullProjectionColor, HullProjectionLift, HullProjectionMarkerSize, HullProjectionThickness);
        DrawProjectedArea(plan, useTargetPositions: false, terrainProjectionColor, TerrainProjectionLift, TerrainProjectionMarkerSize, TerrainProjectionThickness);

        for (int i = 0; i < plan.Hardpoints.Count; i++)
        {
            LandingHardpointSample hardpoint = plan.Hardpoints[i];
            Vector3D start = active ? hardpoint.TargetWorldPosition : hardpoint.CurrentWorldPosition;
            if (!hardpoint.HasHit)
            {
                DrawMarker(start + plan.UpDirection * 0.2, new Color(1f, 0.15f, 0.1f, 1f), 0.16, HardpointIntensity);
                continue;
            }

            Color lineColor = hardpoint.TargetContactExpected
                ? new Color(0.2f, 1f, 0.3f, 1f)
                : new Color(1f, 0.85f, 0.2f, 1f);
            DrawLine(start, hardpoint.HitPosition + plan.UpDirection * 0.2, lineColor, active ? 0.06f : 0.05f);
            DrawMarker(hardpoint.HitPosition + plan.UpDirection * 0.2, lineColor, 0.16, HardpointIntensity);
        }

        DrawHullClearanceIntersections(plan);
    }

    private static void DrawLandingGearBox(MyLandingGear gear, Color color)
    {
        if (gear == null || gear.MarkedForClose || gear.PositionComp == null)
            return;

        MatrixD matrix = gear.PositionComp.WorldMatrixRef;
        BoundingBox localAabb = gear.PositionComp.LocalAABB;
        BoundingBoxD box = new BoundingBoxD(localAabb.Min, localAabb.Max);
        box.Min -= 0.05;
        box.Max += 0.05;

        MySimpleObjectDraw.DrawTransparentBox(
            ref matrix,
            ref box,
            ref color,
            MySimpleObjectRasterizer.SolidAndWireframe,
            1,
            0.02f,
            onlyFrontFaces: false,
            customViewProjection: -1,
            blendType: MyBillboard.BlendTypeEnum.LDR);
    }

    private static void DrawProjectedArea(
        AutoLandingPlan plan,
        bool useTargetPositions,
        Color color,
        double offset,
        double markerSize,
        float lineThickness)
    {
        var ordered = new List<OrderedPoint>();
        Vector3D centroid = Vector3D.Zero;
        if (!TryCollectProjectionPoints(plan, useTargetPositions, expectedOnly: true, offset, ordered, ref centroid)
            && !TryCollectProjectionPoints(plan, useTargetPositions, expectedOnly: false, offset, ordered, ref centroid))
            return;

        Vector3D planeRight = RejectFromAxis(plan.TargetGridMatrix.Right, plan.UpDirection);
        Vector3D planeForward = RejectFromAxis(plan.TargetGridMatrix.Forward, plan.UpDirection);
        if (planeRight.LengthSquared() < AutoDockConstants.MinConnectorDistanceSquared
            || planeForward.LengthSquared() < AutoDockConstants.MinConnectorDistanceSquared)
        {
            return;
        }

        centroid /= ordered.Count;
        planeRight.Normalize();
        planeForward.Normalize();
        for (int i = 0; i < ordered.Count; i++)
        {
            Vector3D delta = ordered[i].Position - centroid;
            ordered[i] = new OrderedPoint
            {
                Position = ordered[i].Position,
                Angle = Math.Atan2(Vector3D.Dot(delta, planeForward), Vector3D.Dot(delta, planeRight))
            };
        }

        ordered.Sort((left, right) => left.Angle.CompareTo(right.Angle));
        for (int i = 0; i < ordered.Count; i++)
        {
            DrawLine(ordered[i].BasePosition, ordered[i].Position, color, ProjectionPostThickness);
            DrawMarker(ordered[i].Position, color, markerSize, ProjectionIntensity);
        }

        if (ordered.Count < 2)
            return;

        for (int i = 0; i < ordered.Count; i++)
        {
            Vector3D start = ordered[i].Position;
            Vector3D end = ordered[(i + 1) % ordered.Count].Position;
            DrawLine(start, end, color, lineThickness);
        }
    }

    private static bool TryCollectProjectionPoints(
        AutoLandingPlan plan,
        bool useTargetPositions,
        bool expectedOnly,
        double offset,
        List<OrderedPoint> ordered,
        ref Vector3D centroid)
    {
        ordered.Clear();
        centroid = Vector3D.Zero;
        for (int i = 0; i < plan.Hardpoints.Count; i++)
        {
            LandingHardpointSample hardpoint = plan.Hardpoints[i];
            if (!hardpoint.HasHit)
                continue;

            if (expectedOnly && !hardpoint.TargetContactExpected)
                continue;

            Vector3D basePoint = useTargetPositions ? hardpoint.TargetWorldPosition : hardpoint.HitPosition;
            Vector3D point = basePoint + plan.UpDirection * offset;
            ordered.Add(new OrderedPoint { BasePosition = basePoint, Position = point });
            centroid += point;
        }

        return ordered.Count > 0;
    }

    private static void DrawHullClearanceIntersections(AutoLandingPlan plan)
    {
        if (plan.HullClearanceSamples == null || plan.HullClearanceOk)
            return;

        Color hullColor = new Color(1f, 0.15f, 0.15f, 1f);
        Color terrainColor = new Color(1f, 0.8f, 0.15f, 1f);
        for (int i = 0; i < plan.HullClearanceSamples.Count; i++)
        {
            LandingHullClearanceSample sample = plan.HullClearanceSamples[i];
            if (!sample.ViolatesClearance)
                continue;

            Vector3D hullPoint = sample.HullWorldPosition + plan.UpDirection * 0.28;
            DrawMarker(hullPoint, hullColor, 0.22, ViolationIntensity);
            if (!sample.HasTerrainHit)
                continue;

            Vector3D terrainPoint = sample.TerrainWorldPosition + plan.UpDirection * 0.42;
            DrawMarker(terrainPoint, terrainColor, 0.24, ViolationIntensity);
            DrawLine(sample.TerrainWorldPosition, terrainPoint, terrainColor, 0.045f);
            DrawLine(sample.HullWorldPosition, hullPoint, hullColor, 0.045f);
            DrawLine(hullPoint, terrainPoint, hullColor, 0.07f);
        }
    }

    private static void DrawMarker(Vector3D position, Color color, double size, float intensity = 1f)
    {
        MatrixD markerMatrix = MatrixD.CreateWorld(position, Vector3D.Forward, Vector3D.Up);
        BoundingBoxD localBox = new BoundingBoxD(
            new Vector3D(-size * 0.5, -size * 0.5, -size * 0.5),
            new Vector3D(size * 0.5, size * 0.5, size * 0.5));

        MySimpleObjectDraw.DrawTransparentBox(
            ref markerMatrix,
            ref localBox,
            ref color,
            MySimpleObjectRasterizer.SolidAndWireframe,
            0,
            0.018f,
            SquareMaterial,
            null,
            onlyFrontFaces: false,
            customViewProjection: -1,
            blendType: MyBillboard.BlendTypeEnum.AdditiveTop,
            intensity: intensity);
    }

    private static void DrawLine(Vector3D start, Vector3D end, Color color, float thickness)
    {
        Vector4 lineColor = color.ToVector4();
        MySimpleObjectDraw.DrawLine(start, end, null, ref lineColor, thickness, MyBillboard.BlendTypeEnum.AdditiveTop);
    }

    private static Vector3D RejectFromAxis(Vector3D vector, Vector3D axis)
    {
        return vector - axis * Vector3D.Dot(vector, axis);
    }
}
