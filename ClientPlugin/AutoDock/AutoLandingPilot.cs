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

    public bool IsActive => landingGrid != null;
    public AutoLandingPlan CurrentPlan => currentPlan;

    public void Start(AutoLandingPlan plan)
    {
        landingGrid = plan?.Grid;
        currentPlan = plan;
        landingFrames = 0;
        postLockFrames = 0;
        lockAttemptNotified = false;
        waitingForContactNotified = false;
        positionErrorIntegral = Vector3D.Zero;
        orientationErrorIntegral = Vector3D.Zero;
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

        if (!AutoLandingPlanner.TryCreatePlan(out AutoLandingPlan plan, out string error))
            return Cancel(error ?? "AutoDock: cannot calculate landing path.", "Red");

        currentPlan = plan;
        if (!plan.HullClearanceOk)
            return Cancel("AutoDock: landing path would intersect terrain with hull.", "Red");

        if (plan.ExpectedReadyGearCount <= 0)
            return Cancel("AutoDock: terrain under gear footprint is not landable.", "Red");

        if (!DockingMath.TryGetControlledShipController(landingGrid, out MyShipController controller))
            return Cancel("AutoDock: controlled ship controller required on selected grid.", "Red");

        int readyGearCount = CountGearsWithMode(LandingGearMode.ReadyToLock);
        int requiredGearCount = plan.ExpectedReadyGearCount;
        if (readyGearCount >= requiredGearCount
            || (readyGearCount > 0 && plan.AnchorDistance <= AutoDockConstants.AutoLandingContactTolerance))
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
            plan.CurrentAnchorWorldPosition,
            plan.TargetAnchorWorldPosition,
            plan.TargetGridMatrix,
            Vector3D.Zero,
            AutoDockConstants.AutoLandingMaxLinearAcceleration,
            AutoDockConstants.AutoLandingMaxAngularVelocity);

        if (plan.AnchorDistance <= AutoDockConstants.AutoLandingContactTolerance && !waitingForContactNotified)
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

    private AutoDockPilotUpdateResult Complete(string message, string font)
    {
        ResetState(releaseControl: true);
        return new AutoDockPilotUpdateResult(AutoDockPilotStatus.Completed, message, font);
    }

    private void ResetState(bool releaseControl)
    {
        if (releaseControl)
            DockingMath.ReleaseShipControl(landingGrid);

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
