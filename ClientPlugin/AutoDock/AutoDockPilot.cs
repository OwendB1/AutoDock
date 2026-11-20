using System;
using Sandbox.Game.Entities;
using Sandbox.ModAPI.Ingame;
using VRageMath;

namespace ClientPlugin;

internal sealed class AutoDockPilot
{
    private readonly LockRotationService lockRotationService;
    private DockingPair autoDockingPair;
    private int autoDockFrames;
    private bool autoDockConnectRequested;
    private bool autoDockWaitingForLockNotified;
    private int autoDockLockReadyFrames;
    private bool autoDockFinalApproachStarted;
    private Vector3D autoDockPositionErrorIntegral;
    private AngularPidControl autoDockAngularControl;
    private int stalledFrames;
    private double bestConnectorDistance;
    private double bestOrientationMismatch;
    private int connectRequestFrame;
    private int connectAttempts;
    private double maxAxialReach = double.PositiveInfinity;
    private int reachMeasuredFrame;
    private bool clearanceLimitNotified;

    public AutoDockPilot(LockRotationService lockRotationService)
    {
        this.lockRotationService = lockRotationService;
    }

    public bool IsActive => autoDockingPair != null;
    public DockingPair ActivePair => autoDockingPair;

    public void Start(DockingPair pair)
    {
        lockRotationService.RememberSelectedRotation(pair);
        autoDockingPair = pair;
        autoDockFrames = 0;
        autoDockConnectRequested = false;
        autoDockWaitingForLockNotified = false;
        autoDockLockReadyFrames = 0;
        autoDockFinalApproachStarted = false;
        autoDockPositionErrorIntegral = Vector3D.Zero;
        autoDockAngularControl = default;
        ResetProgressState();
    }

    public AutoDockPilotUpdateResult Cancel(string message, string font)
    {
        ResetState(releaseControl: true);
        return new AutoDockPilotUpdateResult(AutoDockPilotStatus.Cancelled, message, font);
    }

    public void Reset()
    {
        ResetState(releaseControl: true);
    }

    public AutoDockPilotUpdateResult Update()
    {
        DockingPair pair = autoDockingPair;
        if (pair == null)
            return AutoDockPilotUpdateResult.Running;

        if (DockingMath.HasManualFlightInput())
            return Cancel("AutoDock: cancelled by manual flight input.", "White");

        if (DockingMath.IsDocked(pair))
            return Complete("AutoDock: connector lock succeeded.", "Green");

        if (!DockingMath.IsPairStillUsable(pair))
            return Cancel("AutoDock: connector pair became unavailable.", "Red");

        pair.RefreshMetrics();
        autoDockFrames++;
        if (autoDockFrames > AutoDockConstants.AutoDockHardTimeoutFrames)
            return Cancel("AutoDock: automatic docking timed out.", "Red");

        bool lockReadyAtStart = pair.LockReady;
        if (lockReadyAtStart)
        {
            if (TryRequestDockLock(pair, out AutoDockPilotUpdateResult result))
                return result;
        }
        else
        {
            autoDockLockReadyFrames = 0;
        }

        if (!DockingMath.TryCreateDockingTarget(
                pair,
                lockRotationService,
                autoDockFinalApproachStarted,
                out DockingTarget target,
                out bool finalApproachStarted,
                maxAxialReach))
        {
            if (lockReadyAtStart)
                return AutoDockPilotUpdateResult.Running;

            return Cancel("AutoDock: cannot calculate docking position.", "Red");
        }

        if (AutoDockConstants.RetreatObstacleCheck && (target.Retreating || target.ClearanceLimited)
            && autoDockFrames - reachMeasuredFrame >= ReachMeasureIntervalFrames)
        {
            // Check the retreat path for rock or grids before backing away (caves, tight hangars).
            reachMeasuredFrame = autoDockFrames;
            double free = DockingMath.MeasureFreeDistance(pair.Local.CubeGrid, target.RetreatDirection, MaxRetreatProbeDistance);
            maxAxialReach = target.AxialDistance + Math.Max(0.0, free - AutoDockConstants.RetreatObstacleMargin);
            if (DockingMath.TryCreateDockingTarget(pair, lockRotationService, autoDockFinalApproachStarted,
                    out DockingTarget limitedTarget, out bool limitedFinalApproach, maxAxialReach))
            {
                target = limitedTarget;
                finalApproachStarted = limitedFinalApproach;
            }
        }

        autoDockFinalApproachStarted = finalApproachStarted;

        DockingMath.GetProgressSample(pair, target, out double connectorDistance, out double orientationMismatch,
            out double relativeSpeed, out double angularSpeed);
        if (!UpdateProgress(connectorDistance, orientationMismatch, relativeSpeed, angularSpeed))
            return Cancel("AutoDock: no docking progress, giving up.", "Red");

        if (!DockingMath.TryGetControlledShipController(pair.Local.CubeGrid, out MyShipController controller))
        {
            return Cancel("AutoDock: controlled ship controller required on selected grid.", "Red");
        }

        DockingMath.ApplyAutoDockControl(
            ref autoDockPositionErrorIntegral,
            ref autoDockAngularControl,
            pair,
            controller,
            target,
            allowTranslation: !lockReadyAtStart);

        if (!lockReadyAtStart)
        {
            pair.RefreshMetrics();
            if (pair.LockReady)
            {
                if (TryRequestDockLock(pair, out AutoDockPilotUpdateResult result))
                    return result;
            }
            else
            {
                autoDockLockReadyFrames = 0;
            }
        }

        if (target.ClearanceLimited && !clearanceLimitNotified)
        {
            clearanceLimitNotified = true;
            return new AutoDockPilotUpdateResult(AutoDockPilotStatus.Running, "AutoDock: obstacle behind the ship, turning slowly in place.", "White");
        }

        if (target.DockingReady && !pair.LockReady && !autoDockWaitingForLockNotified)
        {
            autoDockWaitingForLockNotified = true;
            return new AutoDockPilotUpdateResult(AutoDockPilotStatus.Running, "AutoDock: aligned. Waiting for connector lock range.", "White");
        }

        return AutoDockPilotUpdateResult.Running;
    }

