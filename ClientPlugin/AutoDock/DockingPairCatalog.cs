using System.Collections.Generic;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.World;
using VRage.Game.Entity;
using VRageMath;

namespace ClientPlugin;

internal sealed class DockingPairCatalog
{
    private readonly SavedAlignmentService savedAlignmentService;
    private readonly List<DockingPair> pairs = new List<DockingPair>();
    private readonly List<MyShipConnector> localConnectors = new List<MyShipConnector>();
    private readonly Dictionary<long, List<DockingPair>> pairsByLocalConnectorId = new Dictionary<long, List<DockingPair>>();
    private readonly HashSet<PairKey> pairKeys = new HashSet<PairKey>();
    private int selectedIndex = -1;
    private int selectedConnectorIndex = -1;
    private long preferredLocalConnectorEntityId;

    public DockingPairCatalog(SavedAlignmentService savedAlignmentService)
    {
        this.savedAlignmentService = savedAlignmentService;
    }

    public IReadOnlyList<DockingPair> Pairs => pairs;
    public int SelectedIndex => selectedIndex;
    public int SelectedConnectorIndex => selectedConnectorIndex;
    public int ConnectorCount => localConnectors.Count;
    public int PairCount => pairs.Count;

    public void RememberPreferredConnector(MyShipConnector localConnector)
    {
        if (localConnector != null)
            preferredLocalConnectorEntityId = localConnector.EntityId;
    }

    public void RescanPairs(bool preserveSelection)
    {
        long previousLocalId = 0;
        long previousTargetId = 0;
        int previousPairIndex = selectedIndex < 0 ? 0 : selectedIndex;

        if (preserveSelection && TryGetSelectedPair(out DockingPair selectedPair))
        {
            previousLocalId = selectedPair.Local.EntityId;
            previousTargetId = selectedPair.Target.EntityId;
        }

        pairs.Clear();
        pairKeys.Clear();
        localConnectors.Clear();
        pairsByLocalConnectorId.Clear();

        MyCubeGrid controlledGrid = MySession.Static?.ControlledGrid;
        if (controlledGrid == null || controlledGrid.MarkedForClose)
        {
            selectedConnectorIndex = -1;
            selectedIndex = -1;
            return;
        }

        var availableLocalConnectors = new List<MyShipConnector>();
        foreach (MyShipConnector connector in controlledGrid.GetFatBlocks<MyShipConnector>())
        {
            if (DockingMath.IsConnectorAvailable(connector, localSide: true))
                availableLocalConnectors.Add(connector);
        }

        availableLocalConnectors.Sort(CompareLocalConnectors);

        foreach (MyShipConnector localConnector in availableLocalConnectors)
        {
            var connectorPairs = new List<DockingPair>();
            AddPairsNear(localConnector, AutoDockConstants.SoftSelectRadius, connectorPairs);
            if (connectorPairs.Count == 0)
                continue;

            connectorPairs.Sort(ComparePairs);
            localConnectors.Add(localConnector);
            pairsByLocalConnectorId[localConnector.EntityId] = connectorPairs;
        }

        if (localConnectors.Count == 0)
        {
            selectedConnectorIndex = -1;
            selectedIndex = -1;
            return;
        }

        long desiredLocalId = previousLocalId == 0 ? preferredLocalConnectorEntityId : previousLocalId;
        selectedConnectorIndex = FindLocalConnectorIndex(desiredLocalId);
        if (selectedConnectorIndex < 0)
            selectedConnectorIndex = 0;

        SelectConnectorByIndex(
            selectedConnectorIndex,
            preserveSelection ? previousPairIndex : 0,
            preserveSelection ? previousTargetId : 0);
    }

    public bool CyclePair(int offset)
    {
        if (pairs.Count == 0 || offset == 0)
            return false;

        if (selectedIndex < 0)
            selectedIndex = 0;

        int nextPairIndex = selectedIndex + offset;
        if (nextPairIndex >= 0 && nextPairIndex < pairs.Count)
        {
            selectedIndex = nextPairIndex;
            return true;
        }

        if (localConnectors.Count < 2)
            return false;

        int nextConnectorIndex = selectedConnectorIndex + (offset > 0 ? 1 : -1);
        if (nextConnectorIndex < 0)
            nextConnectorIndex = localConnectors.Count - 1;
        else if (nextConnectorIndex >= localConnectors.Count)
            nextConnectorIndex = 0;

        int connectorPairIndex = offset > 0 ? 0 : int.MaxValue;
        SelectConnectorByIndex(nextConnectorIndex, connectorPairIndex);
        return true;
    }

    public bool CycleConnector(int offset)
    {
        if (localConnectors.Count < 2 || offset == 0)
            return false;

        int nextConnectorIndex = selectedConnectorIndex + offset;
        if (nextConnectorIndex < 0)
            nextConnectorIndex = localConnectors.Count - 1;
        else if (nextConnectorIndex >= localConnectors.Count)
            nextConnectorIndex = 0;

        int connectorPairIndex = offset > 0 ? 0 : int.MaxValue;
        SelectConnectorByIndex(nextConnectorIndex, connectorPairIndex);
        return true;
    }

    public bool TryGetSelectedPair(out DockingPair pair)
    {
        if (selectedIndex >= 0 && selectedIndex < pairs.Count)
        {
            pair = pairs[selectedIndex];
            return true;
        }

        pair = null;
        return false;
    }

