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

    private static readonly MyStringId SquareMaterial = MyStringId.GetOrCompute("Square");

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

        for (int i = 0; i < plan.Hardpoints.Count; i++)
        {
            LandingHardpointSample hardpoint = plan.Hardpoints[i];
            Vector3D start = active ? hardpoint.TargetWorldPosition : hardpoint.CurrentWorldPosition;
            if (!hardpoint.HasHit)
            {
                DrawMarker(start, new Color(1f, 0f, 0f, 0.7f), 0.08);
                continue;
            }

            Color lineColor = hardpoint.TargetContactExpected
                ? new Color(0f, 1f, 0.25f, 0.7f)
                : new Color(1f, 0.75f, 0f, 0.7f);
            DrawLine(start, hardpoint.HitPosition, lineColor, active ? 0.045f : 0.03f);
            DrawMarker(hardpoint.HitPosition + plan.UpDirection * 0.04, lineColor, 0.09);
        }

        DrawProjectedArea(plan);
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

    private static void DrawProjectedArea(AutoLandingPlan plan)
    {
        var ordered = new List<OrderedPoint>();
        Vector3D centroid = Vector3D.Zero;
        for (int i = 0; i < plan.Hardpoints.Count; i++)
        {
            if (!plan.Hardpoints[i].HasHit)
                continue;

            Vector3D point = plan.Hardpoints[i].HitPosition + plan.UpDirection * 0.05;
            ordered.Add(new OrderedPoint { Position = point });
            centroid += point;
        }

        if (ordered.Count < 2)
            return;

        centroid /= ordered.Count;
        Vector3D planeRight = RejectFromAxis(plan.TargetGridMatrix.Right, plan.UpDirection);
        Vector3D planeForward = RejectFromAxis(plan.TargetGridMatrix.Forward, plan.UpDirection);
        if (planeRight.LengthSquared() < AutoDockConstants.MinConnectorDistanceSquared
            || planeForward.LengthSquared() < AutoDockConstants.MinConnectorDistanceSquared)
        {
            return;
        }

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
        Color areaColor = plan.HullClearanceOk
            ? new Color(0f, 1f, 0.25f, 0.75f)
            : new Color(1f, 0.2f, 0f, 0.75f);
        for (int i = 0; i < ordered.Count; i++)
        {
            Vector3D start = ordered[i].Position;
            Vector3D end = ordered[(i + 1) % ordered.Count].Position;
            DrawLine(start, end, areaColor, 0.035f);
        }
    }

    private static void DrawMarker(Vector3D position, Color color, double size)
    {
        MatrixD markerMatrix = MatrixD.CreateWorld(position, Vector3D.Forward, Vector3D.Up);
        BoundingBoxD localBox = new BoundingBoxD(
            new Vector3D(-size * 0.5, -size * 0.5, -size * 0.5),
            new Vector3D(size * 0.5, size * 0.5, size * 0.5));

        MySimpleObjectDraw.DrawTransparentBox(
            ref markerMatrix,
            ref localBox,
            ref color,
            MySimpleObjectRasterizer.Solid,
            0,
            0.001f,
            SquareMaterial,
            null,
            onlyFrontFaces: false,
            customViewProjection: -1,
            blendType: MyBillboard.BlendTypeEnum.LDR);
    }

    private static void DrawLine(Vector3D start, Vector3D end, Color color, float thickness)
    {
        Vector4 lineColor = color.ToVector4();
        MySimpleObjectDraw.DrawLine(start, end, null, ref lineColor, thickness);
    }

    private static Vector3D RejectFromAxis(Vector3D vector, Vector3D axis)
    {
        return vector - axis * Vector3D.Dot(vector, axis);
    }
}
