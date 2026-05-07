using System;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.GameSystems;
using Sandbox.Game.World;
using Sandbox.ModAPI.Ingame;
using VRage.Game.Entity;
using VRage.Input;
using VRageMath;

namespace ClientPlugin;

internal static class DockingMath
{
    public static bool IsConnectorAvailable(MyShipConnector connector, bool localSide)
    {
        if (connector == null || connector.MarkedForClose || connector.CubeGrid == null || connector.CubeGrid.MarkedForClose)
            return false;

        if (!connector.IsWorking || connector.Connected)
            return false;

        return !localSide || connector.HasLocalPlayerAccess();
    }

    public static bool AreFacing(MyShipConnector localConnector, MyShipConnector targetConnector, Vector3D localPosition, Vector3D targetPosition)
    {
        Vector3D delta = targetPosition - localPosition;
        if (delta.LengthSquared() < AutoDockConstants.MinConnectorDistanceSquared)
            return false;

        Vector3D direction = Vector3D.Normalize(delta);
        double forwardOpposition = Vector3D.Dot(localConnector.WorldMatrix.Forward, targetConnector.WorldMatrix.Forward);
        double localAim = Vector3D.Dot(localConnector.WorldMatrix.Forward, direction);
        double targetAim = Vector3D.Dot(targetConnector.WorldMatrix.Forward, -direction);

        return forwardOpposition <= -0.65 && localAim >= 0.15 && targetAim >= 0.15;
    }

    public static bool IsLockReady(MyShipConnector localConnector, MyShipConnector targetConnector)
    {
        return localConnector != null
               && targetConnector != null
               && localConnector.InConstraint
               && localConnector.Other == targetConnector
               && !localConnector.Connected;
    }

    public static bool IsLockedPair(MyShipConnector localConnector, MyShipConnector targetConnector)
    {
        IMyShipConnector localConnectorApi = localConnector;
        IMyShipConnector targetConnectorApi = targetConnector;
        return localConnector != null
               && targetConnector != null
               && localConnectorApi.Status == MyShipConnectorStatus.Connected
               && targetConnectorApi.Status == MyShipConnectorStatus.Connected
               && localConnector.Connected
               && targetConnector.Connected
               && localConnector.Other == targetConnector
               && targetConnector.Other == localConnector;
    }

    public static bool IsDocked(DockingPair pair)
    {
        return pair?.Local != null
               && pair.Target != null
               && pair.Local.Connected
               && pair.Local.Other == pair.Target;
    }

    public static bool HasManualFlightInput()
    {
        IMyInput input = MyInput.Static;
        if (input == null)
            return false;

        Vector3 moveInput = input.GetPositionDelta();
        if (moveInput.LengthSquared() > AutoDockConstants.ManualInputDeadzone * AutoDockConstants.ManualInputDeadzone)
            return true;

        if (!input.IsAnyAltKeyPressed())
        {
            Vector2 rotationInput = input.GetRotation();
            if (rotationInput.LengthSquared() > AutoDockConstants.ManualInputDeadzone * AutoDockConstants.ManualInputDeadzone)
                return true;
        }

        return Math.Abs(input.GetRoll()) > AutoDockConstants.ManualInputDeadzone;
    }

    public static bool IsPairStillUsable(DockingPair pair)
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

    public static bool TryGetActiveShipController(MyCubeGrid grid, out MyShipController controller)
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

    public static bool TryCreateDockingTarget(DockingPair pair, LockRotationService lockRotationService, out DockingTarget target)
    {
        target = null;
        MyCubeGrid grid = pair.Local.CubeGrid;
        MatrixD currentGridMatrix = grid.PositionComp.WorldMatrixRef;
        Vector3D currentConstraintPosition = pair.Local.ConstraintPositionWorld();
        Vector3D targetConstraintPosition = pair.Target.ConstraintPositionWorld();
        if (!TryCreateDockingWorldMatrix(pair, lockRotationService, targetConstraintPosition, out MatrixD targetMatrix))
            return false;

        bool positionReady = Vector3D.DistanceSquared(currentConstraintPosition, targetConstraintPosition)
                             <= AutoDockConstants.AutoDockPositionTolerance * AutoDockConstants.AutoDockPositionTolerance;
        bool angleReady = GetOrientationErrorVector(currentGridMatrix, targetMatrix).LengthSquared()
                          <= AutoDockConstants.AutoDockOrientationTolerance * AutoDockConstants.AutoDockOrientationTolerance;
        target = new DockingTarget(targetMatrix, targetConstraintPosition, positionReady && angleReady);
        return true;
    }

