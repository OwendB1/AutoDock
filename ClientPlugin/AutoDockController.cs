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
    private const float SoftSelectRadius = 100f;
    private const float MinSearchRadius = 1f;
    private const float MaxSearchRadius = 30f;
    private const double MinConnectorDistanceSquared = 0.0001;
    private const double AutoDockFinishDistance = 0.12;
    private const double AutoDockFinishAngle = 0.01;
    private const int AutoDockTimeoutFrames = 60 * 15;
    private const double AutoDockApproachDistance = 4.0;
    private const double AutoDockFinalLateralDistance = 1.35;
    private const double AutoDockFinalForwardDistance = 1.1;
    private const double AutoDockInputDeadzone = 0.12;
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
    private DockingPair autoDockingPair;
    private int autoDockFrames;
    private bool autoDockConnectRequested;
    private bool autoDockWaitingForLockNotified;

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
            if (autoDockingPair != null)
                CancelAutoDock("AutoDock: automatic docking cancelled.", "White");
            else
                HandleActivationPressed();
            return;
        }

        if (autoDockingPair != null)
        {
            UpdateAutoDock();
            DrawPairs();
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
                Notify($"AutoDock: no connector pairs within {SoftSelectRadius:0.#} m.", "Red");
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
            Notify($"AutoDock: no connector pairs within {SoftSelectRadius:0.#} m.", "Red");
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
        if (!pair.InRange)
        {
            Notify($"AutoDock: selected pair is {pair.Distance:0.0} m away. Move within {GetSearchRadius():0.#} m.", "Red");
            return;
        }

        if (pair.LockReady)
        {
            ((IMyShipConnector)pair.Local).Connect();
            Notify("AutoDock: connector lock requested.", "Green");
            ClearSelection();
        }
        else
        {
            StartAutoDock(pair);
        }
    }

    private void StartAutoDock(DockingPair pair)
    {
        autoDockingPair = pair;
        autoDockFrames = 0;
        autoDockConnectRequested = false;
        autoDockWaitingForLockNotified = false;
        active = true;
        Notify("AutoDock: moving selected grid into docking position.", "White");
    }

    private void UpdateAutoDock()
    {
        DockingPair pair = autoDockingPair;
        if (pair == null)
            return;

        if (HasManualFlightInput())
        {
            CancelAutoDock("AutoDock: cancelled by manual flight input.", "White");
            return;
        }

        if (IsDocked(pair))
        {
            Notify("AutoDock: docked.", "Green");
            ClearSelection();
            return;
        }

        if (!IsPairStillUsable(pair))
        {
            CancelAutoDock("AutoDock: connector pair became unavailable.", "Red");
            return;
        }

        pair.RefreshMetrics();
        autoDockFrames++;
        if (autoDockFrames > AutoDockTimeoutFrames)
        {
            CancelAutoDock("AutoDock: automatic docking timed out.", "Red");
            return;
        }

        if (pair.LockReady)
        {
            RequestDockLock(pair);
            return;
        }

        if (!TryCreateDockingTarget(pair, out DockingTarget target))
        {
            CancelAutoDock("AutoDock: cannot calculate docking position.", "Red");
            return;
        }

        if (!TryGetActiveShipController(pair.Local.CubeGrid, out MyShipController controller))
        {
            CancelAutoDock("AutoDock: no active ship controller on selected grid.", "Red");
            return;
        }

        ApplyAutoDockControl(pair, controller, target);

        if (target.DockingReady)
        {
            pair.RefreshMetrics();
            if (pair.LockReady)
            {
                RequestDockLock(pair);
            }
            else if (!autoDockWaitingForLockNotified)
            {
                autoDockWaitingForLockNotified = true;
                Notify("AutoDock: aligned. Waiting for connector lock range.", "White");
            }
        }
    }

    private void RequestDockLock(DockingPair pair)
    {
        if (!autoDockConnectRequested)
        {
            autoDockConnectRequested = true;
            ReleaseShipControl(pair.Local.CubeGrid);
            ((IMyShipConnector)pair.Local).Connect();
            Notify("AutoDock: connector lock requested.", "Green");
        }
    }

    private void CancelAutoDock(string message, string font)
    {
        ReleaseShipControl(autoDockingPair?.Local?.CubeGrid);
        autoDockingPair = null;
        autoDockFrames = 0;
        autoDockConnectRequested = false;
        autoDockWaitingForLockNotified = false;
        Notify(message, font);
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

        float radius = SoftSelectRadius;
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

    private static bool IsDocked(DockingPair pair)
    {
        return pair?.Local != null
               && pair.Target != null
               && pair.Local.Connected
               && pair.Local.Other == pair.Target;
    }

    private static bool HasManualFlightInput()
    {
        if (MyInput.Static == null)
            return false;

        Vector3 moveInput = MyInput.Static.GetPositionDelta();
        if (moveInput.LengthSquared() > AutoDockInputDeadzone * AutoDockInputDeadzone)
            return true;

        Vector2 rotationInput = MyInput.Static.GetRotation();
        if (rotationInput.LengthSquared() > AutoDockInputDeadzone * AutoDockInputDeadzone)
            return true;

        return Math.Abs(MyInput.Static.GetRoll()) > AutoDockInputDeadzone;
    }

    private static bool IsPairStillUsable(DockingPair pair)
    {
        return pair?.Local != null
               && pair.Target != null
               && IsConnectorAvailable(pair.Local, localSide: true)
               && IsConnectorAvailable(pair.Target, localSide: false)
               && pair.Local.CubeGrid != null
               && pair.Local.CubeGrid.Physics != null
               && pair.Target.CubeGrid != null
               && pair.Target.CubeGrid.Physics != null
               && !pair.Local.CubeGrid.IsStatic;
    }

    private static bool TryGetActiveShipController(MyCubeGrid grid, out MyShipController controller)
    {
        controller = MySession.Static?.ControlledEntity?.Entity as MyShipController;
        if (controller != null && controller.CubeGrid == grid && !controller.MarkedForClose && controller.IsWorking)
            return true;

        controller = null;
        foreach (MyShipController candidate in grid.GetFatBlocks<MyShipController>())
        {
            if (candidate != null && !candidate.MarkedForClose && candidate.IsWorking)
            {
                controller = candidate;
                return true;
            }
        }

        return false;
    }

    private static bool TryCreateDockingTarget(DockingPair pair, out DockingTarget target)
    {
        target = null;
        MyCubeGrid grid = pair.Local.CubeGrid;
        MatrixD currentGridMatrix = grid.PositionComp.WorldMatrixRef;
        Vector3D currentConstraintPosition = pair.Local.ConstraintPositionWorld();
        Vector3D targetConstraintPosition = pair.Target.ConstraintPositionWorld();
        Vector3D targetForward = pair.Target.WorldMatrix.Forward;
        if (targetForward.LengthSquared() < MinConnectorDistanceSquared)
            return false;

        Vector3D desiredConstraintPosition = GetDesiredConstraintPosition(currentConstraintPosition, targetConstraintPosition, targetForward);
        if (!TryCreateDockingWorldMatrix(pair, desiredConstraintPosition, out MatrixD targetMatrix))
            return false;

        bool finalApproach = Vector3D.DistanceSquared(desiredConstraintPosition, targetConstraintPosition) <= AutoDockFinishDistance * AutoDockFinishDistance;
        bool positionReady = Vector3D.DistanceSquared(currentConstraintPosition, targetConstraintPosition) <= AutoDockFinishDistance * AutoDockFinishDistance;
        bool angleReady = GetOrientationErrorVector(currentGridMatrix, targetMatrix).LengthSquared() <= AutoDockFinishAngle * AutoDockFinishAngle;
        target = new DockingTarget(targetMatrix, desiredConstraintPosition, finalApproach, finalApproach && positionReady && angleReady);
        return true;
    }

    private static Vector3D GetDesiredConstraintPosition(Vector3D currentConstraintPosition, Vector3D targetConstraintPosition, Vector3D targetForward)
    {
        Vector3D forward = Vector3D.Normalize(targetForward);
        Vector3D offset = currentConstraintPosition - targetConstraintPosition;
        double forwardDistance = Vector3D.Dot(offset, forward);
        Vector3D lateral = offset - forward * forwardDistance;

        if (lateral.LengthSquared() > AutoDockFinalLateralDistance * AutoDockFinalLateralDistance || forwardDistance < AutoDockFinalForwardDistance)
            return targetConstraintPosition + forward * AutoDockApproachDistance;

        return targetConstraintPosition;
    }

    private static bool TryCreateDockingWorldMatrix(DockingPair pair, Vector3D desiredConstraintPosition, out MatrixD targetMatrix)
    {
        targetMatrix = MatrixD.Identity;
        MyCubeGrid grid = pair.Local.CubeGrid;
        MatrixD currentGridMatrix = grid.PositionComp.WorldMatrixRef;

        if (!TryGetGridRotationTarget(pair, out MatrixD targetOrientation))
            return false;

        Vector3D localConstraintPosition = Vector3D.Transform(pair.Local.ConstraintPositionWorld(), MatrixD.Invert(currentGridMatrix));
        Vector3D transformedConstraintPosition = Vector3D.Transform(localConstraintPosition, targetOrientation);
        targetMatrix = targetOrientation;
        targetMatrix.Translation = desiredConstraintPosition - transformedConstraintPosition;

        return targetMatrix.IsValid();
    }

    private static bool TryGetGridRotationTarget(DockingPair pair, out MatrixD targetOrientation)
    {
        targetOrientation = MatrixD.Identity;
        MyCubeGrid grid = pair.Local.CubeGrid;
        MatrixD currentGridMatrix = grid.PositionComp.WorldMatrixRef;
        MatrixD inverseGridMatrix = MatrixD.Invert(currentGridMatrix);
        Vector3D desiredConnectorForward = -Vector3D.Normalize(pair.Target.WorldMatrix.Forward);
        Vector3D localConnectorForward = Vector3D.TransformNormal(pair.Local.WorldMatrix.Forward, inverseGridMatrix);
        Vector3D localRollReference = Vector3D.TransformNormal(GetReferenceControllerUp(grid), inverseGridMatrix);
        if (localConnectorForward.LengthSquared() < MinConnectorDistanceSquared || localRollReference.LengthSquared() < MinConnectorDistanceSquared)
            return false;

        localConnectorForward.Normalize();
        localRollReference.Normalize();

        if (RejectFromAxis(localRollReference, localConnectorForward).LengthSquared() < MinConnectorDistanceSquared)
        {
            localRollReference = Vector3D.TransformNormal(pair.Local.WorldMatrix.Up, inverseGridMatrix);
            if (localRollReference.LengthSquared() < MinConnectorDistanceSquared)
                return false;

            localRollReference.Normalize();
        }

        QuaternionD combinedRotation = CreateRotationBetweenVectors(localConnectorForward, desiredConnectorForward, localRollReference);
        Vector3D desiredRollReference = GetDesiredRollReference(pair.Target, grid);
        Vector3D currentProjected = RejectFromAxis(combinedRotation * localRollReference, desiredConnectorForward);
        Vector3D desiredProjected = RejectFromAxis(desiredRollReference, desiredConnectorForward);
        if (currentProjected.LengthSquared() > MinConnectorDistanceSquared && desiredProjected.LengthSquared() > MinConnectorDistanceSquared)
        {
            currentProjected.Normalize();
            desiredProjected.Normalize();
            double signedAngle = SignedAngleAroundAxis(currentProjected, desiredProjected, desiredConnectorForward);
            QuaternionD rollAlign = QuaternionD.CreateFromAxisAngle(desiredConnectorForward, signedAngle);
            combinedRotation = QuaternionD.Normalize(rollAlign * combinedRotation);
        }

        targetOrientation = MatrixD.CreateFromQuaternion(combinedRotation);
        return targetOrientation.IsValid();
    }

    private static Vector3D GetReferenceControllerUp(MyCubeGrid grid)
    {
        return TryGetActiveShipController(grid, out MyShipController controller)
            ? controller.WorldMatrix.Up
            : grid.WorldMatrix.Up;
    }

    private static Vector3D GetGravityVector(MyCubeGrid grid)
    {
        if (TryGetActiveShipController(grid, out MyShipController controller))
            return controller.GetNaturalGravity();

        return Vector3D.Zero;
    }

    private static Vector3D GetDesiredRollReference(MyShipConnector targetConnector, MyCubeGrid localGrid)
    {
        Vector3D gravity = GetGravityVector(localGrid);
        if (gravity.LengthSquared() > MinConnectorDistanceSquared)
            return -Vector3D.Normalize(gravity);

        Vector3D connectorUp = targetConnector.WorldMatrix.Up;
        if (connectorUp.LengthSquared() > MinConnectorDistanceSquared)
            return Vector3D.Normalize(connectorUp);

        return Vector3D.Up;
    }

    private static QuaternionD CreateRotationBetweenVectors(Vector3D from, Vector3D to, Vector3D fallbackAxis)
    {
        Vector3D normalizedFrom = Vector3D.Normalize(from);
        Vector3D normalizedTo = Vector3D.Normalize(to);
        double dot = MathHelper.Clamp(Vector3D.Dot(normalizedFrom, normalizedTo), -1.0, 1.0);
        if (dot >= 0.999999)
            return QuaternionD.Identity;

        if (dot <= -0.999999)
        {
            Vector3D axis = RejectFromAxis(fallbackAxis, normalizedFrom);
            if (axis.LengthSquared() < MinConnectorDistanceSquared)
                axis = RejectFromAxis(Vector3D.Up, normalizedFrom);
            if (axis.LengthSquared() < MinConnectorDistanceSquared)
                axis = RejectFromAxis(Vector3D.Right, normalizedFrom);
            axis.Normalize();
            return QuaternionD.CreateFromAxisAngle(axis, Math.PI);
        }

        Vector3D rotationAxis = Vector3D.Cross(normalizedFrom, normalizedTo);
        rotationAxis.Normalize();
        return QuaternionD.CreateFromAxisAngle(rotationAxis, Math.Acos(dot));
    }

    private static Vector3D RejectFromAxis(Vector3D vector, Vector3D axis)
    {
        return vector - axis * Vector3D.Dot(vector, axis);
    }

    private static double SignedAngleAroundAxis(Vector3D from, Vector3D to, Vector3D axis)
    {
        Vector3D cross = Vector3D.Cross(from, to);
        double sine = Vector3D.Dot(cross, axis);
        double cosine = MathHelper.Clamp(Vector3D.Dot(from, to), -1.0, 1.0);
        return Math.Atan2(sine, cosine);
    }

    private static void ApplyAutoDockControl(DockingPair pair, MyShipController controller, DockingTarget target)
    {
        MatrixD controllerMatrix = controller.WorldMatrix;
        Vector3D currentConstraintPosition = pair.Local.ConstraintPositionWorld();
        Vector3D targetVelocity = pair.Target.CubeGrid.Physics.LinearVelocity;
        Vector3D currentVelocity = pair.Local.CubeGrid.Physics.LinearVelocity;
        Vector3D relativeVelocity = currentVelocity - targetVelocity;

        Vector3D positionError = target.ConstraintPosition - currentConstraintPosition;
        Vector3 localPositionError = new Vector3(
            (float)Vector3D.Dot(positionError, controllerMatrix.Right),
            (float)Vector3D.Dot(positionError, controllerMatrix.Up),
            (float)Vector3D.Dot(positionError, controllerMatrix.Forward));
        Vector3 localVelocity = new Vector3(
            (float)Vector3D.Dot(relativeVelocity, controllerMatrix.Right),
            (float)Vector3D.Dot(relativeVelocity, controllerMatrix.Up),
            (float)Vector3D.Dot(relativeVelocity, controllerMatrix.Forward));

        float maxSpeed = target.FinalApproach ? 1.25f : 6f;
        float maxCommand = target.FinalApproach ? 0.35f : 1f;
        Vector3 desiredLocalVelocity = Vector3.Clamp(localPositionError * 0.65f, new Vector3(-maxSpeed), new Vector3(maxSpeed));
        Vector3 moveIndicator = Vector3.Clamp((desiredLocalVelocity - localVelocity) * 0.3f, new Vector3(-maxCommand), new Vector3(maxCommand));

        MatrixD desiredControllerMatrix = GetDesiredControllerMatrix(controller, pair.Local.CubeGrid, target.WorldMatrix);
        Vector3D orientationError = GetOrientationErrorVector(controllerMatrix, desiredControllerMatrix);
        Vector3D angularVelocity = pair.Local.CubeGrid.Physics.AngularVelocity;
        Vector3D worldTorque = orientationError * 3.0 - angularVelocity * 0.35;
        Vector3 localTorque = new Vector3(
            (float)Vector3D.Dot(worldTorque, controllerMatrix.Right),
            (float)Vector3D.Dot(worldTorque, controllerMatrix.Up),
            (float)Vector3D.Dot(worldTorque, controllerMatrix.Forward));

        Vector2 rotationIndicator = new Vector2(
            MathHelper.Clamp(-localTorque.X * 20f, -6f, 6f),
            MathHelper.Clamp(-localTorque.Y * 20f, -6f, 6f));
        float rollIndicator = MathHelper.Clamp(-localTorque.Z / 0.2f, -5f, 5f);

        controller.MoveAndRotate(moveIndicator, rotationIndicator, rollIndicator);
    }

    private static MatrixD GetDesiredControllerMatrix(MyShipController controller, MyCubeGrid grid, MatrixD targetGridMatrix)
    {
        MatrixD controllerToGrid = controller.WorldMatrix * MatrixD.Invert(grid.PositionComp.WorldMatrixRef);
        return controllerToGrid * targetGridMatrix;
    }

    private static Vector3D GetOrientationErrorVector(MatrixD currentMatrix, MatrixD targetMatrix)
    {
        return Vector3D.Cross(currentMatrix.Forward, targetMatrix.Forward)
               + Vector3D.Cross(currentMatrix.Up, targetMatrix.Up)
               + Vector3D.Cross(currentMatrix.Right, targetMatrix.Right);
    }

    private static void ReleaseShipControl(MyCubeGrid grid)
    {
        if (grid == null)
            return;

        if (TryGetActiveShipController(grid, out MyShipController controller))
            controller.MoveAndRotateStopped();
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
            return selected ? new Color(0.45f, 0.45f, 0.45f, 0.55f) : new Color(0.45f, 0.45f, 0.45f, 0.28f);

        return selected ? new Color(0f, 0.7f, 1f, 0.6f) : new Color(0f, 0.7f, 1f, 0.32f);
    }

    private static Color GetLineColor(DockingPair pair, bool selected)
    {
        if (!pair.InRange)
            return selected ? new Color(1f, 0.8f, 0f, 0.75f) : new Color(1f, 0.8f, 0f, 0.4f);

        return selected ? new Color(0f, 1f, 0.25f, 0.75f) : new Color(0f, 1f, 0.25f, 0.4f);
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
        string state = pair.LockReady ? "lock ready" : pair.InRange ? "in range" : $"outside {GetSearchRadius():0.#} m range";
        Notify($"AutoDock: pair {selectedIndex + 1}/{pairs.Count}, {pair.Distance:0.0} m, {state}.", pair.InRange ? "White" : "Red");
    }

    private void ClearSelection()
    {
        ReleaseShipControl(autoDockingPair?.Local?.CubeGrid);
        active = false;
        selectedIndex = -1;
        pairs.Clear();
        pairKeys.Clear();
        localConnectors.Clear();
        framesUntilRescan = 0;
        autoDockingPair = null;
        autoDockFrames = 0;
        autoDockConnectRequested = false;
        autoDockWaitingForLockNotified = false;
    }

    private static float GetSearchRadius()
    {
        float radius = Config.Current.ConnectorSearchRadius;
        if (float.IsNaN(radius) || float.IsInfinity(radius))
            radius = 5f;

        return MathHelper.Clamp(radius, MinSearchRadius, MaxSearchRadius);
    }

    private static bool IsWithinSearchRange(double distance)
    {
        return distance <= GetSearchRadius();
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

    private sealed class DockingTarget
    {
        public readonly MatrixD WorldMatrix;
        public readonly Vector3D ConstraintPosition;
        public readonly bool FinalApproach;
        public readonly bool DockingReady;

        public DockingTarget(MatrixD worldMatrix, Vector3D constraintPosition, bool finalApproach, bool dockingReady)
        {
            WorldMatrix = worldMatrix;
            ConstraintPosition = constraintPosition;
            FinalApproach = finalApproach;
            DockingReady = dockingReady;
        }
    }

    private sealed class DockingPair
    {
        public readonly MyShipConnector Local;
        public readonly MyShipConnector Target;
        public double Distance { get; private set; }
        public bool InRange { get; private set; }
        public bool LockReady { get; private set; }

        public DockingPair(MyShipConnector local, MyShipConnector target, double distance, bool lockReady)
        {
            Local = local;
            Target = target;
            Distance = distance;
            InRange = IsWithinSearchRange(distance);
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
            InRange = IsWithinSearchRange(Distance);
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
