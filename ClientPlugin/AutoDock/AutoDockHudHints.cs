using System.Collections.Generic;
using ClientPlugin.Settings.Tools;
using Sandbox;
using Sandbox.Game.Entities;
using Sandbox.Game.Gui;
using Sandbox.Game.World;
using Sandbox.Graphics.GUI;
using VRage.Input;

namespace ClientPlugin;

internal sealed class AutoDockHudHints
{
    private const int NearbyScanIntervalFrames = 30;
    private readonly DockingPairCatalog pairCatalog;
    private MyCubeGrid nearbyGrid;
    private int framesUntilScan;
    private bool hasNearbyPair;
    private MyHudNotifications notificationOwner;
    private MyHudNotification notification;
    private string displayedText;

    public AutoDockHudHints(DockingPairCatalog pairCatalog)
    {
        this.pairCatalog = pairCatalog;
    }

    public void Update(bool dockingPreview, bool dockingActive, bool landingPreview, bool landingActive)
    {
        MyCubeGrid grid = MySession.Static?.ControlledGrid;
        MyGuiScreenBase focus = MyScreenManager.GetScreenWithFocus();
        if (grid == null || grid.MarkedForClose || grid.IsStatic || grid.Physics == null
            || MyInput.Static == null || MyHud.Notifications == null
            || !MySandboxGame.Config.ControlsHints || MyHud.MinimalHud || MyHud.CutsceneHud
            || (focus != null && focus != MyGuiScreenGamePlay.Static)
            || !DockingMath.TryGetControlledShipController(grid, out MyShipController controller)
            || !controller.EnableShipControl)
        {
            Clear();
            return;
        }

        string text;
        if (dockingActive)
            text = FormatHint((Config.Current.ActivationKeybind, "Cancel docking"));
        else if (landingActive)
            text = FormatHint((Config.Current.LandingActivationKeybind, "Cancel landing"));
        else if (dockingPreview)
            text = FormatHint((Config.Current.ActivationKeybind, "Dock"), (Config.Current.RotateAlignmentKeybind, "Rotate"));
        else if (landingPreview)
            text = FormatHint((Config.Current.LandingActivationKeybind, "Land"));
        else
        {
            if (Config.Current.ActivationKeybind.Key == MyKeys.None)
            {
                Clear();
                return;
            }

            if (nearbyGrid != grid || --framesUntilScan <= 0)
            {
                nearbyGrid = grid;
                hasNearbyPair = pairCatalog.HasPairInRange(grid);
                framesUntilScan = NearbyScanIntervalFrames;
            }
            Show(hasNearbyPair ? FormatHint((Config.Current.ActivationKeybind, "Preview docking")) : null);
            return;
        }

        framesUntilScan = 0; // Recheck range as soon as a preview or maneuver ends.
        Show(text);
    }

    public void Clear()
    {
        Show(null);
        nearbyGrid = null;
        framesUntilScan = 0;
        hasNearbyPair = false;
    }

    private void Show(string text)
    {
        if (text == displayedText && notificationOwner == MyHud.Notifications)
            return;

        if (notification != null)
            notificationOwner?.Remove(notification);
        notification = null;
        notificationOwner = null;
        displayedText = null;
        if (string.IsNullOrEmpty(text) || MyHud.Notifications == null)
            return;

        notification = new MyHudNotification(MyCommonTexts.CustomText, disappearTimeMs: 0,
            font: "Blue", level: MyNotificationLevel.Control);
        notification.SetTextFormatArguments(text);
        notificationOwner = MyHud.Notifications;
        notificationOwner.Add(notification);
        displayedText = text;
    }

    private static string FormatHint(params (Binding Binding, string Action)[] actions)
    {
        var hints = new List<string>();
        foreach (var action in actions)
        {
            if (action.Binding.Key != MyKeys.None)
                hints.Add($"[{action.Binding}] {action.Action}");
        }
        return hints.Count == 0 ? null : "AutoDock: " + string.Join("  |  ", hints);
    }
}
