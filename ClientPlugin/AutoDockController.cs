using System;
using System.Collections.Generic;
using ClientPlugin.Settings.Tools;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.Gui;
using Sandbox.Game.World;
using Sandbox.Graphics.GUI;
using Sandbox.ModAPI.Ingame;
using VRage.Game;
using VRage.Game.Entity;
using VRage.Input;
using VRage.Utils;
using VRageMath;
using VRageRender;

namespace ClientPlugin;

internal sealed class AutoDockController
{
    private const int RescanIntervalFrames = 10;
    private const float MinSearchRadius = 1f;
    private const float MaxSearchRadius = 30f;
    private const double MinConnectorDistanceSquared = 0.0001;
    private static readonly MyStringId ActivationControlId = MyStringId.GetOrCompute("AutoDock.ActivationKeybind");
    private static readonly MyStringId PreviousPairControlId = MyStringId.GetOrCompute("AutoDock.PreviousPairKeybind");
    private static readonly MyStringId NextPairControlId = MyStringId.GetOrCompute("AutoDock.NextPairKeybind");

    private readonly List<DockingPair> pairs = new List<DockingPair>();
    private readonly List<MyShipConnector> localConnectors = new List<MyShipConnector>();
    private readonly HashSet<PairKey> pairKeys = new HashSet<PairKey>();

    private Binding registeredActivationKeybind = new Binding(MyKeys.None);
    private Binding registeredPreviousPairKeybind = new Binding(MyKeys.None);
    private Binding registeredNextPairKeybind = new Binding(MyKeys.None);
    private MyControl activationControl;
    private MyControl previousPairControl;
    private MyControl nextPairControl;
    private bool active;
    private int selectedIndex = -1;
    private int framesUntilRescan;

    public void Update()
    {
        if (MySession.Static == null || MyInput.Static == null)
        {
            ClearSelection();
            return;
        }

        IMyInput input = MyInput.Static;
        EnsureGameControls(input);

        MyGuiScreenBase screenWithFocus = MyScreenManager.GetScreenWithFocus();
        if (screenWithFocus != null && screenWithFocus != MyGuiScreenGamePlay.Static)
        {
            DrawPairs();
            return;
        }

        if (IsActivationPressed(input))
        {
            HandleActivationPressed();
            return;
        }

        if (!active)
            return;

        framesUntilRescan--;
        if (framesUntilRescan <= 0)
        {
            RescanPairs(preserveSelection: true);
            framesUntilRescan = RescanIntervalFrames;

            if (pairs.Count == 0)
            {
                ClearSelection();
                Notify("AutoDock: no connector pairs in range.", "Red");
                return;
            }
        }

        if (IsCyclePreviousPressed(input))
            SelectRelative(-1);
        else if (IsCycleNextPressed(input))
            SelectRelative(1);

        DrawPairs();
    }

    public void Dispose()
    {
        ClearSelection();
        UnregisterGameControls();
    }

    private void HandleActivationPressed()
    {
        RescanPairs(preserveSelection: active);
        framesUntilRescan = RescanIntervalFrames;

        if (pairs.Count == 0)
        {
            ClearSelection();
            Notify($"AutoDock: no connector pairs within {GetSearchRadius():0.#} m.", "Red");
            return;
        }

        if (!active)
        {
            active = true;
            if (selectedIndex < 0)
                selectedIndex = 0;
            NotifySelection();
            return;
        }

        if (!TryGetSelectedPair(out DockingPair pair))
        {
            selectedIndex = 0;
            NotifySelection();
            return;
        }

        pair.RefreshMetrics();
        if (!pair.LockReady)
        {
            Notify("AutoDock: selected pair is not in connector lock range.", "Red");
            return;
        }

        ((IMyShipConnector)pair.Local).Connect();
        Notify("AutoDock: connector lock requested.", "Green");
        ClearSelection();
    }

    private void RescanPairs(bool preserveSelection)
    {
        long previousLocalId = 0;
        long previousTargetId = 0;
        int previousIndex = selectedIndex < 0 ? 0 : selectedIndex;

        if (preserveSelection && TryGetSelectedPair(out DockingPair selectedPair))
        {
            previousLocalId = selectedPair.Local.EntityId;
            previousTargetId = selectedPair.Target.EntityId;
        }

        pairs.Clear();
        pairKeys.Clear();
        localConnectors.Clear();

        MyCubeGrid controlledGrid = MySession.Static.ControlledGrid;
        if (controlledGrid == null || controlledGrid.MarkedForClose)
        {
            selectedIndex = -1;
            return;
        }

        foreach (MyShipConnector connector in controlledGrid.GetFatBlocks<MyShipConnector>())
        {
            if (IsConnectorAvailable(connector, localSide: true))
                localConnectors.Add(connector);
        }

        float radius = GetSearchRadius();
        foreach (MyShipConnector localConnector in localConnectors)
            AddPairsNear(localConnector, radius);

        pairs.Sort(ComparePairs);

        if (pairs.Count == 0)
        {
            selectedIndex = -1;
            return;
        }

        selectedIndex = FindPair(previousLocalId, previousTargetId);
        if (selectedIndex < 0)
            selectedIndex = Math.Min(previousIndex, pairs.Count - 1);
    }

