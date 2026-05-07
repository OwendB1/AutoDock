using Sandbox.Game.Entities.Cube;
using VRage.Game;
using VRage.Utils;
using VRageMath;
using VRageRender;

namespace ClientPlugin;

internal static class DockingOverlayRenderer
{
    private static readonly MyStringId SquareMaterial = MyStringId.GetOrCompute("Square");

    public static void DrawActivePair(DockingPair pair, LockRotationService lockRotationService)
    {
        if (pair == null)
            return;

        pair.RefreshMetrics();
        DrawConnectorBox(pair.Local, GetBoxColor(pair, selected: true), selected: true);
        DrawConnectorBox(pair.Target, GetBoxColor(pair, selected: true), selected: true);
        DrawConnectionLine(pair, GetLineColor(pair, selected: true), selected: true);
        DrawRotationArrow(pair, lockRotationService, new Color(0f, 1f, 0.25f, 0.85f));
    }

    public static void DrawPreview(bool active, System.Collections.Generic.IReadOnlyList<DockingPair> pairs, int selectedIndex, LockRotationService lockRotationService)
    {
        if (!active || pairs == null || pairs.Count == 0)
            return;

        for (int i = 0; i < pairs.Count; i++)
        {
            DockingPair pair = pairs[i];
            pair.RefreshMetrics();
            bool selected = i == selectedIndex;
            Color boxColor = GetBoxColor(pair, selected);
            Color lineColor = GetLineColor(pair, selected);

            DrawConnectorBox(pair.Local, boxColor, selected);
            DrawConnectorBox(pair.Target, boxColor, selected);
            DrawConnectionLine(pair, lineColor, selected);
            if (selected)
                DrawRotationArrow(pair, lockRotationService, new Color(0f, 0.85f, 1f, 0.85f));
        }
    }

    private static Color GetBoxColor(DockingPair pair, bool selected)
    {
        if (!pair.InRange)
            return selected ? new Color(1f, 0.9f, 0f, 0.55f) : new Color(1f, 0.9f, 0f, 0.32f);

        return selected ? new Color(0f, 0.7f, 1f, 0.6f) : new Color(0.55f, 0.55f, 0.55f, 0.32f);
    }

    private static Color GetLineColor(DockingPair pair, bool selected)
    {
        if (!pair.InRange)
            return selected ? new Color(1f, 0f, 0f, 0.75f) : new Color(1f, 0f, 0f, 0.45f);

        return selected ? new Color(0f, 1f, 0.25f, 0.75f) : new Color(1f, 0.62f, 0f, 0.6f);
    }

    private static void DrawConnectorBox(MyShipConnector connector, Color color, bool selected)
    {
        if (connector == null || connector.MarkedForClose)
            return;

        MatrixD matrix = connector.PositionComp.WorldMatrixRef;
        BoundingBox localAabb = connector.PositionComp.LocalAABB;
        BoundingBoxD box = new BoundingBoxD(localAabb.Min, localAabb.Max);
        double padding = selected ? 0.08 : 0.04;
        box.Min -= padding;
        box.Max += padding;

        MySimpleObjectDraw.DrawTransparentBox(
            ref matrix,
            ref box,
            ref color,
            MySimpleObjectRasterizer.SolidAndWireframe,
            1,
            selected ? 0.035f : 0.018f,
            onlyFrontFaces: false,
            customViewProjection: -1,
            blendType: MyBillboard.BlendTypeEnum.LDR);
    }

    private static void DrawConnectionLine(DockingPair pair, Color color, bool selected)
    {
        if (pair.Local == null || pair.Target == null || pair.Local.MarkedForClose || pair.Target.MarkedForClose)
            return;

        Vector3D start = DockingMath.GetConnectorReferencePosition(pair.Local);
        Vector3D end = DockingMath.GetConnectorReferencePosition(pair.Target);
        Vector4 lineColor = color.ToVector4();
        MySimpleObjectDraw.DrawLine(start, end, null, ref lineColor, selected ? 0.05f : 0.025f);
    }

    private static void DrawRotationArrow(DockingPair pair, LockRotationService lockRotationService, Color color)
    {
        if (pair?.Target == null || pair.Target.MarkedForClose || lockRotationService == null)
            return;

        if (!DockingMath.TryGetDesiredOrientationIndicator(
                pair,
                lockRotationService.GetRotationStep(pair),
                out Vector3D faceNormal,
                out Vector3D direction,
                out Vector3D right))
            return;

        Vector3D origin = DockingMath.GetConnectorReferencePosition(pair.Target) + faceNormal * 0.24;
        Vector3D tip = origin + direction * 1.52;
        Vector3D tail = origin - direction * 0.40;
        Vector3D headBase = tip - direction * 0.48;
        Vector3D wingOffset = right * 0.36;
        DrawArrowSegment(tail, tip, faceNormal, color, 0.14, 0.04);
        DrawArrowSegment(tip, headBase + wingOffset, faceNormal, color, 0.14, 0.04);
        DrawArrowSegment(tip, headBase - wingOffset, faceNormal, color, 0.14, 0.04);
    }

    private static void DrawArrowSegment(Vector3D start, Vector3D end, Vector3D faceNormal, Color color, double width, double depth)
    {
        Vector3D segment = end - start;
        double segmentLength = segment.Length();
        if (segmentLength * segmentLength < AutoDockConstants.MinConnectorDistanceSquared)
            return;

        segment /= segmentLength;
        MatrixD worldMatrix = MatrixD.CreateWorld((start + end) * 0.5, segment, faceNormal);
        BoundingBoxD localBox = new BoundingBoxD(
            new Vector3D(-width * 0.5, -depth * 0.5, -segmentLength * 0.5),
            new Vector3D(width * 0.5, depth * 0.5, segmentLength * 0.5));

        MySimpleObjectDraw.DrawTransparentBox(
            ref worldMatrix,
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
}