    public static bool TryGetGravityTiltWarning(DockingPair pair, LockRotationService lockRotationService, out string warning)
    {
        warning = null;
        if (!TryGetActiveShipController(pair.Local.CubeGrid, out MyShipController controller))
            return false;

        Vector3D gravity = controller.GetNaturalGravity();
        if (gravity.LengthSquared() < AutoDockConstants.MinConnectorDistanceSquared)
            return false;

        if (!TryGetGridRotationTarget(pair, lockRotationService, out MatrixD targetOrientation))
            return false;

        MatrixD desiredControllerMatrix = GetDesiredControllerWorldMatrix(controller, targetOrientation);
        Vector3D desiredControllerUp = desiredControllerMatrix.Up;
        if (desiredControllerUp.LengthSquared() < AutoDockConstants.MinConnectorDistanceSquared)
            return false;

        desiredControllerUp.Normalize();
        Vector3D gravityUp = -Vector3D.Normalize(gravity);
        double dot = MathHelper.Clamp(Vector3D.Dot(desiredControllerUp, gravityUp), -1.0, 1.0);
        double tiltAngle = Math.Acos(dot);
        if (tiltAngle <= AutoDockConstants.MaxGravityTiltWarningRadians)
            return false;

        warning = $"AutoDock: docking needs {MathHelper.ToDegrees((float)tiltAngle):0} deg cockpit tilt from gravity. Move ship first.";
        return true;
    }

    public static void ApplyAutoDockControl(
        ref Vector3D positionErrorIntegral,
        ref Vector3D orientationErrorIntegral,
        DockingPair pair,
        MyShipController controller,
        DockingTarget target)
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
        if (positionError.LengthSquared()
            <= AutoDockConstants.AutoDockLinearIntegralActivationDistance * AutoDockConstants.AutoDockLinearIntegralActivationDistance)
        {
            positionErrorIntegral += positionError * AutoDockConstants.AutoDockControlStepSeconds;
        }
        else
        {
            positionErrorIntegral = Vector3D.Zero;
        }

        positionErrorIntegral = ClampVectorMagnitude(positionErrorIntegral, AutoDockConstants.AutoDockLinearIntegralLimit);

        Vector3D desiredWorldAcceleration = GetDesiredWorldAcceleration(
            pair.Target,
            positionError,
            positionErrorIntegral,
            errorVelocity,
            orientationError);
        desiredWorldAcceleration = ClampVectorMagnitude(desiredWorldAcceleration, AutoDockConstants.AutoDockMaxLinearAcceleration);
        Vector3D desiredLocalAccelerationD = Vector3D.TransformNormal(desiredWorldAcceleration, inverseGridMatrix);
        Vector3 localThrustCommand = new Vector3(
            (float)(desiredLocalAccelerationD.X / AutoDockConstants.AutoDockMaxLinearAcceleration),
            (float)(desiredLocalAccelerationD.Y / AutoDockConstants.AutoDockMaxLinearAcceleration),
            (float)(desiredLocalAccelerationD.Z / AutoDockConstants.AutoDockMaxLinearAcceleration));
        localThrustCommand = Vector3.Clamp(localThrustCommand, -Vector3.One, Vector3.One);

        orientationErrorIntegral += orientationError * AutoDockConstants.AutoDockControlStepSeconds;
        orientationErrorIntegral = ClampVectorMagnitude(orientationErrorIntegral, AutoDockConstants.AutoDockAngularIntegralLimit);

        Vector3D angularVelocity = grid.Physics.AngularVelocity;
        Vector3D desiredAngularVelocity =
            orientationError * AutoDockConstants.AutoDockAngularProportionalGain
            + orientationErrorIntegral * AutoDockConstants.AutoDockAngularIntegralGain;
        desiredAngularVelocity = ClampVectorMagnitude(desiredAngularVelocity, AutoDockConstants.AutoDockMaxAngularVelocity);