    private void AddPairsNear(MyShipConnector localConnector, float radius)
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
                    AddPairIfCompatible(localConnector, targetConnector, radius);
                    continue;
                }

                if (entity is MyCubeGrid targetGrid)
                    AddGridPairs(localConnector, targetGrid, radius);
            }
        }
        finally
        {
            entities.Clear();
        }
    }

    private void AddGridPairs(MyShipConnector localConnector, MyCubeGrid targetGrid, float radius)
    {
        if (targetGrid == null || targetGrid.MarkedForClose || targetGrid == localConnector.CubeGrid)
            return;

        foreach (MyShipConnector targetConnector in targetGrid.GetFatBlocks<MyShipConnector>())
            AddPairIfCompatible(localConnector, targetConnector, radius);
    }

    private void AddPairIfCompatible(MyShipConnector localConnector, MyShipConnector targetConnector, float radius)
    {
        if (!IsConnectorAvailable(targetConnector, localSide: false))
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

        bool lockReady = IsLockReady(localConnector, targetConnector);
        if (!lockReady && !AreFacing(localConnector, targetConnector, localPosition, targetPosition))
            return;

        pairs.Add(new DockingPair(localConnector, targetConnector, distance, lockReady));
    }

    private static bool IsConnectorAvailable(MyShipConnector connector, bool localSide)
    {
        if (connector == null || connector.MarkedForClose || connector.CubeGrid == null || connector.CubeGrid.MarkedForClose)
            return false;

        if (!connector.IsWorking || connector.Connected)
            return false;

        return !localSide || connector.HasLocalPlayerAccess();
    }

    private static bool AreFacing(MyShipConnector localConnector, MyShipConnector targetConnector, Vector3D localPosition, Vector3D targetPosition)
    {
        Vector3D delta = targetPosition - localPosition;
        if (delta.LengthSquared() < MinConnectorDistanceSquared)
            return false;

        Vector3D direction = Vector3D.Normalize(delta);
        double forwardOpposition = Vector3D.Dot(localConnector.WorldMatrix.Forward, targetConnector.WorldMatrix.Forward);
        double localAim = Vector3D.Dot(localConnector.WorldMatrix.Forward, direction);
        double targetAim = Vector3D.Dot(targetConnector.WorldMatrix.Forward, -direction);

        return forwardOpposition <= -0.65 && localAim >= 0.15 && targetAim >= 0.15;
    }

    private static bool IsLockReady(MyShipConnector localConnector, MyShipConnector targetConnector)
    {
        return localConnector != null
               && targetConnector != null
               && localConnector.InConstraint
               && localConnector.Other == targetConnector
               && !localConnector.Connected;
    }

    private void DrawPairs()
    {
        if (!active || pairs.Count == 0)
            return;

        for (int i = 0; i < pairs.Count; i++)
        {
            DockingPair pair = pairs[i];
            pair.RefreshMetrics();
            bool selected = i == selectedIndex;
            Color color = GetPairColor(pair, selected);

            DrawConnectorBox(pair.Local, color, selected);
            DrawConnectorBox(pair.Target, color, selected);
            DrawConnectionLine(pair, color, selected);
        }
    }

    private static Color GetPairColor(DockingPair pair, bool selected)
    {
        if (selected && pair.LockReady)
            return new Color(0f, 1f, 0.25f, 0.65f);

        if (selected)
            return new Color(0f, 0.7f, 1f, 0.55f);

        return new Color(1f, 0.75f, 0f, 0.25f);
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

    private void SelectRelative(int offset)
    {
        if (pairs.Count < 2)
            return;

        selectedIndex = (selectedIndex + offset) % pairs.Count;
        if (selectedIndex < 0)
            selectedIndex += pairs.Count;

        NotifySelection();
    }

    private bool TryGetSelectedPair(out DockingPair pair)
    {
        if (selectedIndex >= 0 && selectedIndex < pairs.Count)
        {
            pair = pairs[selectedIndex];
            return true;
        }

        pair = null;
        return false;
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

    private void NotifySelection()
    {
        if (!TryGetSelectedPair(out DockingPair pair))
            return;

        pair.RefreshMetrics();
        string state = pair.LockReady ? "lock ready" : "aligned";
        Notify($"AutoDock: pair {selectedIndex + 1}/{pairs.Count}, {pair.Distance:0.0} m, {state}.", pair.LockReady ? "Green" : "White");
    }

    private void ClearSelection()
    {
        active = false;
        selectedIndex = -1;
        pairs.Clear();
        pairKeys.Clear();
        localConnectors.Clear();
        framesUntilRescan = 0;
    }

    private static float GetSearchRadius()
    {
        float radius = Config.Current.ConnectorSearchRadius;
        if (float.IsNaN(radius) || float.IsInfinity(radius))
            radius = 5f;

        return MathHelper.Clamp(radius, MinSearchRadius, MaxSearchRadius);
    }

    private void EnsureGameControls(IMyInput input)
    {
        bool changed = false;
        changed |= EnsureGameControl(
            input,
            ActivationControlId,
            "AutoDock Activate Docking",
            Config.Current.ActivationKeybind,
            ref registeredActivationKeybind,
            ref activationControl);
        changed |= EnsureGameControl(
            input,
            PreviousPairControlId,
            "AutoDock Previous Pair",
            Config.Current.PreviousPairKeybind,
            ref registeredPreviousPairKeybind,
            ref previousPairControl);
        changed |= EnsureGameControl(
            input,
            NextPairControlId,
            "AutoDock Next Pair",
            Config.Current.NextPairKeybind,
            ref registeredNextPairKeybind,
            ref nextPairControl);

        if (changed)
            input.CreateKeyControlsPriorityMap();
    }

    private void UnregisterGameControls()
    {
        IMyInput input = MyInput.Static;
        if (input == null)
            return;

        var unbound = new Binding(MyKeys.None);
        bool changed = false;
        changed |= EnsureGameControl(
            input,
            ActivationControlId,
            "AutoDock Activate Docking",
            unbound,
            ref registeredActivationKeybind,
            ref activationControl);
        changed |= EnsureGameControl(
            input,
            PreviousPairControlId,
            "AutoDock Previous Pair",
            unbound,
            ref registeredPreviousPairKeybind,
            ref previousPairControl);
        changed |= EnsureGameControl(
            input,
            NextPairControlId,
            "AutoDock Next Pair",
            unbound,
            ref registeredNextPairKeybind,
            ref nextPairControl);

        if (changed)
            input.CreateKeyControlsPriorityMap();
    }

    private static bool EnsureGameControl(
        IMyInput input,
        MyStringId controlId,
        string displayName,
        Binding binding,
        ref Binding registeredBinding,
        ref MyControl control)
    {
        if (control != null && BindingsEqual(registeredBinding, binding))
            return false;

        control = new MyControl(
            controlId,
            MyStringId.GetOrCompute(displayName),
            MyGuiControlTypeEnum.General,
            null,
            binding.Key,
            keyModifiers: binding.ToKeyboardModifiers());

        input.AddDefaultControl(controlId, control);
        registeredBinding = binding;
        return true;
    }

    private bool IsActivationPressed(IMyInput input)
    {
        return IsControlNewPressed(activationControl, Config.Current.ActivationKeybind, input);
    }

    private bool IsCyclePreviousPressed(IMyInput input)
    {
        return IsControlNewPressed(previousPairControl, Config.Current.PreviousPairKeybind, input);
    }

    private bool IsCycleNextPressed(IMyInput input)
    {
        return IsControlNewPressed(nextPairControl, Config.Current.NextPairKeybind, input);
    }

    private static bool IsControlNewPressed(MyControl control, Binding binding, IMyInput input)
    {
        return control != null ? control.IsNewPressed() : binding.HasPressed(input);
    }

    private static bool BindingsEqual(Binding left, Binding right)
    {
        return left.Key == right.Key
               && left.Ctrl == right.Ctrl
               && left.Alt == right.Alt
               && left.Shift == right.Shift;
    }

    private static int ComparePairs(DockingPair x, DockingPair y)
    {
        int lockReadyComparison = y.LockReady.CompareTo(x.LockReady);
        if (lockReadyComparison != 0)
            return lockReadyComparison;

        return x.Distance.CompareTo(y.Distance);
    }

    private static void Notify(string message, string font)
    {
        MyHudNotification notification = new MyHudNotification(MyCommonTexts.CustomText, 2500, font);
        notification.SetTextFormatArguments(message);
        MyHud.Notifications?.Add(notification);
    }

    private sealed class DockingPair
    {
        public readonly MyShipConnector Local;
        public readonly MyShipConnector Target;
        public double Distance { get; private set; }
        public bool LockReady { get; private set; }

        public DockingPair(MyShipConnector local, MyShipConnector target, double distance, bool lockReady)
        {
            Local = local;
            Target = target;
            Distance = distance;
            LockReady = lockReady;
        }

        public void RefreshMetrics()
        {
            if (Local == null || Target == null || Local.MarkedForClose || Target.MarkedForClose)
            {
                LockReady = false;
                return;
            }

            Distance = Vector3D.Distance(Local.PositionComp.GetPosition(), Target.PositionComp.GetPosition());
            LockReady = IsLockReady(Local, Target);
        }
    }

    private struct PairKey : IEquatable<PairKey>
    {
        private readonly long localEntityId;
        private readonly long targetEntityId;

        public PairKey(long localEntityId, long targetEntityId)
        {
            this.localEntityId = localEntityId;
            this.targetEntityId = targetEntityId;
        }

        public bool Equals(PairKey other)
        {
            return localEntityId == other.localEntityId && targetEntityId == other.targetEntityId;
        }

        public override bool Equals(object obj)
        {
            return obj is PairKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return (localEntityId.GetHashCode() * 397) ^ targetEntityId.GetHashCode();
            }
        }
    }
}
