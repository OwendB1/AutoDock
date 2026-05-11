using System;
using System.Reflection;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.World;
using Sandbox.ModAPI.Ingame;
using VRage.Input;
using VRageMath;

namespace ClientPlugin;

internal static class DockingMath
{
    private static readonly FieldInfo ConnectionPositionField = typeof(MyShipConnector)
        .GetField("m_connectionPosition", BindingFlags.NonPublic | BindingFlags.Instance);
    private static readonly FieldInfo ConnectorDummyLocalField = typeof(MyShipConnector)
        .GetField("m_connectorDummyLocal", BindingFlags.NonPublic | BindingFlags.Instance);

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
        Vector3D localDummyCenter = GetConnectorDummyCenterPosition(localConnector);
        if (localDummyCenter.LengthSquared() < AutoDockConstants.MinConnectorDistanceSquared)
            localDummyCenter = localPosition;

        Vector3D targetDummyCenter = GetConnectorDummyCenterPosition(targetConnector);
        if (targetDummyCenter.LengthSquared() < AutoDockConstants.MinConnectorDistanceSquared)
            targetDummyCenter = targetPosition;

        Vector3D delta = targetDummyCenter - localDummyCenter;
        if (delta.LengthSquared() < AutoDockConstants.MinConnectorDistanceSquared)
            return false;

        return IsFacingTowards(localConnector, targetDummyCenter)
               && IsFacingTowards(targetConnector, localDummyCenter);
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
        if (TryGetControlledShipController(grid, out controller))
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

    public static bool TryGetControlledShipController(MyCubeGrid grid, out MyShipController controller)
    {
        controller = MySession.Static?.ControlledEntity?.Entity as MyShipController;
        return controller != null
               && controller.CubeGrid == grid
               && !controller.MarkedForClose
               && controller.IsWorking;
    }

    public static bool TryCreateDockingTarget(
        DockingPair pair,
        LockRotationService lockRotationService,
        bool finalApproachStarted,
        out DockingTarget target,
        out bool finalApproachActive)
    {
        target = null;
        finalApproachActive = finalApproachStarted;
        MyCubeGrid grid = pair.Local.CubeGrid;
        MatrixD currentGridMatrix = grid.PositionComp.WorldMatrixRef;
        Vector3D currentConstraintPosition = pair.Local.ConstraintPositionWorld();
        Vector3D finalConstraintPosition = pair.Target.ConstraintPositionWorld();
        if (!TryGetGridRotationTarget(pair, lockRotationService, out MatrixD targetOrientation))
            return false;

        double orientationErrorMagnitude = GetOrientationErrorVector(currentGridMatrix, targetOrientation).Length();
        Vector3D targetConstraintPosition = GetApproachConstraintPosition(
            pair.Target,
            currentConstraintPosition,
            finalConstraintPosition,
            orientationErrorMagnitude,
            finalApproachStarted,
            out finalApproachActive);
        if (!TryCreateDockingWorldMatrix(pair, targetOrientation, targetConstraintPosition, out MatrixD targetMatrix))
            return false;

        bool positionReady = Vector3D.DistanceSquared(currentConstraintPosition, finalConstraintPosition)
                             <= AutoDockConstants.AutoDockPositionTolerance * AutoDockConstants.AutoDockPositionTolerance;
        bool angleReady = orientationErrorMagnitude * orientationErrorMagnitude
                          <= AutoDockConstants.AutoDockOrientationTolerance * AutoDockConstants.AutoDockOrientationTolerance;
        target = new DockingTarget(targetMatrix, targetConstraintPosition, positionReady && angleReady, finalApproachActive);
        return true;
    }

