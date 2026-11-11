using System;
using System.Collections.Generic;
using ClientPlugin.Settings;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.World;
using VRage.Utils;
using VRageMath;

namespace ClientPlugin;

internal sealed class LockRotationService
{
    private readonly Dictionary<(Guid WorldId, long GridId, long LocalId, long TargetId), int> selectedRotationSteps =
        new Dictionary<(Guid, long, long, long), int>();

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

            double distance = Vector3D.Distance(
                DockingMath.GetConnectorReferencePosition(localConnector),
                DockingMath.GetConnectorReferencePosition(targetConnector));
            pair = new DockingPair(
                localConnector,
                targetConnector,
                distance,
                DockingMath.IsLockReady(localConnector, targetConnector),
                HasRememberedRotation(localConnector, targetConnector));
            return true;
        }

        return false;
    }

    public bool HasRememberedRotation(MyShipConnector localConnector, MyShipConnector targetConnector)
    {
        return localConnector?.CubeGrid != null && targetConnector != null
               && FindRememberedRotation(GetKey(localConnector, targetConnector)) != null;
    }

    public bool TryGetRotationStep(DockingPair pair, out int rotationStep)
    {
        rotationStep = 0;
        if (pair?.Local?.CubeGrid == null || pair.Target == null)
            return false;

        var key = GetKey(pair.Local, pair.Target);
        if (selectedRotationSteps.TryGetValue(key, out rotationStep))
            return true;

        DockingRotationPreference remembered = FindRememberedRotation(key);
        rotationStep = DockingMath.NormalizeRotationStep(remembered?.RotationStep ?? DockingMath.GetDefaultRotationStep(pair));
        // Choose the default once: ship motion must not rotate the preview or the pilot's target.
        selectedRotationSteps[key] = rotationStep;
        return true;
    }

    public int GetRotationStep(DockingPair pair)
    {
        return TryGetRotationStep(pair, out int rotationStep)
            ? rotationStep
            : 0;
    }

    public int CycleRotationStep(DockingPair pair, int delta)
    {
        int nextStep = DockingMath.NormalizeRotationStep(GetRotationStep(pair) + delta);
        if (pair?.Local?.CubeGrid != null && pair.Target != null)
            selectedRotationSteps[GetKey(pair.Local, pair.Target)] = nextStep;
        return nextStep;
    }

    public bool RememberSelectedRotation(DockingPair pair)
    {
        return TryGetRotationStep(pair, out int rotationStep) && RememberRotationStep(pair, rotationStep);
    }

    public bool RememberCurrentRotation(DockingPair pair)
    {
        if (pair?.Local == null || pair.Target == null)
            return false;

        // A completed lock must not overwrite the choice saved when the pilot started.
        if (HasRememberedRotation(pair.Local, pair.Target))
            return false;

        if (!DockingMath.TryGetActualRotationStep(pair.Local, pair.Target, out int rotationStep))
            return false;

        return RememberRotationStep(pair, rotationStep);
    }

    public bool TryGetDesiredLocalConnectorWorldMatrix(DockingPair pair, out MatrixD desiredLocalConnectorWorldMatrix)
    {
        desiredLocalConnectorWorldMatrix = MatrixD.Identity;
        return pair != null
               && DockingMath.TryCreateDesiredLocalConnectorWorldMatrix(pair.Target, GetRotationStep(pair), out desiredLocalConnectorWorldMatrix);
    }

    public void ClearSelectedRotations()
    {
        selectedRotationSteps.Clear();
    }

    private bool RememberRotationStep(DockingPair pair, int rotationStep)
    {
        if (pair?.Local?.CubeGrid == null || pair.Target == null
            || pair.Local.EntityId == 0 || pair.Target.EntityId == 0)
            return false;

        var key = GetKey(pair.Local, pair.Target);
        int normalizedRotationStep = DockingMath.NormalizeRotationStep(rotationStep);
        selectedRotationSteps[key] = normalizedRotationStep;
        DockingRotationPreference remembered = FindRememberedRotation(key);
        if (remembered != null && remembered.RotationStep == normalizedRotationStep)
            return true;

        if (remembered == null)
        {
            remembered = new DockingRotationPreference
            {
                WorldId = key.WorldId,
                GridId = key.GridId,
                LocalConnectorId = key.LocalId,
                TargetConnectorId = key.TargetId
            };
            Config.Current.DockingRotations ??= new List<DockingRotationPreference>();
            Config.Current.DockingRotations.Add(remembered);
        }
        remembered.RotationStep = normalizedRotationStep;
        try
        {
            ConfigStorage.Save(Config.Current);
        }
        catch (Exception exception)
        {
            MyLog.Default.Warning($"AutoDock: failed to save docking rotation: {exception}");
        }
        return true;
    }

    private static (Guid WorldId, long GridId, long LocalId, long TargetId) GetKey(MyShipConnector local, MyShipConnector target)
    {
        return (MySession.Static?.WorldId ?? Guid.Empty, local.CubeGrid.EntityId, local.EntityId, target.EntityId);
    }

    private static DockingRotationPreference FindRememberedRotation((Guid WorldId, long GridId, long LocalId, long TargetId) key)
    {
        return Config.Current.DockingRotations?.Find(rotation => rotation != null
            && rotation.WorldId == key.WorldId && rotation.GridId == key.GridId
            && rotation.LocalConnectorId == key.LocalId && rotation.TargetConnectorId == key.TargetId);
    }
}
