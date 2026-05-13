using System.Collections.Generic;
using Sandbox.Game.Entities;
using SpaceEngineers.Game.Entities.Blocks;
using SpaceEngineers.Game.ModAPI.Ingame;
using VRageMath;

namespace ClientPlugin;

internal sealed class AutoLandingPilot
{
    private MyCubeGrid landingGrid;
    private AutoLandingPlan currentPlan;
    private int landingFrames;
    private int postLockFrames;
    private bool lockAttemptNotified;
    private bool waitingForContactNotified;
    private Vector3D positionErrorIntegral;
    private Vector3D orientationErrorIntegral;
    private readonly Dictionary<long, MyLandingGear> suppressedAutoLockGears = new Dictionary<long, MyLandingGear>();

    public bool IsActive => landingGrid != null;
    public AutoLandingPlan CurrentPlan => currentPlan;

    public void Start(AutoLandingPlan plan)
    {
        RestoreLandingGearAutoLock();
        landingGrid = plan?.Grid;
        currentPlan = plan;
        landingFrames = 0;
        postLockFrames = 0;
        lockAttemptNotified = false;
        waitingForContactNotified = false;
        positionErrorIntegral = Vector3D.Zero;
        orientationErrorIntegral = Vector3D.Zero;
        EnsureLandingGearAutoLockDisabled(plan?.Gears);
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
        if (landingGrid == null)
            return AutoDockPilotUpdateResult.Running;

        AutoLandingPlan plan = currentPlan;
        if (plan == null)
            return Cancel("AutoDock: landing plan was lost.", "Red");

        landingFrames++;
        if (landingFrames > AutoDockConstants.AutoLandingTimeoutFrames)
            return Cancel("AutoDock: automatic landing timed out.", "Red");

        int lockedGearCount = CountGearsWithMode(LandingGearMode.Locked);
        if (lockedGearCount == 0 && DockingMath.HasManualFlightInput())
            return Cancel("AutoDock: landing cancelled by manual flight input.", "White");

        if (lockedGearCount > 0)
        {
            RequestLandingGearLocks();
            postLockFrames++;
            if (postLockFrames >= AutoDockConstants.AutoLandingPostLockFrames)
                return Complete($"AutoDock: landing locked on {lockedGearCount} gear(s).", "Green");

            return AutoDockPilotUpdateResult.Running;
        }

        if (plan.ExpectedReadyGearCount <= 0)
            return Cancel("AutoDock: terrain under gear footprint is not landable.", "Red");

        if (!DockingMath.TryGetControlledShipController(landingGrid, out MyShipController controller))
            return Cancel("AutoDock: controlled ship controller required on selected grid.", "Red");

        Vector3D currentAnchorWorldPosition = plan.GetCurrentAnchorWorldPosition();
        double anchorDistance = Vector3D.Distance(currentAnchorWorldPosition, plan.TargetAnchorWorldPosition);
        int readyGearCount = CountGearsWithMode(LandingGearMode.ReadyToLock);
        int requiredGearCount = plan.ExpectedReadyGearCount;
        if (readyGearCount >= requiredGearCount
            || (readyGearCount > 0 && anchorDistance <= AutoDockConstants.AutoLandingContactTolerance))
        {
            RequestLandingGearLocks();
            if (!lockAttemptNotified)
            {
                lockAttemptNotified = true;
                return new AutoDockPilotUpdateResult(AutoDockPilotStatus.Running, "AutoDock: landing contact ready. Locking landing gear.", "White");
            }
        }

        DockingMath.ApplyGridAlignmentControl(
            ref positionErrorIntegral,
            ref orientationErrorIntegral,
            landingGrid,
            controller,
            currentAnchorWorldPosition,
            plan.TargetAnchorWorldPosition,
            plan.TargetGridMatrix,
            Vector3D.Zero,
            AutoDockConstants.AutoLandingMaxLinearAcceleration,
            AutoDockConstants.AutoLandingMaxAngularVelocity);

        if (anchorDistance <= AutoDockConstants.AutoLandingContactTolerance && !waitingForContactNotified)
        {
            waitingForContactNotified = true;
            return new AutoDockPilotUpdateResult(AutoDockPilotStatus.Running, "AutoDock: aligned. Waiting for landing gear contact.", "White");
        }

        return AutoDockPilotUpdateResult.Running;
    }

    private void RequestLandingGearLocks()
    {
        if (currentPlan?.Gears == null)
            return;

        for (int i = 0; i < currentPlan.Gears.Count; i++)
        {
            MyLandingGear gear = currentPlan.Gears[i];
            if (gear == null || gear.MarkedForClose || !gear.IsWorking || gear.LockMode == LandingGearMode.Locked)
                continue;

            ((IMyLandingGear)gear).Lock();
        }
    }

    private int CountGearsWithMode(LandingGearMode mode)
    {
        if (currentPlan?.Gears == null)
            return 0;

        int count = 0;
        for (int i = 0; i < currentPlan.Gears.Count; i++)
        {
            MyLandingGear gear = currentPlan.Gears[i];
            if (gear != null && !gear.MarkedForClose && gear.LockMode == mode)
                count++;
        }

        return count;
    }

    private void EnsureLandingGearAutoLockDisabled(IReadOnlyList<MyLandingGear> gears)
    {
        if (gears == null)
            return;

        for (int i = 0; i < gears.Count; i++)
        {
            MyLandingGear gear = gears[i];
            if (gear == null || gear.MarkedForClose)
                continue;

            IMyLandingGear landingGear = gear;
            if (!landingGear.AutoLock)
                continue;

            suppressedAutoLockGears[gear.EntityId] = gear;
            landingGear.AutoLock = false;
        }
    }

    private void RestoreLandingGearAutoLock()
    {
        if (suppressedAutoLockGears.Count == 0)
            return;

        foreach (MyLandingGear gear in suppressedAutoLockGears.Values)
        {
            if (gear == null || gear.MarkedForClose)
                continue;

            ((IMyLandingGear)gear).AutoLock = true;
        }

        suppressedAutoLockGears.Clear();
    }

    private AutoDockPilotUpdateResult Complete(string message, string font)
    {
        ResetState(releaseControl: true);
        return new AutoDockPilotUpdateResult(AutoDockPilotStatus.Completed, message, font);
    }

    private void ResetState(bool releaseControl)
    {
        if (releaseControl)
            DockingMath.ReleaseShipControl(landingGrid);

        RestoreLandingGearAutoLock();
        landingGrid = null;
        currentPlan = null;
        landingFrames = 0;
        postLockFrames = 0;
        lockAttemptNotified = false;
        waitingForContactNotified = false;
        positionErrorIntegral = Vector3D.Zero;
        orientationErrorIntegral = Vector3D.Zero;
    }
}