        Vector3D worldTorque = (desiredAngularVelocity - angularVelocity) * AutoDockConstants.AutoDockAngularVelocityGain;
        Vector3D localTorqueD = Vector3D.TransformNormal(worldTorque, inverseGridMatrix);
        Vector3 localTorque = new Vector3((float)localTorqueD.X, (float)localTorqueD.Y, (float)localTorqueD.Z);
        float torqueLimit = MathHelper.Lerp(
            AutoDockConstants.AutoDockAngularMinTorque,
            AutoDockConstants.AutoDockAngularMaxTorque,
            MathHelper.Clamp((float)(orientationError.Length() / AutoDockConstants.AutoDockAngularSofteningThreshold), 0f, 1f));
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

    public static void ReleaseShipControl(MyCubeGrid grid)
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

    public static bool IsWithinSearchRange(double distance)
    {
        return distance <= AutoDockConstants.GetSearchRadius();
    }

    public static Vector3D GetConnectorReferencePosition(MyShipConnector connector)
    {
        if (connector == null)
            return Vector3D.Zero;

        return connector.CubeGrid != null && !connector.CubeGrid.MarkedForClose
            ? connector.ConstraintPositionWorld()
            : connector.PositionComp.GetPosition();
    }

    public static int NormalizeRotationStep(int rotationStep)
    {
        rotationStep %= AutoDockConstants.LockRotationStepCount;
        if (rotationStep < 0)
            rotationStep += AutoDockConstants.LockRotationStepCount;

        return rotationStep;
    }

    public static int GetRotationAngleDegrees(int rotationStep)
    {
        return NormalizeRotationStep(rotationStep) * 360 / AutoDockConstants.LockRotationStepCount;
    }

    public static bool TryGetOrientationAngleDegrees(DockingPair pair, int rotationStep, out int angleDegrees)
    {
        angleDegrees = 0;
        if (!TryGetDesiredOrientationIndicator(pair, rotationStep, out Vector3D faceNormal, out Vector3D direction, out _))
            return false;

        Vector3D baseDirection = RejectFromAxis(pair.Target.WorldMatrix.Up, faceNormal);
        if (baseDirection.LengthSquared() < AutoDockConstants.MinConnectorDistanceSquared)
            baseDirection = RejectFromAxis(pair.Target.WorldMatrix.Right, faceNormal);
        if (baseDirection.LengthSquared() < AutoDockConstants.MinConnectorDistanceSquared)
            return false;

        baseDirection.Normalize();
        double angleRadians = SignedAngleAroundAxis(baseDirection, direction, faceNormal);
        if (angleRadians < 0.0)
            angleRadians += Math.PI * 2.0;

        angleDegrees = (int)Math.Round(MathHelper.ToDegrees((float)angleRadians));
        if (angleDegrees >= 360)
            angleDegrees -= 360;
        return true;
    }

    public static int GetDefaultRotationStep(DockingPair pair)
    {
        if (!TryGetCurrentOrientationIndicator(pair, out Vector3D currentDirection))
            return 0;

        int bestRotationStep = 0;
        double bestAlignment = double.NegativeInfinity;
        bool found = false;
        for (int rotationStep = 0; rotationStep < AutoDockConstants.LockRotationStepCount; rotationStep++)
        {
            if (!TryGetDesiredOrientationIndicator(pair, rotationStep, out _, out Vector3D desiredDirection, out _))
                continue;

            double alignment = Vector3D.Dot(currentDirection, desiredDirection);
            if (!found || alignment > bestAlignment)
            {
                bestAlignment = alignment;
                bestRotationStep = rotationStep;
                found = true;
            }
        }

        return found ? bestRotationStep : 0;
    }

    public static bool TryCreateDesiredLocalConnectorWorldMatrix(
        MyShipConnector targetConnector,
        int rotationStep,
        out MatrixD desiredLocalConnectorWorldMatrix)
    {
        desiredLocalConnectorWorldMatrix = MatrixD.Identity;
        if (!TryGetRotationBasis(targetConnector, out Vector3D desiredForward, out Vector3D baseUp))
            return false;

        QuaternionD rollRotation = QuaternionD.CreateFromAxisAngle(
            desiredForward,
            NormalizeRotationStep(rotationStep) * AutoDockConstants.LockRotationStepRadians);
        Vector3D desiredUp = RejectFromAxis(rollRotation * baseUp, desiredForward);
        if (desiredUp.LengthSquared() < AutoDockConstants.MinConnectorDistanceSquared)
            return false;

        desiredUp.Normalize();
        desiredLocalConnectorWorldMatrix = MatrixD.CreateWorld(Vector3D.Zero, desiredForward, desiredUp);
        desiredLocalConnectorWorldMatrix.Translation = Vector3D.Zero;
        return desiredLocalConnectorWorldMatrix.IsValid();
    }

