using System;
using System.Collections.Generic;
using ClientPlugin.Settings;
using ClientPlugin.Settings.Tools;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.GameSystems;
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
    private const int AutoDockTimeoutFrames = 60 * 15;
    private const double AutoDockPositionTolerance = 0.12;
    private const double AutoDockOrientationTolerance = 0.01;
    private const double AutoDockControlStepSeconds = 1.0 / 60.0;
    private const double AutoDockLinearProportionalGain = 0.75;
    private const double AutoDockLinearIntegralGain = 0.22;
    private const double AutoDockLinearDerivativeGain = 1.35;
    private const double AutoDockLinearIntegralLimit = 6.0;
    private const double AutoDockLinearIntegralActivationDistance = 5.0;
    private const double AutoDockMaxLinearAcceleration = 2.5;
    private const double AutoDockFinalApproachDistance = 2.75;
    private const double AutoDockFinalApproachOrientationThreshold = 0.35;
    private const double AutoDockFinalPlanarProportionalBoost = 1.8;
    private const double AutoDockFinalPlanarIntegralBoost = 0.75;
    private const double AutoDockFinalPlanarDerivativeBoost = 1.15;
    private const double AutoDockFinalPlanarHoldDistance = 0.35;
    private const double AutoDockFinalAxialSuppression = 0.75;
    private const double AutoDockAngularProportionalGain = 2.0;
    private const double AutoDockAngularIntegralGain = 0.08;
    private const double AutoDockAngularIntegralLimit = 0.35;
    private const double AutoDockMaxAngularVelocity = 0.45;
    private const double AutoDockAngularVelocityGain = 1.8;
    private const double AutoDockAngularSofteningThreshold = 0.35;
    private const float AutoDockAngularMinTorque = 0.35f;
    private const float AutoDockAngularMaxTorque = 0.8f;
    private const double ManualInputDeadzone = 0.12;
    private const double MaxGravityTiltWarningRadians = Math.PI / 4.0;
    private static readonly MyStringId ActivationControlId = MyStringId.GetOrCompute("AutoDock.ActivationKeybind");
    private static readonly MyStringId PreviousPairControlId = MyStringId.GetOrCompute("AutoDock.PreviousPairKeybind");
    private static readonly MyStringId NextPairControlId = MyStringId.GetOrCompute("AutoDock.NextPairKeybind");
    private static readonly MyStringId SaveAlignmentControlId = MyStringId.GetOrCompute("AutoDock.SaveAlignmentKeybind");
    private static readonly MyStringId RemoveAlignmentControlId = MyStringId.GetOrCompute("AutoDock.RemoveAlignmentKeybind");

    private readonly List<DockingPair> pairs = new List<DockingPair>();
    private readonly List<MyShipConnector> localConnectors = new List<MyShipConnector>();
    private readonly HashSet<PairKey> pairKeys = new HashSet<PairKey>();

    private Binding registeredActivationKeybind = new Binding(MyKeys.None);
    private Binding registeredPreviousPairKeybind = new Binding(MyKeys.None);
    private Binding registeredNextPairKeybind = new Binding(MyKeys.None);
    private Binding registeredSaveAlignmentKeybind = new Binding(MyKeys.None);
    private Binding registeredRemoveAlignmentKeybind = new Binding(MyKeys.None);
    private MyControl activationControl;
    private MyControl previousPairControl;
    private MyControl nextPairControl;
    private MyControl saveAlignmentControl;
    private MyControl removeAlignmentControl;
    private bool active;
    private int selectedIndex = -1;
    private int framesUntilRescan;
    private DockingPair autoDockingPair;
    private int autoDockFrames;
    private bool autoDockConnectRequested;
    private bool autoDockWaitingForLockNotified;
    private Vector3D autoDockPositionErrorIntegral;
    private Vector3D autoDockOrientationErrorIntegral;
    private SavedAlignmentRemovalScreen removeAlignmentScreen;

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
        if (removeAlignmentScreen != null && screenWithFocus == removeAlignmentScreen)
            removeAlignmentScreen.HandleExternalInput(input);

        if (screenWithFocus != null && screenWithFocus != MyGuiScreenGamePlay.Static)
        {
            DrawPairs();
            return;
        }

        if (IsRemoveAlignmentPressed(input))
        {
            HandleRemoveAlignmentPressed();
            DrawPairs();
            return;
        }

        if (IsSaveAlignmentPressed(input))
        {
            HandleSaveAlignmentPressed();
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
        removeAlignmentScreen?.CloseScreen();
        removeAlignmentScreen = null;
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
            if (TryGetGravityTiltWarning(pair, out string warning))
            {
                Notify(warning, "Red");
                return;
            }

            StartAutoDock(pair);
        }
    }

    private void StartAutoDock(DockingPair pair)
    {
        autoDockingPair = pair;
        autoDockFrames = 0;
        autoDockConnectRequested = false;
        autoDockWaitingForLockNotified = false;
        ResetAutoDockPidState();
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
            Notify("AutoDock: connector lock succeeded.", "Green");
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
            pair.RefreshMetrics();
            if (IsDocked(pair))
            {
                Notify("AutoDock: connector lock succeeded.", "Green");
                ClearSelection();
                return;
            }

            Notify("AutoDock: connector lock requested.", "Green");
        }
    }

    private void CancelAutoDock(string message, string font)
    {
        ReleaseShipControl(autoDockingPair?.Local?.CubeGrid);
        ResetAutoDockPidState();
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
        IMyInput input = MyInput.Static;
        if (input == null)
            return false;

        Vector3 moveInput = input.GetPositionDelta();
        if (moveInput.LengthSquared() > ManualInputDeadzone * ManualInputDeadzone)
            return true;

        if (!input.IsAnyAltKeyPressed())
        {
            Vector2 rotationInput = input.GetRotation();
            if (rotationInput.LengthSquared() > ManualInputDeadzone * ManualInputDeadzone)
                return true;
        }

        return Math.Abs(input.GetRoll()) > ManualInputDeadzone;
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
        if (!TryCreateDockingWorldMatrix(pair, targetConstraintPosition, out MatrixD targetMatrix))
            return false;

        bool positionReady = Vector3D.DistanceSquared(currentConstraintPosition, targetConstraintPosition) <= AutoDockPositionTolerance * AutoDockPositionTolerance;
        bool angleReady = GetOrientationErrorVector(currentGridMatrix, targetMatrix).LengthSquared() <= AutoDockOrientationTolerance * AutoDockOrientationTolerance;
        target = new DockingTarget(targetMatrix, targetConstraintPosition, positionReady && angleReady);
        return true;
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
        if (TryGetSavedGridRotationTarget(pair, out targetOrientation))
            return true;

        return TryGetDefaultGridRotationTarget(pair, out targetOrientation);
    }

    private static bool TryGetSavedGridRotationTarget(DockingPair pair, out MatrixD targetOrientation)
    {
        targetOrientation = MatrixD.Identity;
        if (!TryGetSavedConnectorAlignmentMatrix(pair, out MatrixD relativeConnectorMatrix))
            return false;

        MatrixD targetConnectorWorldMatrix = pair.Target.WorldMatrix;
        targetConnectorWorldMatrix.Translation = Vector3D.Zero;

        MatrixD desiredLocalConnectorWorldMatrix = relativeConnectorMatrix * targetConnectorWorldMatrix;
        desiredLocalConnectorWorldMatrix.Translation = Vector3D.Zero;

        MatrixD currentGridMatrix = pair.Local.CubeGrid.PositionComp.WorldMatrixRef;
        currentGridMatrix.Translation = Vector3D.Zero;
        MatrixD inverseGridMatrix = MatrixD.Invert(currentGridMatrix);

        MatrixD localConnectorWorldMatrix = pair.Local.WorldMatrix;
        localConnectorWorldMatrix.Translation = Vector3D.Zero;
        MatrixD localConnectorGridMatrix = localConnectorWorldMatrix * inverseGridMatrix;
        localConnectorGridMatrix.Translation = Vector3D.Zero;

        targetOrientation = MatrixD.Invert(localConnectorGridMatrix) * desiredLocalConnectorWorldMatrix;
        targetOrientation.Translation = Vector3D.Zero;
        return targetOrientation.IsValid();
    }

    private static bool TryGetDefaultGridRotationTarget(DockingPair pair, out MatrixD targetOrientation)
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

    private static bool TryGetGravityTiltWarning(DockingPair pair, out string warning)
    {
        warning = null;
        if (!TryGetActiveShipController(pair.Local.CubeGrid, out MyShipController controller))
            return false;

        Vector3D gravity = controller.GetNaturalGravity();
        if (gravity.LengthSquared() < MinConnectorDistanceSquared)
            return false;

        if (!TryGetGridRotationTarget(pair, out MatrixD targetOrientation))
            return false;

        MatrixD desiredControllerMatrix = GetDesiredControllerWorldMatrix(controller, targetOrientation);
        Vector3D desiredControllerUp = desiredControllerMatrix.Up;
        if (desiredControllerUp.LengthSquared() < MinConnectorDistanceSquared)
            return false;

        desiredControllerUp.Normalize();
        Vector3D gravityUp = -Vector3D.Normalize(gravity);
        double dot = MathHelper.Clamp(Vector3D.Dot(desiredControllerUp, gravityUp), -1.0, 1.0);
        double tiltAngle = Math.Acos(dot);
        if (tiltAngle <= MaxGravityTiltWarningRadians)
            return false;

        warning = $"AutoDock: docking needs {MathHelper.ToDegrees((float)tiltAngle):0} deg cockpit tilt from gravity. Move ship first.";
        return true;
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

    private static MatrixD GetDesiredControllerWorldMatrix(MyShipController controller, MatrixD targetGridMatrix)
    {
        MatrixD controllerLocalMatrix = controller.PositionComp.LocalMatrixRef;
        return controllerLocalMatrix * targetGridMatrix;
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

    private void ApplyAutoDockControl(DockingPair pair, MyShipController controller, DockingTarget target)
    {
        MyCubeGrid grid = pair.Local.CubeGrid;
        MatrixD currentGridMatrix = grid.PositionComp.WorldMatrixRef;
        MatrixD inverseGridMatrix = grid.PositionComp.WorldMatrixNormalizedInv;
        Vector3D currentConstraintPosition = pair.Local.ConstraintPositionWorld();
        Vector3D targetVelocity = pair.Target.CubeGrid.Physics.LinearVelocity;
        Vector3D currentVelocity = pair.Local.CubeGrid.Physics.LinearVelocity;
        MyEntityThrustComponent thrustComponent = controller.EntityThrustComponent;
        MyGridGyroSystem gyroSystem = grid.GridSystems?.GyroSystem;

        Vector3D positionError = target.ConstraintPosition - currentConstraintPosition;
        Vector3D errorVelocity = targetVelocity - currentVelocity;
        Vector3D orientationError = GetOrientationErrorVector(currentGridMatrix, target.WorldMatrix);
        if (positionError.LengthSquared() <= AutoDockLinearIntegralActivationDistance * AutoDockLinearIntegralActivationDistance)
            autoDockPositionErrorIntegral += positionError * AutoDockControlStepSeconds;
        else
            autoDockPositionErrorIntegral = Vector3D.Zero;

        autoDockPositionErrorIntegral = ClampVectorMagnitude(autoDockPositionErrorIntegral, AutoDockLinearIntegralLimit);

        Vector3D desiredWorldAcceleration = GetDesiredWorldAcceleration(
            pair.Target,
            positionError,
            autoDockPositionErrorIntegral,
            errorVelocity,
            orientationError);
        desiredWorldAcceleration = ClampVectorMagnitude(desiredWorldAcceleration, AutoDockMaxLinearAcceleration);
        Vector3D desiredLocalAccelerationD = Vector3D.TransformNormal(desiredWorldAcceleration, inverseGridMatrix);
        Vector3 localThrustCommand = new Vector3(
            (float)(desiredLocalAccelerationD.X / AutoDockMaxLinearAcceleration),
            (float)(desiredLocalAccelerationD.Y / AutoDockMaxLinearAcceleration),
            (float)(desiredLocalAccelerationD.Z / AutoDockMaxLinearAcceleration));
        localThrustCommand = Vector3.Clamp(localThrustCommand, -Vector3.One, Vector3.One);

        autoDockOrientationErrorIntegral += orientationError * AutoDockControlStepSeconds;
        autoDockOrientationErrorIntegral = ClampVectorMagnitude(autoDockOrientationErrorIntegral, AutoDockAngularIntegralLimit);

        Vector3D angularVelocity = grid.Physics.AngularVelocity;
        Vector3D desiredAngularVelocity =
            orientationError * AutoDockAngularProportionalGain
            + autoDockOrientationErrorIntegral * AutoDockAngularIntegralGain;
        desiredAngularVelocity = ClampVectorMagnitude(desiredAngularVelocity, AutoDockMaxAngularVelocity);

        Vector3D worldTorque = (desiredAngularVelocity - angularVelocity) * AutoDockAngularVelocityGain;
        Vector3D localTorqueD = Vector3D.TransformNormal(worldTorque, inverseGridMatrix);
        Vector3 localTorque = new Vector3((float)localTorqueD.X, (float)localTorqueD.Y, (float)localTorqueD.Z);
        float torqueLimit = MathHelper.Lerp(
            AutoDockAngularMinTorque,
            AutoDockAngularMaxTorque,
            MathHelper.Clamp((float)(orientationError.Length() / AutoDockAngularSofteningThreshold), 0f, 1f));
        localTorque = Vector3.ClampToSphere(localTorque, torqueLimit);

        if (thrustComponent != null)
        {
            thrustComponent.Enabled = true;
            thrustComponent.AutopilotEnabled = true;
            thrustComponent.AutoPilotControlThrust = localThrustCommand;
            thrustComponent.MarkDirty();
        }

        if (gyroSystem != null)
        {
            gyroSystem.AutopilotEnabled = true;
            gyroSystem.ControlTorque = localTorque;
            gyroSystem.MarkDirty();
        }
    }

    private static Vector3D GetDesiredWorldAcceleration(
        MyShipConnector targetConnector,
        Vector3D positionError,
        Vector3D positionErrorIntegral,
        Vector3D errorVelocity,
        Vector3D orientationError)
    {
        if (!TryGetDockingApproachAxis(targetConnector, out Vector3D approachAxis))
        {
            return positionError * AutoDockLinearProportionalGain
                   + positionErrorIntegral * AutoDockLinearIntegralGain
                   + errorVelocity * AutoDockLinearDerivativeGain;
        }

        Vector3D axialPositionError = approachAxis * Vector3D.Dot(positionError, approachAxis);
        Vector3D planarPositionError = positionError - axialPositionError;
        Vector3D axialIntegralError = approachAxis * Vector3D.Dot(positionErrorIntegral, approachAxis);
        Vector3D planarIntegralError = positionErrorIntegral - axialIntegralError;
        Vector3D axialVelocityError = approachAxis * Vector3D.Dot(errorVelocity, approachAxis);
        Vector3D planarVelocityError = errorVelocity - axialVelocityError;

        double finalApproachBlend = GetFinalApproachBlend(
            Math.Abs(Vector3D.Dot(positionError, approachAxis)),
            orientationError.Length());
        double planarProportionalGain = AutoDockLinearProportionalGain * (1.0 + finalApproachBlend * AutoDockFinalPlanarProportionalBoost);
        double planarIntegralGain = AutoDockLinearIntegralGain * (1.0 + finalApproachBlend * AutoDockFinalPlanarIntegralBoost);
        double planarDerivativeGain = AutoDockLinearDerivativeGain * (1.0 + finalApproachBlend * AutoDockFinalPlanarDerivativeBoost);
        double planarHoldStrength = MathHelper.Clamp((float)(planarPositionError.Length() / AutoDockFinalPlanarHoldDistance), 0f, 1f);
        double axialScale = 1.0 - finalApproachBlend * planarHoldStrength * AutoDockFinalAxialSuppression;

        return axialPositionError * (AutoDockLinearProportionalGain * axialScale)
               + axialIntegralError * (AutoDockLinearIntegralGain * axialScale)
               + axialVelocityError * (AutoDockLinearDerivativeGain * axialScale)
               + planarPositionError * planarProportionalGain
               + planarIntegralError * planarIntegralGain
               + planarVelocityError * planarDerivativeGain;
    }

    private static double GetFinalApproachBlend(double axialDistance, double orientationMagnitude)
    {
        if (axialDistance >= AutoDockFinalApproachDistance)
            return 0.0;

        double distanceBlend = 1.0 - axialDistance / AutoDockFinalApproachDistance;
        double orientationBlend = 1.0 - MathHelper.Clamp((float)(orientationMagnitude / AutoDockFinalApproachOrientationThreshold), 0f, 1f);
        return distanceBlend * orientationBlend;
    }

    private static bool TryGetDockingApproachAxis(MyShipConnector targetConnector, out Vector3D approachAxis)
    {
        approachAxis = Vector3D.Zero;
        if (targetConnector == null)
            return false;

        approachAxis = targetConnector.WorldMatrix.Forward;
        if (approachAxis.LengthSquared() < MinConnectorDistanceSquared)
            return false;

        approachAxis.Normalize();
        return true;
    }

    private static Vector3D GetOrientationErrorVector(MatrixD currentMatrix, MatrixD targetMatrix)
    {
        return Vector3D.Cross(currentMatrix.Forward, targetMatrix.Forward)
               + Vector3D.Cross(currentMatrix.Up, targetMatrix.Up)
               + Vector3D.Cross(currentMatrix.Right, targetMatrix.Right);
    }

    private static Vector3D ClampVectorMagnitude(Vector3D vector, double maxLength)
    {
        if (maxLength <= 0.0)
            return Vector3D.Zero;

        double lengthSquared = vector.LengthSquared();
        if (lengthSquared <= maxLength * maxLength || lengthSquared < MinConnectorDistanceSquared)
            return vector;

        return vector * (maxLength / Math.Sqrt(lengthSquared));
    }

    private void HandleSaveAlignmentPressed()
    {
        if (!TryGetPairForAlignmentSave(out DockingPair pair))
        {
            Notify("AutoDock: no selected or connected connector pair to save.", "Red");
            return;
        }

        if (!TryBuildRelativeConnectorAlignment(pair.Local, pair.Target, out MatrixD relativeConnectorMatrix))
        {
            Notify("AutoDock: cannot save connector alignment right now.", "Red");
            return;
        }

        SavedConnectorAlignment savedAlignment = GetOrCreateSavedAlignment(pair.Local.EntityId, pair.Target.EntityId);
        savedAlignment.SetRelativeConnectorMatrix(relativeConnectorMatrix);
        savedAlignment.LocalGridName = GetGridDisplayName(pair.Local.CubeGrid);
        savedAlignment.TargetGridName = GetGridDisplayName(pair.Target.CubeGrid);
        ConfigStorage.Save(Config.Current);

        if (active)
        {
            RescanPairs(preserveSelection: true);
            framesUntilRescan = RescanIntervalFrames;
        }

        Notify("AutoDock: saved connector alignment for current pair.", "Green");
    }

    private void HandleRemoveAlignmentPressed()
    {
        if (TryGetConnectedPair(out DockingPair pair)
            && TryGetSavedAlignment(pair.Local.EntityId, pair.Target.EntityId, out SavedConnectorAlignment savedAlignment, out _))
        {
            RemoveSavedAlignment(savedAlignment, $"AutoDock: removed saved alignment for {savedAlignment.GetDisplayName(GetGridDisplayName(pair.Local.CubeGrid))}.");
            return;
        }

        OpenRemoveAlignmentScreen();
    }

    private void OpenRemoveAlignmentScreen()
    {
        if (removeAlignmentScreen != null)
            return;

        List<SavedConnectorAlignment> savedAlignments = Config.Current.SavedAlignments;
        if (savedAlignments.Count == 0)
        {
            Notify("AutoDock: no saved alignments to remove.", "Red");
            return;
        }

        string currentGridName = GetGridDisplayName(MySession.Static?.ControlledGrid);
        var screen = new SavedAlignmentRemovalScreen(savedAlignments, currentGridName, HandlePopupAlignmentRemoval);
        screen.Closed += (_, _) =>
        {
            if (ReferenceEquals(removeAlignmentScreen, screen))
                removeAlignmentScreen = null;
        };

        removeAlignmentScreen = screen;
        MyGuiSandbox.AddScreen(screen);
    }

    private void HandlePopupAlignmentRemoval(SavedConnectorAlignment savedAlignment)
    {
        if (savedAlignment == null)
            return;

        RemoveSavedAlignment(savedAlignment, $"AutoDock: removed saved alignment for {savedAlignment.GetDisplayName(GetGridDisplayName(MySession.Static?.ControlledGrid))}.");
    }

    private void RemoveSavedAlignment(SavedConnectorAlignment savedAlignment, string message)
    {
        if (savedAlignment == null || !Config.Current.SavedAlignments.Remove(savedAlignment))
            return;

        ConfigStorage.Save(Config.Current);

        if (active)
        {
            RescanPairs(preserveSelection: true);
            framesUntilRescan = RescanIntervalFrames;
        }

        Notify(message, "Green");
    }

    private void ResetAutoDockPidState()
    {
        autoDockPositionErrorIntegral = Vector3D.Zero;
        autoDockOrientationErrorIntegral = Vector3D.Zero;
    }

    private void ReleaseShipControl(MyCubeGrid grid)
    {
        if (grid == null)
            return;

        if (TryGetActiveShipController(grid, out MyShipController controller))
        {
            controller.MoveAndRotateStopped();
        }

        MyEntityThrustComponent thrustComponent = grid.Components?.Get<MyEntityThrustComponent>();
        if (thrustComponent != null)
        {
            thrustComponent.AutoPilotControlThrust = Vector3.Zero;
            thrustComponent.AutopilotEnabled = false;
            thrustComponent.Enabled = true;
            thrustComponent.MarkDirty();
        }

        MyGridGyroSystem gyroSystem = grid.GridSystems?.GyroSystem;
        if (gyroSystem != null)
        {
            gyroSystem.ControlTorque = Vector3.Zero;
            gyroSystem.AutopilotEnabled = false;
            gyroSystem.MarkDirty();
        }
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
        string savedAlignmentState = pair.HasSavedAlignment ? ", saved alignment" : "";
        Notify($"AutoDock: pair {selectedIndex + 1}/{pairs.Count}, {pair.Distance:0.0} m, {state}{savedAlignmentState}.", pair.InRange ? "White" : "Red");
    }

    private void ClearSelection()
    {
        ReleaseShipControl(autoDockingPair?.Local?.CubeGrid);
        ResetAutoDockPidState();
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
        changed |= EnsureGameControl(
            input,
            SaveAlignmentControlId,
            "AutoDock Save Alignment",
            Config.Current.SaveAlignmentKeybind,
            ref registeredSaveAlignmentKeybind,
            ref saveAlignmentControl);
        changed |= EnsureGameControl(
            input,
            RemoveAlignmentControlId,
            "AutoDock Remove Alignment",
            Config.Current.RemoveAlignmentKeybind,
            ref registeredRemoveAlignmentKeybind,
            ref removeAlignmentControl);

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
        changed |= EnsureGameControl(
            input,
            SaveAlignmentControlId,
            "AutoDock Save Alignment",
            unbound,
            ref registeredSaveAlignmentKeybind,
            ref saveAlignmentControl);
        changed |= EnsureGameControl(
            input,
            RemoveAlignmentControlId,
            "AutoDock Remove Alignment",
            unbound,
            ref registeredRemoveAlignmentKeybind,
            ref removeAlignmentControl);

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

    private bool IsSaveAlignmentPressed(IMyInput input)
    {
        return IsControlNewPressed(saveAlignmentControl, Config.Current.SaveAlignmentKeybind, input);
    }

    private bool IsRemoveAlignmentPressed(IMyInput input)
    {
        return IsControlNewPressed(removeAlignmentControl, Config.Current.RemoveAlignmentKeybind, input);
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

        int savedAlignmentComparison = y.HasSavedAlignment.CompareTo(x.HasSavedAlignment);
        if (savedAlignmentComparison != 0)
            return savedAlignmentComparison;

        return x.Distance.CompareTo(y.Distance);
    }

    private bool TryGetPairForAlignmentSave(out DockingPair pair)
    {
        if (TryGetSelectedPair(out pair))
            return true;

        if (autoDockingPair != null)
        {
            pair = autoDockingPair;
            return true;
        }

        return TryGetConnectedPair(out pair);
    }

    private static bool TryGetConnectedPair(out DockingPair pair)
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

            double distance = Vector3D.Distance(localConnector.PositionComp.GetPosition(), targetConnector.PositionComp.GetPosition());
            bool lockReady = IsLockReady(localConnector, targetConnector);
            pair = new DockingPair(localConnector, targetConnector, distance, lockReady);
            return true;
        }

        return false;
    }

    private static bool TryGetSavedConnectorAlignmentMatrix(DockingPair pair, out MatrixD relativeConnectorMatrix)
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

    private static bool TryGetSavedAlignment(long localConnectorId, long targetConnectorId, out SavedConnectorAlignment savedAlignment, out bool invert)
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

    private static bool HasSavedAlignment(long localConnectorId, long targetConnectorId)
    {
        return TryGetSavedAlignment(localConnectorId, targetConnectorId, out _, out _);
    }

    private static string GetGridDisplayName(MyCubeGrid grid)
    {
        if (grid == null)
            return "Unknown grid";

        return string.IsNullOrWhiteSpace(grid.DisplayName) ? "Unknown grid" : grid.DisplayName;
    }

    private static bool TryBuildRelativeConnectorAlignment(MyShipConnector localConnector, MyShipConnector targetConnector, out MatrixD relativeConnectorMatrix)
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

    private static SavedConnectorAlignment GetOrCreateSavedAlignment(long localConnectorId, long targetConnectorId)
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
        public readonly bool DockingReady;

        public DockingTarget(MatrixD worldMatrix, Vector3D constraintPosition, bool dockingReady)
        {
            WorldMatrix = worldMatrix;
            ConstraintPosition = constraintPosition;
            DockingReady = dockingReady;
        }
    }

    private sealed class DockingPair
    {
        public readonly MyShipConnector Local;
        public readonly MyShipConnector Target;
        public readonly bool HasSavedAlignment;
        public double Distance { get; private set; }
        public bool InRange { get; private set; }
        public bool LockReady { get; private set; }

        public DockingPair(MyShipConnector local, MyShipConnector target, double distance, bool lockReady)
        {
            Local = local;
            Target = target;
            HasSavedAlignment = HasSavedAlignment(local.EntityId, target.EntityId);
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