    public static bool TryGetGravityTiltWarning(DockingPair pair, LockRotationService lockRotationService, out string warning)
    {
        warning = null;
        if (!TryGetControlledShipController(pair.Local.CubeGrid, out MyShipController controller)
            && !TryGetActiveShipController(pair.Local.CubeGrid, out controller))
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
        DockingTarget target,
        bool allowTranslation = true)
    {
        MyCubeGrid grid = pair.Local.CubeGrid;
        MatrixD currentGridMatrix = grid.PositionComp.WorldMatrixRef;
        Vector3D currentConstraintPosition = pair.Local.ConstraintPositionWorld();
        Vector3D targetVelocity = pair.Target.CubeGrid.Physics.LinearVelocity;
        Vector3D currentVelocity = pair.Local.CubeGrid.Physics.LinearVelocity;
        MatrixD inverseControllerMatrix = controller.PositionComp.WorldMatrixNormalizedInv;

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

        if (Vector3D.Dot(positionErrorIntegral, positionError) < 0.0)
            positionErrorIntegral = Vector3D.Zero;

        positionErrorIntegral = ClampVectorMagnitude(positionErrorIntegral, AutoDockConstants.AutoDockLinearIntegralLimit);

        double maxLinearAcceleration = target.FinalApproachActive
            ? AutoDockConstants.AutoDockFinalApproachMaxLinearAcceleration
            : AutoDockConstants.AutoDockMaxLinearAcceleration;

        Vector3D desiredWorldAcceleration = GetDesiredWorldAcceleration(
            pair.Target,
            positionError,
            positionErrorIntegral,
            errorVelocity,
            maxLinearAcceleration,
            target.FinalApproachActive);
        desiredWorldAcceleration = ClampVectorMagnitude(desiredWorldAcceleration, maxLinearAcceleration);
        Vector3 moveIndicator = allowTranslation
            ? CreateMoveIndicator(desiredWorldAcceleration, inverseControllerMatrix, maxLinearAcceleration)
            : Vector3.Zero;

        orientationErrorIntegral += orientationError * AutoDockConstants.AutoDockControlStepSeconds;
        orientationErrorIntegral = ClampVectorMagnitude(orientationErrorIntegral, AutoDockConstants.AutoDockAngularIntegralLimit);

        Vector3D angularVelocity = grid.Physics.AngularVelocity;
        Vector3D desiredAngularVelocity =
            orientationError * AutoDockConstants.AutoDockAngularProportionalGain
            + orientationErrorIntegral * AutoDockConstants.AutoDockAngularIntegralGain;
        desiredAngularVelocity = ClampVectorMagnitude(desiredAngularVelocity, AutoDockConstants.AutoDockMaxAngularVelocity);

        Vector3D worldTorque = (desiredAngularVelocity - angularVelocity) * AutoDockConstants.AutoDockAngularVelocityGain;
        float torqueLimit = MathHelper.Lerp(
            AutoDockConstants.AutoDockAngularMinTorque,
            AutoDockConstants.AutoDockAngularMaxTorque,
            MathHelper.Clamp((float)(orientationError.Length() / AutoDockConstants.AutoDockAngularSofteningThreshold), 0f, 1f));
        CreateRotationInput(
            worldTorque,
            inverseControllerMatrix,
            torqueLimit,
            out Vector2 rotationIndicator,
            out float rollIndicator);

        AutoDockControlOverride.Set(controller, moveIndicator, rotationIndicator, rollIndicator);
    }