    public static bool TryGetActualRotationStep(MyShipConnector localConnector, MyShipConnector targetConnector, out int rotationStep)
    {
        rotationStep = 0;
        if (localConnector == null || targetConnector == null)
            return false;

        return TryQuantizeRotationStep(targetConnector, localConnector.WorldMatrix.Up, out rotationStep);
    }

    public static bool TryGetDesiredOrientationIndicator(
        DockingPair pair,
        int rotationStep,
        out Vector3D faceNormal,
        out Vector3D direction,
        out Vector3D right)
    {
        faceNormal = Vector3D.Zero;
        direction = Vector3D.Zero;
        right = Vector3D.Zero;
        if (pair?.Local == null || pair.Target == null || pair.Local.CubeGrid == null)
            return false;

        if (!TryCreateGridRotationTarget(pair, rotationStep, out MatrixD targetOrientation))
            return false;

        if (!TryGetReferenceControllerAxesLocal(pair.Local.CubeGrid, out Vector3D localReferenceForward, out Vector3D localReferenceUp))
            return false;

        Vector3D desiredReferenceForward = Vector3D.TransformNormal(localReferenceForward, targetOrientation);
        Vector3D desiredReferenceUp = Vector3D.TransformNormal(localReferenceUp, targetOrientation);
        return TryProjectTargetFaceDirection(
            pair.Target,
            desiredReferenceForward,
            desiredReferenceUp,
            out faceNormal,
            out direction,
            out right);
    }

    private static bool TryCreateDockingWorldMatrix(
        DockingPair pair,
        LockRotationService lockRotationService,
        Vector3D desiredConstraintPosition,
        out MatrixD targetMatrix)
    {
        targetMatrix = MatrixD.Identity;
        MyCubeGrid grid = pair.Local.CubeGrid;
        MatrixD currentGridMatrix = grid.PositionComp.WorldMatrixRef;

        if (!TryGetGridRotationTarget(pair, lockRotationService, out MatrixD targetOrientation))
            return false;

        Vector3D localConstraintPosition = Vector3D.Transform(pair.Local.ConstraintPositionWorld(), MatrixD.Invert(currentGridMatrix));
        Vector3D transformedConstraintPosition = Vector3D.Transform(localConstraintPosition, targetOrientation);
        targetMatrix = targetOrientation;
        targetMatrix.Translation = desiredConstraintPosition - transformedConstraintPosition;

        return targetMatrix.IsValid();
    }

    private static bool TryGetGridRotationTarget(DockingPair pair, LockRotationService lockRotationService, out MatrixD targetOrientation)
    {
        return TryCreateGridRotationTarget(pair, lockRotationService.GetRotationStep(pair), out targetOrientation);
    }

    private static bool TryCreateGridRotationTarget(
        DockingPair pair,
        int rotationStep,
        out MatrixD targetOrientation)
    {
        targetOrientation = MatrixD.Identity;
        if (!TryCreateDesiredLocalConnectorWorldMatrix(pair.Target, rotationStep, out MatrixD desiredLocalConnectorWorldMatrix))
            return false;

        if (!TryGetLocalConnectorGridMatrix(pair, out MatrixD localConnectorGridMatrix))
            return false;

        targetOrientation = MatrixD.Invert(localConnectorGridMatrix) * desiredLocalConnectorWorldMatrix;
        targetOrientation.Translation = Vector3D.Zero;
        return targetOrientation.IsValid();
    }

    private static bool TryGetDesiredLocalConnectorWorldMatrix(
        DockingPair pair,
        MatrixD targetOrientation,
        out MatrixD desiredLocalConnectorWorldMatrix)
    {
        desiredLocalConnectorWorldMatrix = MatrixD.Identity;
        if (!TryGetLocalConnectorGridMatrix(pair, out MatrixD localConnectorGridMatrix))
            return false;

        desiredLocalConnectorWorldMatrix = localConnectorGridMatrix * targetOrientation;
        desiredLocalConnectorWorldMatrix.Translation = Vector3D.Zero;
        return desiredLocalConnectorWorldMatrix.IsValid();
    }

