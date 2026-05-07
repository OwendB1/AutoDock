using System.Collections.Generic;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.World;
using Sandbox.ModAPI.Ingame;
using VRageMath;

namespace ClientPlugin;

internal sealed class LockRotationService
{
    private readonly Dictionary<PairKey, int> rememberedRotationSteps = new Dictionary<PairKey, int>();
    private readonly Dictionary<PairKey, int> selectedRotationSteps = new Dictionary<PairKey, int>();

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
                HasRememberedRotation(localConnector.EntityId, targetConnector.EntityId));
            return true;
        }

        return false;
    }

    public bool HasRememberedRotation(long localConnectorId, long targetConnectorId)
    {
        return rememberedRotationSteps.ContainsKey(GetCanonicalKey(localConnectorId, targetConnectorId));
    }

    public bool TryGetRotationStep(DockingPair pair, out int rotationStep)
    {
        rotationStep = 0;
        if (pair == null)
            return false;

        PairKey key = GetCanonicalKey(pair.Local.EntityId, pair.Target.EntityId);
        if (selectedRotationSteps.TryGetValue(key, out rotationStep)
            || rememberedRotationSteps.TryGetValue(key, out rotationStep))
        {
            rotationStep = DockingMath.NormalizeRotationStep(rotationStep);
            return true;
        }

        rotationStep = DockingMath.GetDefaultRotationStep(pair);
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
        RememberSelectedRotationStep(pair?.Local?.EntityId ?? 0L, pair?.Target?.EntityId ?? 0L, nextStep);
        return nextStep;
    }

    public bool RememberCurrentRotation(DockingPair pair)
    {
        if (pair?.Local == null || pair.Target == null)
            return false;

        if (!DockingMath.TryGetActualRotationStep(pair.Local, pair.Target, out int rotationStep))
            return false;

        RememberRotationStep(pair.Local.EntityId, pair.Target.EntityId, rotationStep);
        return true;
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

    public void ClearRememberedRotations()
    {
        rememberedRotationSteps.Clear();
        selectedRotationSteps.Clear();
    }

    private void RememberRotationStep(long localConnectorId, long targetConnectorId, int rotationStep)
    {
        if (localConnectorId == 0 || targetConnectorId == 0)
            return;

        PairKey key = GetCanonicalKey(localConnectorId, targetConnectorId);
        int normalizedRotationStep = DockingMath.NormalizeRotationStep(rotationStep);
        rememberedRotationSteps[key] = normalizedRotationStep;
        selectedRotationSteps[key] = normalizedRotationStep;
    }

    private void RememberSelectedRotationStep(long localConnectorId, long targetConnectorId, int rotationStep)
    {
        if (localConnectorId == 0 || targetConnectorId == 0)
            return;

        selectedRotationSteps[GetCanonicalKey(localConnectorId, targetConnectorId)] = DockingMath.NormalizeRotationStep(rotationStep);
    }

    private static PairKey GetCanonicalKey(long leftEntityId, long rightEntityId)
    {
        return leftEntityId <= rightEntityId
            ? new PairKey(leftEntityId, rightEntityId)
            : new PairKey(rightEntityId, leftEntityId);
    }
}