    public static void ReleaseShipControl(MyCubeGrid grid)
    {
        AutoDockControlOverride.Clear();

        if (grid == null)
            return;

        if (TryGetActiveShipController(grid, out MyShipController controller))
        {
            controller.MoveAndRotate(Vector3.Zero, Vector2.Zero, 0f);
            controller.MoveAndRotateStopped();
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

    public static Vector3D GetConnectorDummyCenterPosition(MyShipConnector connector)
    {
        if (connector == null)
            return Vector3D.Zero;

        if (connector.PositionComp == null)
            return Vector3D.Zero;

        if (TryGetConnectorDummyLocalMatrix(connector, out Matrix connectorDummyLocal)
            && connector.CubeGrid?.PositionComp != null)
        {
            return Vector3D.Transform(
                new Vector3D(connectorDummyLocal.Translation.X, connectorDummyLocal.Translation.Y, connectorDummyLocal.Translation.Z),
                connector.CubeGrid.PositionComp.WorldMatrixRef);
        }

        if (ConnectionPositionField?.GetValue(connector) is Vector3 connectionPosition)
            return Vector3D.Transform(connectionPosition, connector.PositionComp.WorldMatrixRef);

        return connector.PositionComp.GetPosition();
    }

    public static Vector3D GetConnectorModelCenterPosition(MyShipConnector connector)
    {
        if (connector == null || connector.PositionComp == null)
            return Vector3D.Zero;

        BoundingBox localAabb = connector.PositionComp.LocalAABB;
        return Vector3D.Transform(localAabb.Center, connector.PositionComp.WorldMatrixRef);
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

        if (!TryGetConnectorFaceWorldMatrix(pair.Target, out MatrixD targetFaceWorldMatrix))
            return false;

        Vector3D baseDirection = RejectFromAxis(targetFaceWorldMatrix.Up, faceNormal);
        if (baseDirection.LengthSquared() < AutoDockConstants.MinConnectorDistanceSquared)
            baseDirection = RejectFromAxis(targetFaceWorldMatrix.Right, faceNormal);
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

        if (!TryGetConnectorFaceWorldMatrix(localConnector, out MatrixD localFaceWorldMatrix))
            return false;

        return TryQuantizeRotationStep(targetConnector, localFaceWorldMatrix.Up, out rotationStep);
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

    private static Vector3D GetApproachConstraintPosition(
        MyShipConnector targetConnector,
        Vector3D currentConstraintPosition,
        Vector3D finalConstraintPosition,
        double orientationErrorMagnitude,
        bool finalApproachStarted,
        out bool finalApproachActive)
    {
        finalApproachActive = finalApproachStarted;
        if (!TryGetDockingApproachAxis(targetConnector, out Vector3D approachAxis))
            return finalConstraintPosition;

        Vector3D finalPositionError = finalConstraintPosition - currentConstraintPosition;
        double signedAxialOffset = Vector3D.Dot(currentConstraintPosition - finalConstraintPosition, approachAxis);
        double approachSign = signedAxialOffset < 0.0 ? -1.0 : 1.0;
        Vector3D axialPositionError = approachAxis * Vector3D.Dot(finalPositionError, approachAxis);
        Vector3D planarPositionError = finalPositionError - axialPositionError;
        double axialDistance = Math.Abs(signedAxialOffset);
        bool finalApproachReady =
            finalApproachStarted
            || (axialDistance <= AutoDockConstants.AutoDockFinalApproachDistance + AutoDockConstants.AutoDockFinalApproachEntryDistanceTolerance
                && planarPositionError.LengthSquared()
                <= AutoDockConstants.AutoDockFinalApproachPlanarTolerance * AutoDockConstants.AutoDockFinalApproachPlanarTolerance
                && orientationErrorMagnitude <= AutoDockConstants.AutoDockFinalApproachOrientationThreshold);

        finalApproachActive = finalApproachReady;
        // Hold staging point 2 m out on same side of connector as ship until lateral alignment is done.
        return finalApproachReady
            ? finalConstraintPosition
            : finalConstraintPosition + approachAxis * (approachSign * AutoDockConstants.AutoDockFinalApproachDistance);
    }

    private static bool IsFacingTowards(MyShipConnector connector, Vector3D otherConnectorPosition)
    {
        if (connector == null || connector.MarkedForClose || connector.PositionComp == null)
            return false;

        Vector3D dummyCenter = GetConnectorDummyCenterPosition(connector);
        Vector3D modelCenter = GetConnectorModelCenterPosition(connector);
        double dummyDistanceSquared = Vector3D.DistanceSquared(dummyCenter, otherConnectorPosition);
        double modelDistanceSquared = Vector3D.DistanceSquared(modelCenter, otherConnectorPosition);
        return dummyDistanceSquared + AutoDockConstants.MinConnectorDistanceSquared < modelDistanceSquared;
    }

    private static bool TryCreateDockingWorldMatrix(
        DockingPair pair,
        MatrixD targetOrientation,
        Vector3D desiredConstraintPosition,
        out MatrixD targetMatrix)
    {
        targetMatrix = MatrixD.Identity;
        MyCubeGrid grid = pair.Local.CubeGrid;
        MatrixD currentGridMatrix = grid.PositionComp.WorldMatrixRef;

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

    private static bool TryGetLocalConnectorGridMatrix(DockingPair pair, out MatrixD localConnectorGridMatrix)
    {
        localConnectorGridMatrix = MatrixD.Identity;
        if (pair?.Local == null || pair.Local.CubeGrid == null)
            return false;

        MatrixD currentGridMatrix = pair.Local.CubeGrid.PositionComp.WorldMatrixRef;
        currentGridMatrix.Translation = Vector3D.Zero;
        MatrixD inverseGridMatrix = MatrixD.Invert(currentGridMatrix);

        if (!TryGetConnectorFaceWorldMatrix(pair.Local, out MatrixD localConnectorWorldMatrix))
            return false;

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

        localReferenceForward = Vector3D.TransformNormal(GetReferenceAlignmentForward(grid), inverseGridMatrix);
        localReferenceUp = Vector3D.TransformNormal(GetReferenceAlignmentUp(grid), inverseGridMatrix);
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
                   GetReferenceAlignmentForward(pair.Local.CubeGrid),
                   GetReferenceAlignmentUp(pair.Local.CubeGrid),
                   out _,
                   out direction,
                   out _);
    }

    private static Vector3D GetReferenceAlignmentUp(MyCubeGrid grid)
    {
        return TryGetControlledShipController(grid, out MyShipController controller)
            ? controller.WorldMatrix.Up
            : grid.WorldMatrix.Up;
    }

    private static Vector3D GetReferenceAlignmentForward(MyCubeGrid grid)
    {
        return TryGetControlledShipController(grid, out MyShipController controller)
            ? controller.WorldMatrix.Forward
            : grid.WorldMatrix.Forward;
    }

    private static MatrixD GetDesiredControllerWorldMatrix(MyShipController controller, MatrixD targetGridMatrix)
    {
        MatrixD controllerLocalMatrix = controller.PositionComp.LocalMatrixRef;
        return controllerLocalMatrix * targetGridMatrix;
    }

    private static bool TryGetRotationBasis(MyShipConnector targetConnector, out Vector3D desiredForward, out Vector3D baseUp)
    {
        desiredForward = Vector3D.Zero;
        baseUp = Vector3D.Zero;
        if (!TryGetConnectorFaceWorldMatrix(targetConnector, out MatrixD targetFaceWorldMatrix))
            return false;

        desiredForward = -targetFaceWorldMatrix.Forward;
        if (desiredForward.LengthSquared() < AutoDockConstants.MinConnectorDistanceSquared)
            return false;

        desiredForward.Normalize();
        baseUp = RejectFromAxis(targetFaceWorldMatrix.Up, desiredForward);
        if (baseUp.LengthSquared() < AutoDockConstants.MinConnectorDistanceSquared)
            baseUp = RejectFromAxis(targetFaceWorldMatrix.Right, desiredForward);
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
        if (!TryGetConnectorFaceWorldMatrix(targetConnector, out MatrixD targetFaceWorldMatrix))
            return false;

        faceNormal = targetFaceWorldMatrix.Forward;
        if (faceNormal.LengthSquared() < AutoDockConstants.MinConnectorDistanceSquared)
            return false;

        faceNormal.Normalize();
        direction = RejectFromAxis(desiredReferenceForward, faceNormal);
        if (direction.LengthSquared() < AutoDockConstants.MinConnectorDistanceSquared)
            direction = RejectFromAxis(desiredReferenceUp, faceNormal);
        if (direction.LengthSquared() < AutoDockConstants.MinConnectorDistanceSquared)
            direction = RejectFromAxis(targetFaceWorldMatrix.Up, faceNormal);
        if (direction.LengthSquared() < AutoDockConstants.MinConnectorDistanceSquared)
            direction = RejectFromAxis(targetFaceWorldMatrix.Right, faceNormal);
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
        double maxLinearAcceleration,
        bool finalApproachActive)
    {
        if (!TryGetDockingApproachAxis(targetConnector, out Vector3D approachAxis))
        {
            return GetStabilizedAcceleration(positionError, positionErrorIntegral, errorVelocity, maxLinearAcceleration);
        }

        Vector3D axialPositionError = approachAxis * Vector3D.Dot(positionError, approachAxis);
        Vector3D planarPositionError = positionError - axialPositionError;
        Vector3D axialIntegralError = approachAxis * Vector3D.Dot(positionErrorIntegral, approachAxis);
        Vector3D planarIntegralError = positionErrorIntegral - axialIntegralError;
        Vector3D axialVelocityError = approachAxis * Vector3D.Dot(errorVelocity, approachAxis);
        Vector3D planarVelocityError = errorVelocity - axialVelocityError;
        return GetStabilizedAcceleration(
                   axialPositionError,
                   axialIntegralError,
                   axialVelocityError,
                   maxLinearAcceleration,
                   enforceStoppingDistance: !finalApproachActive)
               + GetStabilizedAcceleration(planarPositionError, planarIntegralError, planarVelocityError, maxLinearAcceleration);
    }

    private static Vector3D GetStabilizedAcceleration(
        Vector3D positionError,
        Vector3D positionErrorIntegral,
        Vector3D errorVelocity,
        double maxLinearAcceleration,
        bool enforceStoppingDistance = true)
    {
        Vector3D desiredAcceleration =
            positionError * AutoDockConstants.AutoDockLinearProportionalGain
            + positionErrorIntegral * AutoDockConstants.AutoDockLinearIntegralGain
            + errorVelocity * AutoDockConstants.AutoDockLinearDerivativeGain;

        if (!enforceStoppingDistance)
            return desiredAcceleration;

        double distanceSquared = positionError.LengthSquared();
        if (distanceSquared < AutoDockConstants.MinConnectorDistanceSquared)
            return desiredAcceleration;

        double distance = Math.Sqrt(distanceSquared);
        Vector3D errorDirection = positionError / distance;
        double errorVelocityAlongError = Vector3D.Dot(errorVelocity, errorDirection);
        double closingSpeed = -errorVelocityAlongError;
        double stoppingDistance = Math.Max(0.0, distance - AutoDockConstants.AutoDockLinearBrakePadding);
        double maxClosingSpeed = Math.Sqrt(2.0 * maxLinearAcceleration * stoppingDistance);
        if (closingSpeed <= maxClosingSpeed)
            return desiredAcceleration;

        // Too much closing speed for remaining distance. Spend full authority on braking.
        Vector3D lateralAcceleration = desiredAcceleration - errorDirection * Vector3D.Dot(desiredAcceleration, errorDirection);
        return lateralAcceleration - errorDirection * maxLinearAcceleration;
    }

    private static bool TryGetDockingApproachAxis(MyShipConnector targetConnector, out Vector3D approachAxis)
    {
        approachAxis = Vector3D.Zero;
        if (!TryGetConnectorFaceWorldMatrix(targetConnector, out MatrixD targetFaceWorldMatrix))
            return false;

        approachAxis = targetFaceWorldMatrix.Forward;
        if (approachAxis.LengthSquared() < AutoDockConstants.MinConnectorDistanceSquared)
            return false;

        approachAxis.Normalize();
        return true;
    }

    private static bool TryGetConnectorDummyLocalMatrix(MyShipConnector connector, out Matrix connectorDummyLocal)
    {
        connectorDummyLocal = Matrix.Identity;
        if (connector == null)
            return false;

        if (!(ConnectorDummyLocalField?.GetValue(connector) is Matrix reflectedDummyLocal))
            return false;

        connectorDummyLocal = reflectedDummyLocal;
        return true;
    }

    private static bool TryGetConnectorFaceWorldMatrix(MyShipConnector connector, out MatrixD connectorFaceWorldMatrix)
    {
        connectorFaceWorldMatrix = MatrixD.Identity;
        if (connector == null || connector.MarkedForClose || connector.PositionComp == null)
            return false;

        Vector3D faceNormal = GetConnectorDummyCenterPosition(connector) - GetConnectorModelCenterPosition(connector);
        Vector3D upCandidate = connector.WorldMatrix.Up;
        Vector3D rightCandidate = connector.WorldMatrix.Right;

        if (TryGetConnectorDummyLocalMatrix(connector, out Matrix connectorDummyLocal)
            && connector.CubeGrid?.PositionComp != null)
        {
            MatrixD gridWorldMatrix = connector.CubeGrid.PositionComp.WorldMatrixRef;
            upCandidate = Vector3D.TransformNormal(
                new Vector3D(connectorDummyLocal.Up.X, connectorDummyLocal.Up.Y, connectorDummyLocal.Up.Z),
                gridWorldMatrix);
            rightCandidate = Vector3D.TransformNormal(
                new Vector3D(connectorDummyLocal.Right.X, connectorDummyLocal.Right.Y, connectorDummyLocal.Right.Z),
                gridWorldMatrix);
            if (faceNormal.LengthSquared() < AutoDockConstants.MinConnectorDistanceSquared)
            {
                faceNormal = Vector3D.TransformNormal(
                    new Vector3D(connectorDummyLocal.Forward.X, connectorDummyLocal.Forward.Y, connectorDummyLocal.Forward.Z),
                    gridWorldMatrix);
            }
        }

        if (faceNormal.LengthSquared() < AutoDockConstants.MinConnectorDistanceSquared)
            faceNormal = connector.WorldMatrix.Forward;
        if (faceNormal.LengthSquared() < AutoDockConstants.MinConnectorDistanceSquared)
            return false;

        faceNormal.Normalize();
        Vector3D connectorUp = RejectFromAxis(upCandidate, faceNormal);
        if (connectorUp.LengthSquared() < AutoDockConstants.MinConnectorDistanceSquared)
            connectorUp = RejectFromAxis(rightCandidate, faceNormal);
        if (connectorUp.LengthSquared() < AutoDockConstants.MinConnectorDistanceSquared)
            connectorUp = RejectFromAxis(connector.WorldMatrix.Up, faceNormal);
        if (connectorUp.LengthSquared() < AutoDockConstants.MinConnectorDistanceSquared)
            connectorUp = RejectFromAxis(connector.WorldMatrix.Right, faceNormal);
        if (connectorUp.LengthSquared() < AutoDockConstants.MinConnectorDistanceSquared)
            return false;

        connectorUp.Normalize();
        connectorFaceWorldMatrix = MatrixD.CreateWorld(Vector3D.Zero, faceNormal, connectorUp);
        connectorFaceWorldMatrix.Translation = Vector3D.Zero;
        return connectorFaceWorldMatrix.IsValid();
    }

    private static Vector3 CreateMoveIndicator(
        Vector3D desiredWorldAcceleration,
        MatrixD inverseControllerMatrix,
        double maxLinearAcceleration)
    {
        if (maxLinearAcceleration <= 0.0)
            return Vector3.Zero;

        Vector3D desiredControllerAcceleration = Vector3D.TransformNormal(desiredWorldAcceleration, inverseControllerMatrix);
        Vector3 moveIndicator = new Vector3(
            (float)(desiredControllerAcceleration.X / maxLinearAcceleration),
            (float)(desiredControllerAcceleration.Y / maxLinearAcceleration),
            (float)(desiredControllerAcceleration.Z / maxLinearAcceleration));
        return Vector3.Clamp(moveIndicator, -Vector3.One, Vector3.One);
    }

    private static void CreateRotationInput(
        Vector3D desiredWorldTorque,
        MatrixD inverseControllerMatrix,
        float torqueLimit,
        out Vector2 rotationIndicator,
        out float rollIndicator)
    {
        Vector3D desiredControllerTorqueD = Vector3D.TransformNormal(desiredWorldTorque, inverseControllerMatrix);
        Vector3 desiredControllerTorque = new Vector3(
            (float)desiredControllerTorqueD.X,
            (float)desiredControllerTorqueD.Y,
            (float)desiredControllerTorqueD.Z);
        desiredControllerTorque = Vector3.ClampToSphere(desiredControllerTorque, torqueLimit);

        rotationIndicator = new Vector2(-desiredControllerTorque.X * 20f, -desiredControllerTorque.Y * 20f);
        rollIndicator = -desiredControllerTorque.Z * 5f;
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