    private static bool TryGetLocalConnectorGridMatrix(DockingPair pair, out MatrixD localConnectorGridMatrix)
    {
        localConnectorGridMatrix = MatrixD.Identity;
        if (pair?.Local == null || pair.Local.CubeGrid == null)
            return false;

        MatrixD currentGridMatrix = pair.Local.CubeGrid.PositionComp.WorldMatrixRef;
        currentGridMatrix.Translation = Vector3D.Zero;
        MatrixD inverseGridMatrix = MatrixD.Invert(currentGridMatrix);

        MatrixD localConnectorWorldMatrix = pair.Local.WorldMatrix;
        localConnectorWorldMatrix.Translation = Vector3D.Zero;

        localConnectorGridMatrix = localConnectorWorldMatrix * inverseGridMatrix;
        localConnectorGridMatrix.Translation = Vector3D.Zero;
        return localConnectorGridMatrix.IsValid();
    }

    private static bool TryGetReferenceControllerAxesLocal(
        MyCubeGrid grid,
        out Vector3D localReferenceForward,
        out Vector3D localReferenceUp)
    {
        localReferenceForward = Vector3D.Zero;
        localReferenceUp = Vector3D.Zero;
        if (grid == null || grid.MarkedForClose)
            return false;

        MatrixD currentGridMatrix = grid.PositionComp.WorldMatrixRef;
        currentGridMatrix.Translation = Vector3D.Zero;
        MatrixD inverseGridMatrix = MatrixD.Invert(currentGridMatrix);

        localReferenceForward = Vector3D.TransformNormal(GetReferenceControllerForward(grid), inverseGridMatrix);
        localReferenceUp = Vector3D.TransformNormal(GetReferenceControllerUp(grid), inverseGridMatrix);
        if (localReferenceForward.LengthSquared() < AutoDockConstants.MinConnectorDistanceSquared
            || localReferenceUp.LengthSquared() < AutoDockConstants.MinConnectorDistanceSquared)
            return false;

        localReferenceForward.Normalize();
        localReferenceUp.Normalize();
        return true;
    }

    private static bool TryGetCurrentOrientationIndicator(DockingPair pair, out Vector3D direction)
    {
        direction = Vector3D.Zero;
        return pair?.Local?.CubeGrid != null
               && pair.Target != null
               && TryProjectTargetFaceDirection(
                   pair.Target,
                   GetReferenceControllerForward(pair.Local.CubeGrid),
                   GetReferenceControllerUp(pair.Local.CubeGrid),
                   out _,
                   out direction,
                   out _);
    }

    private static bool TryGetContinuousGridRotationTarget(DockingPair pair, out MatrixD targetOrientation)
    {
        targetOrientation = MatrixD.Identity;
        MyCubeGrid grid = pair.Local.CubeGrid;
        MatrixD currentGridMatrix = grid.PositionComp.WorldMatrixRef;
        MatrixD inverseGridMatrix = MatrixD.Invert(currentGridMatrix);
        Vector3D desiredConnectorForward = -Vector3D.Normalize(pair.Target.WorldMatrix.Forward);
        Vector3D localConnectorForward = Vector3D.TransformNormal(pair.Local.WorldMatrix.Forward, inverseGridMatrix);
        Vector3D localRollReference = Vector3D.TransformNormal(GetReferenceControllerUp(grid), inverseGridMatrix);
        if (localConnectorForward.LengthSquared() < AutoDockConstants.MinConnectorDistanceSquared
            || localRollReference.LengthSquared() < AutoDockConstants.MinConnectorDistanceSquared)
            return false;

        localConnectorForward.Normalize();
        localRollReference.Normalize();

        if (RejectFromAxis(localRollReference, localConnectorForward).LengthSquared() < AutoDockConstants.MinConnectorDistanceSquared)
        {
            localRollReference = Vector3D.TransformNormal(pair.Local.WorldMatrix.Up, inverseGridMatrix);
            if (localRollReference.LengthSquared() < AutoDockConstants.MinConnectorDistanceSquared)
                return false;

            localRollReference.Normalize();
        }

        QuaternionD combinedRotation = CreateRotationBetweenVectors(localConnectorForward, desiredConnectorForward, localRollReference);
        Vector3D desiredRollReference = GetDesiredRollReference(pair.Target, grid);
        Vector3D currentProjected = RejectFromAxis(combinedRotation * localRollReference, desiredConnectorForward);
        Vector3D desiredProjected = RejectFromAxis(desiredRollReference, desiredConnectorForward);
        if (currentProjected.LengthSquared() > AutoDockConstants.MinConnectorDistanceSquared
            && desiredProjected.LengthSquared() > AutoDockConstants.MinConnectorDistanceSquared)
        {
            currentProjected.Normalize();
            desiredProjected.Normalize();
            double signedAngle = SignedAngleAroundAxis(currentProjected, desiredProjected, desiredConnectorForward);
            QuaternionD rollAlign = QuaternionD.CreateFromAxisAngle(desiredConnectorForward, signedAngle);
            combinedRotation = QuaternionD.Normalize(rollAlign * combinedRotation);
        }

        targetOrientation = MatrixD.CreateFromQuaternion(combinedRotation);
        targetOrientation.Translation = Vector3D.Zero;
        return targetOrientation.IsValid();
    }