    public void ClearTransientState()
    {
        selectedConnectorIndex = -1;
        selectedIndex = -1;
        pairs.Clear();
        pairKeys.Clear();
        localConnectors.Clear();
        pairsByLocalConnectorId.Clear();
    }

    private void AddPairsNear(MyShipConnector localConnector, float radius, List<DockingPair> connectorPairs)
    {
        Vector3D position = localConnector.PositionComp.GetPosition();
        BoundingSphereD sphere = new BoundingSphereD(position, radius);
        List<MyEntity> entities = MyEntities.GetEntitiesInSphere(ref sphere);

        try
        {
            foreach (MyEntity entity in entities)
            {
                if (entity is MyShipConnector targetConnector)
                {
                    AddPairIfCompatible(localConnector, targetConnector, radius, connectorPairs);
                    continue;
                }

                if (entity is MyCubeGrid targetGrid)
                    AddGridPairs(localConnector, targetGrid, radius, connectorPairs);
            }
        }
        finally
        {
            entities.Clear();
        }
    }

    private void AddGridPairs(MyShipConnector localConnector, MyCubeGrid targetGrid, float radius, List<DockingPair> connectorPairs)
    {
        if (targetGrid == null || targetGrid.MarkedForClose || targetGrid == localConnector.CubeGrid)
            return;

        foreach (MyShipConnector targetConnector in targetGrid.GetFatBlocks<MyShipConnector>())
            AddPairIfCompatible(localConnector, targetConnector, radius, connectorPairs);
    }

    private void AddPairIfCompatible(MyShipConnector localConnector, MyShipConnector targetConnector, float radius, List<DockingPair> connectorPairs)
    {
        if (!DockingMath.IsConnectorAvailable(targetConnector, localSide: false))
            return;

        if (localConnector == targetConnector || localConnector.CubeGrid == targetConnector.CubeGrid)
            return;

        PairKey key = new PairKey(localConnector.EntityId, targetConnector.EntityId);
        if (!pairKeys.Add(key))
            return;

        Vector3D localPosition = localConnector.PositionComp.GetPosition();
        Vector3D targetPosition = targetConnector.PositionComp.GetPosition();
        double distance = Vector3D.Distance(localPosition, targetPosition);
        if (distance > radius)
            return;

        bool lockReady = DockingMath.IsLockReady(localConnector, targetConnector);
        if (!lockReady && !DockingMath.AreFacing(localConnector, targetConnector, localPosition, targetPosition))
            return;

        connectorPairs.Add(new DockingPair(
            localConnector,
            targetConnector,
            distance,
            lockReady,
            savedAlignmentService.HasSavedAlignment(localConnector.EntityId, targetConnector.EntityId)));
    }

    private void SelectConnectorByIndex(int connectorIndex, int fallbackPairIndex, long preferredTargetId = 0)
    {
        pairs.Clear();

        if (connectorIndex < 0 || connectorIndex >= localConnectors.Count)
        {
            selectedConnectorIndex = -1;
            selectedIndex = -1;
            return;
        }

        selectedConnectorIndex = connectorIndex;
        MyShipConnector localConnector = localConnectors[connectorIndex];
        preferredLocalConnectorEntityId = localConnector.EntityId;

        if (!pairsByLocalConnectorId.TryGetValue(localConnector.EntityId, out List<DockingPair> connectorPairs)
            || connectorPairs.Count == 0)
        {
            selectedIndex = -1;
            return;
        }

        pairs.AddRange(connectorPairs);
        selectedIndex = preferredTargetId == 0 ? -1 : FindPair(localConnector.EntityId, preferredTargetId);
        if (selectedIndex < 0)
        {
            if (fallbackPairIndex < 0)
                fallbackPairIndex = 0;
            if (fallbackPairIndex >= pairs.Count)
                fallbackPairIndex = pairs.Count - 1;

            selectedIndex = fallbackPairIndex;
        }
    }

    private int FindLocalConnectorIndex(long localEntityId)
    {
        if (localEntityId == 0)
            return -1;

        for (int i = 0; i < localConnectors.Count; i++)
        {
            if (localConnectors[i].EntityId == localEntityId)
                return i;
        }

        return -1;
    }

    private int FindPair(long localEntityId, long targetEntityId)
    {
        if (localEntityId == 0 || targetEntityId == 0)
            return -1;

        for (int i = 0; i < pairs.Count; i++)
        {
            DockingPair pair = pairs[i];
            if (pair.Local.EntityId == localEntityId && pair.Target.EntityId == targetEntityId)
                return i;
        }

        return -1;
    }

    private static int CompareLocalConnectors(MyShipConnector x, MyShipConnector y)
    {
        long leftId = x?.EntityId ?? 0L;
        long rightId = y?.EntityId ?? 0L;
        return leftId.CompareTo(rightId);
    }

    private static int ComparePairs(DockingPair x, DockingPair y)
    {
        int lockReadyComparison = y.LockReady.CompareTo(x.LockReady);
        if (lockReadyComparison != 0)
            return lockReadyComparison;

        int savedAlignmentComparison = y.HasSavedAlignment.CompareTo(x.HasSavedAlignment);
        if (savedAlignmentComparison != 0)
            return savedAlignmentComparison;

        return x.Distance.CompareTo(y.Distance);
    }
}
