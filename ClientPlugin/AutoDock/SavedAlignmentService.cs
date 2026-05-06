using System.Collections.Generic;
using ClientPlugin.Settings;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.World;
using Sandbox.ModAPI.Ingame;
using VRageMath;

namespace ClientPlugin;

internal sealed class SavedAlignmentService
{
    public List<SavedConnectorAlignment> SavedAlignments => Config.Current.SavedAlignments;

    public bool TryGetLockedPair(out DockingPair pair)
    {
        pair = null;

        MyCubeGrid controlledGrid = MySession.Static?.ControlledGrid;
        if (controlledGrid == null || controlledGrid.MarkedForClose)
            return false;

        foreach (MyShipConnector localConnector in controlledGrid.GetFatBlocks<MyShipConnector>())
        {
            if (localConnector == null
                || localConnector.MarkedForClose
                || !localConnector.IsWorking
                || localConnector.CubeGrid == null
                || localConnector.CubeGrid.MarkedForClose
                || !localConnector.HasLocalPlayerAccess())
                continue;

            MyShipConnector targetConnector = localConnector.Other;
            if (targetConnector == null
                || targetConnector.MarkedForClose
                || targetConnector.CubeGrid == null
                || targetConnector.CubeGrid.MarkedForClose
                || targetConnector.CubeGrid == localConnector.CubeGrid)
                continue;

            if (!DockingMath.IsLockedPair(localConnector, targetConnector))
                continue;

            double distance = Vector3D.Distance(localConnector.PositionComp.GetPosition(), targetConnector.PositionComp.GetPosition());
            pair = new DockingPair(
                localConnector,
                targetConnector,
                distance,
                DockingMath.IsLockReady(localConnector, targetConnector),
                HasSavedAlignment(localConnector.EntityId, targetConnector.EntityId));
            return true;
        }

        return false;
    }

    public bool TryGetSavedConnectorAlignmentMatrix(DockingPair pair, out MatrixD relativeConnectorMatrix)
    {
        relativeConnectorMatrix = MatrixD.Identity;
        if (!TryGetSavedAlignment(pair.Local.EntityId, pair.Target.EntityId, out SavedConnectorAlignment savedAlignment, out bool invert))
            return false;

        relativeConnectorMatrix = savedAlignment.GetRelativeConnectorMatrix();
        if (invert)
            relativeConnectorMatrix = MatrixD.Invert(relativeConnectorMatrix);

        relativeConnectorMatrix.Translation = Vector3D.Zero;
        return relativeConnectorMatrix.IsValid();
    }

    public bool TryGetSavedAlignment(long localConnectorId, long targetConnectorId, out SavedConnectorAlignment savedAlignment, out bool invert)
    {
        List<SavedConnectorAlignment> savedAlignments = Config.Current.SavedAlignments;
        for (int i = 0; i < savedAlignments.Count; i++)
        {
            SavedConnectorAlignment candidate = savedAlignments[i];
            if (candidate.Matches(localConnectorId, targetConnectorId))
            {
                savedAlignment = candidate;
                invert = false;
                return true;
            }

            if (candidate.Matches(targetConnectorId, localConnectorId))
            {
                savedAlignment = candidate;
                invert = true;
                return true;
            }
        }

        savedAlignment = null;
        invert = false;
        return false;
    }

    public bool HasSavedAlignment(long localConnectorId, long targetConnectorId)
    {
        return TryGetSavedAlignment(localConnectorId, targetConnectorId, out _, out _);
    }

    public string GetGridDisplayName(MyCubeGrid grid)
    {
        if (grid == null)
            return "Unknown grid";

        return string.IsNullOrWhiteSpace(grid.DisplayName) ? "Unknown grid" : grid.DisplayName;
    }

    public bool TryBuildRelativeConnectorAlignment(MyShipConnector localConnector, MyShipConnector targetConnector, out MatrixD relativeConnectorMatrix)
    {
        relativeConnectorMatrix = MatrixD.Identity;
        if (localConnector == null || targetConnector == null)
            return false;

        MatrixD localConnectorWorldMatrix = localConnector.WorldMatrix;
        localConnectorWorldMatrix.Translation = Vector3D.Zero;
        MatrixD targetConnectorWorldMatrix = targetConnector.WorldMatrix;
        targetConnectorWorldMatrix.Translation = Vector3D.Zero;

        relativeConnectorMatrix = localConnectorWorldMatrix * MatrixD.Invert(targetConnectorWorldMatrix);
        relativeConnectorMatrix.Translation = Vector3D.Zero;
        return relativeConnectorMatrix.IsValid();
    }

    public void SaveAlignment(DockingPair pair, MatrixD relativeConnectorMatrix)
    {
        SavedConnectorAlignment savedAlignment = GetOrCreateSavedAlignment(pair.Local.EntityId, pair.Target.EntityId);
        savedAlignment.SetRelativeConnectorMatrix(relativeConnectorMatrix);
        savedAlignment.LocalGridName = GetGridDisplayName(pair.Local.CubeGrid);
        savedAlignment.TargetGridName = GetGridDisplayName(pair.Target.CubeGrid);
        ConfigStorage.Save(Config.Current);
    }

    public void RemoveSavedAlignment(SavedConnectorAlignment savedAlignment)
    {
        if (savedAlignment == null || !Config.Current.SavedAlignments.Remove(savedAlignment))
            return;

        ConfigStorage.Save(Config.Current);
    }

    private SavedConnectorAlignment GetOrCreateSavedAlignment(long localConnectorId, long targetConnectorId)
    {
        List<SavedConnectorAlignment> savedAlignments = Config.Current.SavedAlignments;
        for (int i = 0; i < savedAlignments.Count; i++)
        {
            SavedConnectorAlignment alignment = savedAlignments[i];
            if (!alignment.Matches(localConnectorId, targetConnectorId)
                && !alignment.Matches(targetConnectorId, localConnectorId))
                continue;

            alignment.LocalConnectorId = localConnectorId;
            alignment.TargetConnectorId = targetConnectorId;
            return alignment;
        }

        var newAlignment = new SavedConnectorAlignment
        {
            LocalConnectorId = localConnectorId,
            TargetConnectorId = targetConnectorId
        };
        savedAlignments.Add(newAlignment);
        return newAlignment;
    }
}