    private static Vector3D GetReferenceControllerUp(MyCubeGrid grid)
    {
        return TryGetActiveShipController(grid, out MyShipController controller)
            ? controller.WorldMatrix.Up
            : grid.WorldMatrix.Up;
    }

    private static Vector3D GetReferenceControllerForward(MyCubeGrid grid)
    {
        return TryGetActiveShipController(grid, out MyShipController controller)
            ? controller.WorldMatrix.Forward
            : grid.WorldMatrix.Forward;
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
        if (gravity.LengthSquared() > AutoDockConstants.MinConnectorDistanceSquared)
            return -Vector3D.Normalize(gravity);

        Vector3D connectorUp = targetConnector.WorldMatrix.Up;
        if (connectorUp.LengthSquared() > AutoDockConstants.MinConnectorDistanceSquared)
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
            if (axis.LengthSquared() < AutoDockConstants.MinConnectorDistanceSquared)
                axis = RejectFromAxis(Vector3D.Up, normalizedFrom);
            if (axis.LengthSquared() < AutoDockConstants.MinConnectorDistanceSquared)
                axis = RejectFromAxis(Vector3D.Right, normalizedFrom);
            axis.Normalize();
            return QuaternionD.CreateFromAxisAngle(axis, Math.PI);
        }

        Vector3D rotationAxis = Vector3D.Cross(normalizedFrom, normalizedTo);
        rotationAxis.Normalize();
        return QuaternionD.CreateFromAxisAngle(rotationAxis, Math.Acos(dot));
    }

    private static bool TryGetRotationBasis(MyShipConnector targetConnector, out Vector3D desiredForward, out Vector3D baseUp)
    {
        desiredForward = Vector3D.Zero;
        baseUp = Vector3D.Zero;
        if (targetConnector == null)
            return false;

        desiredForward = -targetConnector.WorldMatrix.Forward;
        if (desiredForward.LengthSquared() < AutoDockConstants.MinConnectorDistanceSquared)
            return false;

        desiredForward.Normalize();
        baseUp = RejectFromAxis(targetConnector.WorldMatrix.Up, desiredForward);
        if (baseUp.LengthSquared() < AutoDockConstants.MinConnectorDistanceSquared)
            baseUp = RejectFromAxis(targetConnector.WorldMatrix.Right, desiredForward);
        if (baseUp.LengthSquared() < AutoDockConstants.MinConnectorDistanceSquared)
            return false;

        baseUp.Normalize();
        return true;
    }

    private static bool TryProjectTargetFaceDirection(
        MyShipConnector targetConnector,
        Vector3D desiredReferenceForward,
        Vector3D desiredReferenceUp,
        out Vector3D faceNormal,
        out Vector3D direction,
        out Vector3D right)
    {
        faceNormal = Vector3D.Zero;
        direction = Vector3D.Zero;
        right = Vector3D.Zero;
        if (targetConnector == null)
            return false;

        faceNormal = targetConnector.WorldMatrix.Forward;
        if (faceNormal.LengthSquared() < AutoDockConstants.MinConnectorDistanceSquared)
            return false;

        faceNormal.Normalize();
        direction = RejectFromAxis(desiredReferenceForward, faceNormal);
        if (direction.LengthSquared() < AutoDockConstants.MinConnectorDistanceSquared)
            direction = RejectFromAxis(desiredReferenceUp, faceNormal);
        if (direction.LengthSquared() < AutoDockConstants.MinConnectorDistanceSquared)
            direction = RejectFromAxis(targetConnector.WorldMatrix.Up, faceNormal);
        if (direction.LengthSquared() < AutoDockConstants.MinConnectorDistanceSquared)
            direction = RejectFromAxis(targetConnector.WorldMatrix.Right, faceNormal);
        if (direction.LengthSquared() < AutoDockConstants.MinConnectorDistanceSquared)
            return false;

        direction.Normalize();
        right = Vector3D.Cross(faceNormal, direction);
        if (right.LengthSquared() < AutoDockConstants.MinConnectorDistanceSquared)
            return false;

        right.Normalize();
        return true;
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

    private static bool TryQuantizeRotationStep(MyShipConnector targetConnector, Vector3D localConnectorUp, out int rotationStep)
    {
        rotationStep = 0;
        if (!TryGetRotationBasis(targetConnector, out Vector3D desiredForward, out Vector3D baseUp))
            return false;

        Vector3D projectedUp = RejectFromAxis(localConnectorUp, desiredForward);
        if (projectedUp.LengthSquared() < AutoDockConstants.MinConnectorDistanceSquared)
            return false;

        projectedUp.Normalize();
        double angle = SignedAngleAroundAxis(baseUp, projectedUp, desiredForward);
        if (angle < 0.0)
            angle += Math.PI * 2.0;

        rotationStep = NormalizeRotationStep((int)Math.Floor(angle / AutoDockConstants.LockRotationStepRadians + 0.5));
        return true;
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
            return positionError * AutoDockConstants.AutoDockLinearProportionalGain
                   + positionErrorIntegral * AutoDockConstants.AutoDockLinearIntegralGain
                   + errorVelocity * AutoDockConstants.AutoDockLinearDerivativeGain;
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
        double planarProportionalGain =
            AutoDockConstants.AutoDockLinearProportionalGain * (1.0 + finalApproachBlend * AutoDockConstants.AutoDockFinalPlanarProportionalBoost);
        double planarIntegralGain =
            AutoDockConstants.AutoDockLinearIntegralGain * (1.0 + finalApproachBlend * AutoDockConstants.AutoDockFinalPlanarIntegralBoost);
        double planarDerivativeGain =
            AutoDockConstants.AutoDockLinearDerivativeGain * (1.0 + finalApproachBlend * AutoDockConstants.AutoDockFinalPlanarDerivativeBoost);
        double planarHoldStrength = MathHelper.Clamp((float)(planarPositionError.Length() / AutoDockConstants.AutoDockFinalPlanarHoldDistance), 0f, 1f);
        double axialScale = 1.0 - finalApproachBlend * planarHoldStrength * AutoDockConstants.AutoDockFinalAxialSuppression;

        return axialPositionError * (AutoDockConstants.AutoDockLinearProportionalGain * axialScale)
               + axialIntegralError * (AutoDockConstants.AutoDockLinearIntegralGain * axialScale)
               + axialVelocityError * (AutoDockConstants.AutoDockLinearDerivativeGain * axialScale)
               + planarPositionError * planarProportionalGain
               + planarIntegralError * planarIntegralGain
               + planarVelocityError * planarDerivativeGain;
    }

    private static double GetFinalApproachBlend(double axialDistance, double orientationMagnitude)
    {
        if (axialDistance >= AutoDockConstants.AutoDockFinalApproachDistance)
            return 0.0;

        double distanceBlend = 1.0 - axialDistance / AutoDockConstants.AutoDockFinalApproachDistance;
        double orientationBlend =
            1.0 - MathHelper.Clamp((float)(orientationMagnitude / AutoDockConstants.AutoDockFinalApproachOrientationThreshold), 0f, 1f);
        return distanceBlend * orientationBlend;
    }

    private static bool TryGetDockingApproachAxis(MyShipConnector targetConnector, out Vector3D approachAxis)
    {
        approachAxis = Vector3D.Zero;
        if (targetConnector == null)
            return false;

        approachAxis = targetConnector.WorldMatrix.Forward;
        if (approachAxis.LengthSquared() < AutoDockConstants.MinConnectorDistanceSquared)
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
        if (lengthSquared <= maxLength * maxLength || lengthSquared < AutoDockConstants.MinConnectorDistanceSquared)
            return vector;

        return vector * (maxLength / Math.Sqrt(lengthSquared));
    }
}
