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
    private readonly AutoDockInput autoDockInput;

    private bool active;
    private int framesUntilRescan;

    public AutoDockController()
    {
        lockRotationService = new LockRotationService();
        pairCatalog = new DockingPairCatalog(lockRotationService);
        autoDockPilot = new AutoDockPilot(lockRotationService);
        autoDockInput = new AutoDockInput();
    }

    public void Update()
    {
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
            DrawPairs();
            return;
        }

        if (autoDockInput.IsActivationPressed(input))
        {
            if (autoDockPilot.IsActive)
                HandleAutoDockResult(autoDockPilot.Cancel("AutoDock: automatic docking cancelled.", "White"));
            else
                HandleActivationPressed();
            return;
        }

        if (autoDockPilot.IsActive)
        {
            HandleAutoDockResult(autoDockPilot.Update());
            DrawPairs();
            return;
        }

        if (!active)
            return;

        framesUntilRescan--;
        if (framesUntilRescan <= 0)
        {
            pairCatalog.RescanPairs(preserveSelection: true);
            framesUntilRescan = AutoDockConstants.RescanIntervalFrames;

            if (pairCatalog.PairCount == 0)
            {
                ClearSelection();
                Notify($"AutoDock: no connector pairs within {AutoDockConstants.SoftSelectRadius:0.#} m.", "Red");
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

        DrawPairs();
    }

    public void Dispose()
    {
        ClearSelection();
        lockRotationService.ClearRememberedRotations();
        pairCatalog.ClearRememberedConnectorSelection();
        autoDockInput.Unregister();
    }

    private void HandleActivationPressed()
    {
        pairCatalog.RescanPairs(preserveSelection: active);
        framesUntilRescan = AutoDockConstants.RescanIntervalFrames;

        if (pairCatalog.PairCount == 0)
        {
            ClearSelection();
            Notify($"AutoDock: no connector pairs within {AutoDockConstants.SoftSelectRadius:0.#} m.", "Red");
            return;
        }

        if (!active)
        {
            active = true;
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
            StartAutoDock(pair, "AutoDock: delaying connector lock to close final gap.");
            return;
        }

        if (DockingMath.TryGetGravityTiltWarning(pair, lockRotationService, out string warning))
        {
            Notify(warning, "Red");
            return;
        }

        StartAutoDock(pair, "AutoDock: moving selected grid into docking position.");
    }

    private void StartAutoDock(DockingPair pair, string message)
    {
        autoDockPilot.Start(pair);
        pairCatalog.RememberPreferredConnector(pair?.Local);
        active = true;
        Notify(message, "White");
    }

    private void HandleAutoDockResult(AutoDockPilotUpdateResult result)
    {
        if (result.HasMessage)
            Notify(result.Message, result.Font);

        switch (result.Status)
        {
            case AutoDockPilotStatus.Completed:
                ClearSelection();
                break;

            case AutoDockPilotStatus.Cancelled:
                framesUntilRescan = AutoDockConstants.RescanIntervalFrames;
                break;
        }
    }

    private void HandleRotateAlignmentPressed()
    {
        if (!pairCatalog.TryGetSelectedPair(out DockingPair pair))
        {
            return;
        }

        lockRotationService.CycleRotationStep(pair, 1);
        HandleRotationChanged();
        NotifySelection();
    }

    private void HandleRotationChanged()
    {
        if (!active)
            return;

        pairCatalog.RescanPairs(preserveSelection: true);
        framesUntilRescan = AutoDockConstants.RescanIntervalFrames;
    }

    private void DrawPairs()
    {
        if (autoDockPilot.IsActive)
        {
            DockingOverlayRenderer.DrawActivePair(autoDockPilot.ActivePair, lockRotationService);
            return;
        }

        DockingOverlayRenderer.DrawPreview(active, pairCatalog.Pairs, pairCatalog.SelectedIndex, lockRotationService);
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

    private void ClearSelection()
    {
        autoDockPilot.Reset();
        active = false;
        framesUntilRescan = 0;
        lockRotationService.ClearSelectedRotations();
        pairCatalog.ClearTransientState();
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
