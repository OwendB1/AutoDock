using System;
using Sandbox.Game.Gui;
using Sandbox.Game.World;
using Sandbox.Graphics.GUI;
using VRage.Input;

namespace ClientPlugin;

internal sealed class AutoDockController
{
    private readonly LockRotationService lockRotationService;
    private readonly DockingPairCatalog pairCatalog;
    private readonly AutoDockPilot autoDockPilot;
    private readonly AutoLandingPilot autoLandingPilot;
    private readonly AutoDockInput autoDockInput;

    private bool dockingPreviewActive;
    private bool landingPreviewActive;
    private int framesUntilRescan;
    private AutoLandingPlan currentLandingPlan;

    public AutoDockController()
    {
        lockRotationService = new LockRotationService();
        pairCatalog = new DockingPairCatalog(lockRotationService);
        autoDockPilot = new AutoDockPilot(lockRotationService);
        autoLandingPilot = new AutoLandingPilot();
        autoDockInput = new AutoDockInput();
    }

    public void Update()
    {
        AutoDockControlOverride.BeginFrame();

        if (MySession.Static == null)
        {
            ResetSessionState();
            return;
        }

        if (MyInput.Static == null)
        {
            ClearSelection();
            return;
        }

        IMyInput input = MyInput.Static;
        autoDockInput.EnsureRegistered(input);

        if (lockRotationService.TryGetLockedPair(out DockingPair lockedPair))
        {
            lockRotationService.RememberCurrentRotation(lockedPair);
            pairCatalog.RememberPreferredConnector(lockedPair.Local);
        }

        MyGuiScreenBase screenWithFocus = MyScreenManager.GetScreenWithFocus();
        if (screenWithFocus != null && screenWithFocus != MyGuiScreenGamePlay.Static)
        {
            DrawSelections();
            return;
        }

        if (autoDockInput.IsLandingActivationPressed(input))
        {
            if (autoDockPilot.IsActive)
            {
                Notify("AutoDock: connector docking active. Cancel it before auto-landing.", "Red");
            }
            else if (autoLandingPilot.IsActive)
            {
                HandleAutoLandingResult(autoLandingPilot.Cancel("AutoDock: automatic landing cancelled.", "White"));
            }
            else
            {
                HandleLandingActivationPressed();
            }

            DrawSelections();
            return;
        }

        if (autoDockInput.IsActivationPressed(input))
        {
            if (autoLandingPilot.IsActive)
            {
                Notify("AutoDock: auto-landing active. Alt+P cancels landing.", "Red");
            }
            else if (autoDockPilot.IsActive)
            {
                HandleAutoDockResult(autoDockPilot.Cancel("AutoDock: automatic docking cancelled.", "White"));
            }
            else
            {
                HandleDockActivationPressed();
            }

            DrawSelections();
            return;
        }

        if (autoDockPilot.IsActive)
        {
            HandleAutoDockResult(autoDockPilot.Update());
            DrawSelections();
            return;
        }

        if (autoLandingPilot.IsActive)
        {
            HandleAutoLandingResult(autoLandingPilot.Update());
            DrawSelections();
            return;
        }

        if (landingPreviewActive)
            RefreshLandingPreview(notifyOnFailure: false);

        if (!dockingPreviewActive)
        {
            DrawSelections();
            return;
        }

        framesUntilRescan--;
        if (framesUntilRescan <= 0)
        {
            pairCatalog.RescanPairs(preserveSelection: true);
            framesUntilRescan = AutoDockConstants.RescanIntervalFrames;

            if (pairCatalog.PairCount == 0)
            {
                ClearDockSelection();
                Notify($"AutoDock: no connector pairs within {AutoDockConstants.SoftSelectRadius:0.#} m.", "Red");
                DrawSelections();
                return;
            }
        }

        if (autoDockInput.IsRotateAlignmentPressed(input))
        {
            HandleRotateAlignmentPressed();
        }
        else if (autoDockInput.IsCyclePreviousConnectorPressed(input))
        {
            if (pairCatalog.CycleConnector(-1))
                NotifySelection();
        }
        else if (autoDockInput.IsCycleNextConnectorPressed(input))
        {
            if (pairCatalog.CycleConnector(1))
                NotifySelection();
        }
        else if (autoDockInput.IsCyclePreviousPressed(input))
        {
            if (pairCatalog.CyclePair(-1))
                NotifySelection();
        }
        else if (autoDockInput.IsCycleNextPressed(input))
        {
            if (pairCatalog.CyclePair(1))
                NotifySelection();
        }

        DrawSelections();
    }