    private bool TryRequestDockLock(DockingPair pair, out AutoDockPilotUpdateResult result)
    {
        if (autoDockConnectRequested)
        {
            // A single request can be lost or rejected in multiplayer; retry instead of waiting for the timeout.
            if (autoDockFrames - connectRequestFrame < AutoDockConstants.AutoDockConnectRetryFrames)
            {
                result = AutoDockPilotUpdateResult.Running;
                return true;
            }

            if (connectAttempts >= AutoDockConstants.AutoDockMaxConnectAttempts)
            {
                result = Cancel("AutoDock: connector did not lock after several attempts.", "Red");
                return true;
            }

            autoDockConnectRequested = false;
            autoDockLockReadyFrames = AutoDockConstants.AutoDockLockDelayFrames;
        }

        autoDockLockReadyFrames++;
        if (autoDockLockReadyFrames < AutoDockConstants.AutoDockLockDelayFrames)
        {
            result = AutoDockPilotUpdateResult.Running;
            return false;
        }

        autoDockConnectRequested = true;
        connectRequestFrame = autoDockFrames;
        connectAttempts++;
        DockingMath.ReleaseShipControl(pair.Local.CubeGrid);
        ((IMyShipConnector)pair.Local).Connect();
        pair.RefreshMetrics();
        if (DockingMath.IsDocked(pair))
        {
            result = Complete("AutoDock: connector lock succeeded.", "Green");
            return true;
        }

        result = new AutoDockPilotUpdateResult(AutoDockPilotStatus.Running, "AutoDock: connector lock requested.", "Green");
        return true;
    }

    private AutoDockPilotUpdateResult Complete(string message, string font)
    {
        ResetState(releaseControl: true);
        return new AutoDockPilotUpdateResult(AutoDockPilotStatus.Completed, message, font);
    }

    private void ResetState(bool releaseControl)
    {
        if (releaseControl)
            DockingMath.ReleaseShipControl(autoDockingPair?.Local?.CubeGrid);

        autoDockingPair = null;
        autoDockFrames = 0;
        autoDockConnectRequested = false;
        autoDockWaitingForLockNotified = false;
        autoDockLockReadyFrames = 0;
        autoDockFinalApproachStarted = false;
        autoDockPositionErrorIntegral = Vector3D.Zero;
        autoDockAngularControl = default;
        ResetProgressState();
    }

    private const int ReachMeasureIntervalFrames = 20;
    private const double MaxRetreatProbeDistance = 200.0;

    private void ResetProgressState()
    {
        stalledFrames = 0;
        bestConnectorDistance = double.MaxValue;
        bestOrientationMismatch = double.MaxValue;
        connectRequestFrame = 0;
        connectAttempts = 0;
        maxAxialReach = double.PositiveInfinity;
        reachMeasuredFrame = -ReachMeasureIntervalFrames;
        clearanceLimitNotified = false;
    }

    /// <summary>
    /// Counts frames without progress. Progress means moving or turning, or getting closer to the target
    /// connector / orientation. Returns false once the no-progress timeout is exceeded.
    /// </summary>
    private bool UpdateProgress(double connectorDistance, double orientationMismatch, double relativeSpeed, double angularSpeed)
    {
        bool improved = false;
        if (connectorDistance < bestConnectorDistance - 0.05)
        {
            bestConnectorDistance = connectorDistance;
            improved = true;
        }
        if (orientationMismatch < bestOrientationMismatch - 0.01)
        {
            bestOrientationMismatch = orientationMismatch;
            improved = true;
        }

        if (improved || relativeSpeed > 0.15 || angularSpeed > 0.02)
            stalledFrames = 0;
        else
            stalledFrames++;

        return stalledFrames <= AutoDockConstants.AutoDockTimeoutFrames;
    }
}
