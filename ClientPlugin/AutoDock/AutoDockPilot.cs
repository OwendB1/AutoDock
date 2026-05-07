using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
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
    private Vector3D autoDockPositionErrorIntegral;
    private Vector3D autoDockOrientationErrorIntegral;

    public AutoDockPilot(LockRotationService lockRotationService)
    {
        this.lockRotationService = lockRotationService;
    }

    public bool IsActive => autoDockingPair != null;
    public DockingPair ActivePair => autoDockingPair;

    public void Start(DockingPair pair)
    {
        autoDockingPair = pair;
        autoDockFrames = 0;
        autoDockConnectRequested = false;
        autoDockWaitingForLockNotified = false;
        autoDockLockReadyFrames = 0;
        autoDockPositionErrorIntegral = Vector3D.Zero;
        autoDockOrientationErrorIntegral = Vector3D.Zero;
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
        if (autoDockFrames > AutoDockConstants.AutoDockTimeoutFrames)
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

        if (!DockingMath.TryCreateDockingTarget(pair, lockRotationService, out DockingTarget target))
        {
            if (lockReadyAtStart)
                return AutoDockPilotUpdateResult.Running;

            return Cancel("AutoDock: cannot calculate docking position.", "Red");
        }

        if (!DockingMath.TryGetActiveShipController(pair.Local.CubeGrid, out MyShipController controller))
        {
            if (lockReadyAtStart)
                return AutoDockPilotUpdateResult.Running;

            return Cancel("AutoDock: no active ship controller on selected grid.", "Red");
        }

        DockingMath.ApplyAutoDockControl(
            ref autoDockPositionErrorIntegral,
            ref autoDockOrientationErrorIntegral,
            pair,
            controller,
            target);

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
            result = AutoDockPilotUpdateResult.Running;
            return true;
        }

        autoDockLockReadyFrames++;
        if (autoDockLockReadyFrames < AutoDockConstants.AutoDockLockDelayFrames)
        {
            result = AutoDockPilotUpdateResult.Running;
            return false;
        }

        autoDockConnectRequested = true;
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
        autoDockPositionErrorIntegral = Vector3D.Zero;
        autoDockOrientationErrorIntegral = Vector3D.Zero;
    }
}