    public void Dispose()
    {
        ClearSelection();
        lockRotationService.ClearRememberedRotations();
        pairCatalog.ClearRememberedConnectorSelection();
        autoDockInput.Unregister();
    }

    private void HandleDockActivationPressed()
    {
        pairCatalog.RescanPairs(preserveSelection: dockingPreviewActive);
        framesUntilRescan = AutoDockConstants.RescanIntervalFrames;

        if (pairCatalog.PairCount == 0)
        {
            ClearDockSelection();
            Notify($"AutoDock: no connector pairs within {AutoDockConstants.SoftSelectRadius:0.#} m.", "Red");
            return;
        }

        if (!dockingPreviewActive)
        {
            ClearLandingSelection();
            dockingPreviewActive = true;
            NotifySelection();
            return;
        }

        if (!pairCatalog.TryGetSelectedPair(out DockingPair pair))
        {
            NotifySelection();
            return;
        }

        pair.RefreshMetrics();
        if (!pair.InRange)
        {
            Notify($"AutoDock: selected pair is {pair.Distance:0.0} m away. Move within {AutoDockConstants.GetSearchRadius():0.#} m.", "Red");
            return;
        }

        if (pair.LockReady)
        {
            if (!DockingMath.TryGetControlledShipController(pair.Local.CubeGrid, out _))
            {
                Notify("AutoDock: take control of a ship controller on selected grid first.", "Red");
                return;
            }

            StartAutoDock(pair, "AutoDock: delaying connector lock to close final gap.");
            return;
        }

        if (DockingMath.TryGetGravityTiltWarning(pair, lockRotationService, out string warning))
        {
            Notify(warning, "Red");
            return;
        }

        if (!DockingMath.TryGetControlledShipController(pair.Local.CubeGrid, out _))
        {
            Notify("AutoDock: take control of a ship controller on selected grid first.", "Red");
            return;
        }

        StartAutoDock(pair, "AutoDock: moving selected grid into docking position.");
    }

    private void HandleLandingActivationPressed()
    {
        if (!RefreshLandingPreview(notifyOnFailure: true))
            return;

        if (!landingPreviewActive)
        {
            ClearDockSelection();
            landingPreviewActive = true;
            NotifyLandingPreview();
            return;
        }

        if (currentLandingPlan == null)
            return;

        if (!currentLandingPlan.HullClearanceOk)
        {
            Notify("AutoDock: landing pose would intersect terrain with hull.", "Red");
            return;
        }

        if (currentLandingPlan.ExpectedReadyGearCount <= 0)
        {
            Notify("AutoDock: terrain under current footprint is not landable.", "Red");
            return;
        }

        if (!DockingMath.TryGetControlledShipController(currentLandingPlan.Grid, out _))
        {
            Notify("AutoDock: take control of a ship controller on selected grid first.", "Red");
            return;
        }

        StartAutoLanding(currentLandingPlan, "AutoDock: moving into landing position.");
    }

    private bool RefreshLandingPreview(bool notifyOnFailure)
    {
        if (!AutoLandingPlanner.TryCreatePlan(out AutoLandingPlan plan, out string error))
        {
            currentLandingPlan = null;
            landingPreviewActive = false;
            if (notifyOnFailure && !string.IsNullOrWhiteSpace(error))
                Notify(error, "Red");
            return false;
        }

        currentLandingPlan = plan;
        return true;
    }

    private void StartAutoDock(DockingPair pair, string message)
    {
        ClearLandingSelection();
        autoDockPilot.Start(pair);
        pairCatalog.RememberPreferredConnector(pair?.Local);
        dockingPreviewActive = true;
        Notify(message, "White");
    }

    private void StartAutoLanding(AutoLandingPlan plan, string message)
    {
        ClearDockSelection();
        currentLandingPlan = plan;
        landingPreviewActive = true;
        autoLandingPilot.Start(plan);
        Notify(message, "White");
    }

    private void HandleAutoDockResult(AutoDockPilotUpdateResult result)
    {
        if (result.HasMessage)
            Notify(result.Message, result.Font);

        switch (result.Status)
        {
            case AutoDockPilotStatus.Completed:
                ClearDockSelection();
                break;

            case AutoDockPilotStatus.Cancelled:
                framesUntilRescan = AutoDockConstants.RescanIntervalFrames;
                break;
        }
    }

