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
        public Vector3D Position;
    }

    private const double HullProjectionLift = 0.03;
    private const double TerrainProjectionLift = 0.05;
    private const float HullProjectionMarkerSize = 0.11f;
    private const float TerrainProjectionMarkerSize = 0.14f;
    private const float ViolationMarkerSize = 0.15f;
    private const float HardpointMarkerSize = 0.1f;
    private const float ProjectionLineThickness = 1f;
    private const float HardpointLineThickness = 0.8f;
    private const float ViolationLineThickness = 1.15f;
    private const double MinLineOffset = 0.03;
    private const double MaxLineOffset = 0.12;
    private const double DistanceLineOffsetScale = 0.0022;

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
        DrawProjectedArea(plan, useTargetPositions: true, hullProjectionColor, HullProjectionLift, HullProjectionMarkerSize, ProjectionLineThickness);
        DrawProjectedArea(plan, useTargetPositions: false, terrainProjectionColor, TerrainProjectionLift, TerrainProjectionMarkerSize, ProjectionLineThickness);

        for (int i = 0; i < plan.Hardpoints.Count; i++)
        {
            LandingHardpointSample hardpoint = plan.Hardpoints[i];
            Vector3D start = active ? hardpoint.TargetWorldPosition : hardpoint.CurrentWorldPosition;
            if (!hardpoint.HasHit)
            {
                DrawMarker(start + plan.UpDirection * 0.06, new Color(1f, 0.15f, 0.1f, 1f), HardpointMarkerSize);
                continue;
            }

            Color lineColor = hardpoint.TargetContactExpected
                ? new Color(0.2f, 1f, 0.3f, 1f)
                : new Color(1f, 0.85f, 0.2f, 1f);
            DrawLine(start, hardpoint.HitPosition + plan.UpDirection * 0.05, lineColor, HardpointLineThickness);
            DrawMarker(hardpoint.HitPosition + plan.UpDirection * 0.05, lineColor, HardpointMarkerSize);
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
        float markerSize,
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
            DrawMarker(ordered[i].Position, color, markerSize);

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

            Vector3D point = (useTargetPositions ? hardpoint.TargetWorldPosition : hardpoint.HitPosition) + plan.UpDirection * offset;
            ordered.Add(new OrderedPoint { Position = point });
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

            Vector3D hullPoint = sample.HullWorldPosition + plan.UpDirection * 0.04;
            DrawMarker(hullPoint, hullColor, ViolationMarkerSize);
            if (!sample.HasTerrainHit)
                continue;

            Vector3D terrainPoint = sample.TerrainWorldPosition + plan.UpDirection * 0.06;
            DrawMarker(terrainPoint, terrainColor, ViolationMarkerSize);
            DrawLine(hullPoint, terrainPoint, hullColor, ViolationLineThickness);
        }
    }

    private static void DrawMarker(Vector3D position, Color color, float radius)
    {
        MyRenderProxy.DebugDrawSphere(position, radius, color, alpha: 1f, depthRead: false, smooth: true, cull: false);
    }

    private static void DrawLine(Vector3D start, Vector3D end, Color color, float thickness)
    {
        DrawRawLine(start, end, color);

        Vector3D lineDirection = end - start;
        if (lineDirection.LengthSquared() < 1e-6)
            return;

        Vector3D midpoint = (start + end) * 0.5;
        Vector3D cameraPosition = MyTransparentGeometry.HasCamera
            ? MyTransparentGeometry.Camera.Translation
            : midpoint + Vector3D.Up;
        Vector3D toCamera = cameraPosition - midpoint;
        Vector3D lineAxis = Vector3D.Normalize(lineDirection);

        Vector3D sideOffset = Vector3D.Cross(lineAxis, toCamera);
        if (sideOffset.LengthSquared() < 1e-6)
            sideOffset = Vector3D.Cross(lineAxis, Vector3D.Up);
        if (sideOffset.LengthSquared() < 1e-6)
            sideOffset = Vector3D.Cross(lineAxis, Vector3D.Right);
        if (sideOffset.LengthSquared() < 1e-6)
            return;

        sideOffset.Normalize();
        Vector3D upOffset = Vector3D.Cross(lineAxis, sideOffset);
        if (upOffset.LengthSquared() < 1e-6)
            return;

        upOffset.Normalize();
        double cameraDistance = Math.Max(Math.Sqrt(toCamera.LengthSquared()), 1.0);
        double offset = MathHelper.Clamp(
            (float)(cameraDistance * DistanceLineOffsetScale * thickness),
            (float)(MinLineOffset * thickness),
            (float)(MaxLineOffset * thickness));

        DrawRawLine(start + sideOffset * offset, end + sideOffset * offset, color);
        DrawRawLine(start - sideOffset * offset, end - sideOffset * offset, color);
        DrawRawLine(start + upOffset * offset, end + upOffset * offset, color);
        DrawRawLine(start - upOffset * offset, end - upOffset * offset, color);
    }

    private static void DrawRawLine(Vector3D start, Vector3D end, Color color)
    {
        MyRenderProxy.DebugDrawLine3D(start, end, color, color, depthRead: false);
    }

    private static Vector3D RejectFromAxis(Vector3D vector, Vector3D axis)
    {
        return vector - axis * Vector3D.Dot(vector, axis);
    }
}
