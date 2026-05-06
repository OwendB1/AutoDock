using Sandbox.Game.Entities.Cube;
using VRage.Game;
using VRageMath;
using VRageRender;

namespace ClientPlugin;

internal static class DockingOverlayRenderer
{
    public static void DrawActivePair(DockingPair pair)
    {
        if (pair == null)
            return;

        pair.RefreshMetrics();
        DrawConnectorBox(pair.Local, GetBoxColor(pair, selected: true), selected: true);
        DrawConnectorBox(pair.Target, GetBoxColor(pair, selected: true), selected: true);
        DrawConnectionLine(pair, GetLineColor(pair, selected: true), selected: true);
    }

    public static void DrawPreview(bool active, System.Collections.Generic.IReadOnlyList<DockingPair> pairs, int selectedIndex)
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

        return selected ? new Color(0f, 1f, 0.25f, 0.75f) : new Color(1f, 0.85f, 0f, 0.45f);
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

        Vector3D start = pair.Local.PositionComp.GetPosition();
        Vector3D end = pair.Target.PositionComp.GetPosition();
        Vector4 lineColor = color.ToVector4();
        MySimpleObjectDraw.DrawLine(start, end, null, ref lineColor, selected ? 0.05f : 0.025f, MyBillboard.BlendTypeEnum.LDR);
    }
}