    private void HandleAutoLandingResult(AutoDockPilotUpdateResult result)
    {
        if (result.HasMessage)
            Notify(result.Message, result.Font);

        switch (result.Status)
        {
            case AutoDockPilotStatus.Completed:
                ClearLandingSelection();
                break;

            case AutoDockPilotStatus.Cancelled:
                RefreshLandingPreview(notifyOnFailure: false);
                landingPreviewActive = currentLandingPlan != null;
                break;
        }
    }

    private void HandleRotateAlignmentPressed()
    {
        if (!pairCatalog.TryGetSelectedPair(out DockingPair pair))
            return;

        lockRotationService.CycleRotationStep(pair, 1);
        HandleRotationChanged();
        NotifySelection();
    }

    private void HandleRotationChanged()
    {
        if (!dockingPreviewActive)
            return;

        pairCatalog.RescanPairs(preserveSelection: true);
        framesUntilRescan = AutoDockConstants.RescanIntervalFrames;
    }

    private void DrawSelections()
    {
        if (autoLandingPilot.IsActive)
        {
            LandingOverlayRenderer.DrawActive(autoLandingPilot.CurrentPlan);
            return;
        }

        if (landingPreviewActive && currentLandingPlan != null)
        {
            LandingOverlayRenderer.DrawPreview(currentLandingPlan);
            return;
        }

        if (autoDockPilot.IsActive)
        {
            DockingOverlayRenderer.DrawActivePair(autoDockPilot.ActivePair, lockRotationService);
            return;
        }

        DockingOverlayRenderer.DrawPreview(dockingPreviewActive, pairCatalog.Pairs, pairCatalog.SelectedIndex, lockRotationService);
    }

    private void NotifySelection()
    {
        if (!pairCatalog.TryGetSelectedPair(out DockingPair pair))
            return;

        pair.RefreshMetrics();
        string state = pair.LockReady
            ? "lock ready"
            : pair.InRange
                ? "in range"
                : $"outside {AutoDockConstants.GetSearchRadius():0.#} m range";
        lockRotationService.TryGetRotationStep(pair, out int rotationStep);
        int angleDegrees = DockingMath.TryGetOrientationAngleDegrees(pair, rotationStep, out int projectedAngleDegrees)
            ? projectedAngleDegrees
            : DockingMath.GetRotationAngleDegrees(rotationStep);
        int connectorNumber = pairCatalog.SelectedConnectorIndex >= 0 ? pairCatalog.SelectedConnectorIndex + 1 : 1;
        int pairNumber = pairCatalog.SelectedIndex >= 0 ? pairCatalog.SelectedIndex + 1 : 1;
        Notify(
            $"AutoDock: connector {connectorNumber}/{Math.Max(1, pairCatalog.ConnectorCount)}, pair {pairNumber}/{Math.Max(1, pairCatalog.PairCount)}, {pair.Distance:0.0} m, {state}, angle {angleDegrees} deg.",
            pair.InRange ? "White" : "Red");
    }

    private void NotifyLandingPreview()
    {
        if (currentLandingPlan == null)
            return;

        string clearanceText = double.IsInfinity(currentLandingPlan.MinHullClearance)
            ? "open hull clearance"
            : $"hull clearance {currentLandingPlan.MinHullClearance:0.00} m";
        Notify(
            $"AutoDock: landing preview {currentLandingPlan.ExpectedReadyGearCount}/{Math.Max(1, currentLandingPlan.Gears.Count)} gear(s) expected, {clearanceText}.",
            currentLandingPlan.HullClearanceOk ? "White" : "Red");
    }

    private void ClearDockSelection()
    {
        autoDockPilot.Reset();
        dockingPreviewActive = false;
        framesUntilRescan = 0;
        lockRotationService.ClearSelectedRotations();
        pairCatalog.ClearTransientState();
    }

    private void ClearLandingSelection()
    {
        autoLandingPilot.Reset();
        landingPreviewActive = false;
        currentLandingPlan = null;
    }

    private void ClearSelection()
    {
        ClearDockSelection();
        ClearLandingSelection();
    }

    private void ResetSessionState()
    {
        ClearSelection();
        lockRotationService.ClearRememberedRotations();
        pairCatalog.ClearRememberedConnectorSelection();
    }

    private static void Notify(string message, string font)
    {
        MyHudNotification notification = new MyHudNotification(MyCommonTexts.CustomText, 2500, font);
        notification.SetTextFormatArguments(message);
        MyHud.Notifications?.Add(notification);
    }
}
