using System;
using Sandbox.Game.Gui;
using Sandbox.Game.World;
using Sandbox.Graphics.GUI;
using VRage.Game;
using VRage.Input;
using VRage.Utils;
using VRageMath;

namespace ClientPlugin;

internal sealed class AutoDockController
{
    private readonly SavedAlignmentService savedAlignmentService;
    private readonly DockingPairCatalog pairCatalog;
    private readonly AutoDockPilot autoDockPilot;
    private readonly AutoDockInput autoDockInput;

    private bool active;
    private int framesUntilRescan;
    private SavedAlignmentRemovalScreen removeAlignmentScreen;

    public AutoDockController()
    {
        savedAlignmentService = new SavedAlignmentService();
        pairCatalog = new DockingPairCatalog(savedAlignmentService);
        autoDockPilot = new AutoDockPilot(savedAlignmentService);
        autoDockInput = new AutoDockInput();
    }

    public void Update()
    {
        if (MySession.Static == null || MyInput.Static == null)
        {
            ClearSelection();
            return;
        }

        IMyInput input = MyInput.Static;
        autoDockInput.EnsureRegistered(input);

        MyGuiScreenBase screenWithFocus = MyScreenManager.GetScreenWithFocus();
        if (removeAlignmentScreen != null && screenWithFocus == removeAlignmentScreen)
            removeAlignmentScreen.HandleExternalInput(input);

        if (screenWithFocus != null && screenWithFocus != MyGuiScreenGamePlay.Static)
        {
            DrawPairs();
            return;
        }

        if (autoDockInput.IsRemoveAlignmentPressed(input))
        {
            HandleRemoveAlignmentPressed();
            DrawPairs();
            return;
        }

        if (autoDockInput.IsSaveAlignmentPressed(input))
        {
            HandleSaveAlignmentPressed();
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

        if (autoDockInput.IsCyclePreviousConnectorPressed(input))
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
        removeAlignmentScreen?.CloseScreen();
        removeAlignmentScreen = null;
        ClearSelection();
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

        if (DockingMath.TryGetGravityTiltWarning(pair, savedAlignmentService, out string warning))
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

    private void HandleSaveAlignmentPressed()
    {
        if (!savedAlignmentService.TryGetLockedPair(out DockingPair pair))
        {
            Notify("AutoDock: connectors must be locked before saving alignment.", "Red");
            return;
        }

        if (!savedAlignmentService.TryBuildRelativeConnectorAlignment(pair.Local, pair.Target, out MatrixD relativeConnectorMatrix))
        {
            Notify("AutoDock: cannot save connector alignment right now.", "Red");
            return;
        }

        savedAlignmentService.SaveAlignment(pair, relativeConnectorMatrix);
        HandleAlignmentChanged();
        Notify("AutoDock: saved connector alignment for current pair.", "Green");
    }

    private void HandleRemoveAlignmentPressed()
    {
        if (savedAlignmentService.TryGetLockedPair(out DockingPair pair)
            && savedAlignmentService.TryGetSavedAlignment(pair.Local.EntityId, pair.Target.EntityId, out SavedConnectorAlignment savedAlignment, out _))
        {
            RemoveSavedAlignment(
                savedAlignment,
                $"AutoDock: removed saved alignment for {savedAlignment.GetDisplayName(savedAlignmentService.GetGridDisplayName(pair.Local.CubeGrid))}.");
            return;
        }

        OpenRemoveAlignmentScreen();
    }

    private void OpenRemoveAlignmentScreen()
    {
        if (removeAlignmentScreen != null)
            return;

        var savedAlignments = savedAlignmentService.SavedAlignments;
        if (savedAlignments.Count == 0)
        {
            Notify("AutoDock: no saved alignments to remove.", "Red");
            return;
        }

        string currentGridName = savedAlignmentService.GetGridDisplayName(MySession.Static?.ControlledGrid);
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

        RemoveSavedAlignment(
            savedAlignment,
            $"AutoDock: removed saved alignment for {savedAlignment.GetDisplayName(savedAlignmentService.GetGridDisplayName(MySession.Static?.ControlledGrid))}.");
    }

    private void RemoveSavedAlignment(SavedConnectorAlignment savedAlignment, string message)
    {
        savedAlignmentService.RemoveSavedAlignment(savedAlignment);
        HandleAlignmentChanged();
        Notify(message, "Green");
    }

    private void HandleAlignmentChanged()
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
            DockingOverlayRenderer.DrawActivePair(autoDockPilot.ActivePair);
            return;
        }

        DockingOverlayRenderer.DrawPreview(active, pairCatalog.Pairs, pairCatalog.SelectedIndex);
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
        string savedAlignmentState = pair.HasSavedAlignment ? ", saved alignment" : "";
        int connectorNumber = pairCatalog.SelectedConnectorIndex >= 0 ? pairCatalog.SelectedConnectorIndex + 1 : 1;
        int pairNumber = pairCatalog.SelectedIndex >= 0 ? pairCatalog.SelectedIndex + 1 : 1;
        Notify(
            $"AutoDock: connector {connectorNumber}/{Math.Max(1, pairCatalog.ConnectorCount)}, pair {pairNumber}/{Math.Max(1, pairCatalog.PairCount)}, {pair.Distance:0.0} m, {state}{savedAlignmentState}.",
            pair.InRange ? "White" : "Red");
    }

    private void ClearSelection()
    {
        autoDockPilot.Reset();
        active = false;
        framesUntilRescan = 0;
        pairCatalog.ClearTransientState();
    }

    private static void Notify(string message, string font)
    {
        MyHudNotification notification = new MyHudNotification(MyCommonTexts.CustomText, 2500, font);
        notification.SetTextFormatArguments(message);
        MyHud.Notifications?.Add(notification);
    }
}
