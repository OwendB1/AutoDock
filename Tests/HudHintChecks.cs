using System;
using System.Collections.Generic;
using ClientPlugin;
using ClientPlugin.Settings.Tools;
using Sandbox;
using Sandbox.Game.Entities;
using Sandbox.Game.Gui;
using Sandbox.Game.World;
using Sandbox.Graphics.GUI;
using VRage.Input;

internal static partial class Program
{
    private static void CheckHudHints()
    {
        Config.Current = new Config();
        var grid = new MyCubeGrid { EntityId = 101 };
        var controller = new MyShipController { CubeGrid = grid };
        MySession.Static = new MySession { ControlledGrid = grid, Controller = controller };
        var catalog = new DockingPairCatalog { InRange = true };
        var hints = new AutoDockHudHints(catalog);
        void Update() => hints.Update(false, false, false, false);
        Update();
        AssertHint("AutoDock: [Ctrl+P] Preview docking");
        Require(MyHud.Notifications.Items[0].Level == MyNotificationLevel.Control);
        for (int i = 0; i < 29; i++) Update();
        Check(catalog.Scans, 1);
        Check(MyHud.Notifications.Adds, 1); // A persistent hint, not repeated notifications.
        catalog.InRange = false;
        Update();
        AssertHint(null);
        for (int i = 0; i < 29; i++) Update();
        Check(catalog.Scans, 2); // No nearby pair must still respect the scan interval.

        grid = new MyCubeGrid { EntityId = 102 };
        controller.CubeGrid = grid;
        MySession.Static.ControlledGrid = grid;
        catalog.InRange = true;
        Update();
        Check(catalog.Scans, 3); // Changing ship invalidates range immediately.
        Require(catalog.LastGrid == grid);
        AssertHint("AutoDock: [Ctrl+P] Preview docking");

        Config.Current.ActivationKeybind = new Binding(MyKeys.K, alt: true, shift: true);
        Update();
        AssertHint("AutoDock: [Alt+Shift+K] Preview docking");
        hints.Update(true, false, false, false);
        AssertHint("AutoDock: [Alt+Shift+K] Dock  |  [Ctrl+R] Rotate");
        hints.Update(true, true, false, false);
        AssertHint("AutoDock: [Alt+Shift+K] Cancel docking");
        hints.Update(false, false, true, false);
        AssertHint("AutoDock: [Alt+P] Land");
        hints.Update(false, false, true, true);
        AssertHint("AutoDock: [Alt+P] Cancel landing");
        Check(catalog.Scans, 3); // Maneuvers/previews do not trigger proximity scans.

        Config.Current.ActivationKeybind = new Binding(MyKeys.None);
        hints.Update(true, false, false, false);
        AssertHint("AutoDock: [Ctrl+R] Rotate");
        Update();
        AssertHint(null);
        Check(catalog.Scans, 3);
        Config.Current.ActivationKeybind = new Binding(MyKeys.P, ctrl: true);

        // UI focus, passenger seats, leaving the cockpit, disabled hints, and hidden HUD.
        MyScreenManager.Focus = new MyGuiScreenBase();
        Update(); AssertHint(null);
        MyScreenManager.Focus = null;
        controller.EnableShipControl = false;
        Update(); AssertHint(null);
        controller.EnableShipControl = true;
        MySession.Static.Controller = null;
        Update(); AssertHint(null);
        MySession.Static.Controller = controller;
        MySandboxGame.Config.ControlsHints = false;
        Update(); AssertHint(null);
        MySandboxGame.Config.ControlsHints = true;
        MyHud.MinimalHud = true;
        Update(); AssertHint(null);
        MyHud.MinimalHud = false;
        MyHud.CutsceneHud = true;
        Update(); AssertHint(null);
        MyHud.CutsceneHud = false;
        grid.IsStatic = true;
        Update(); AssertHint(null);
        grid.IsStatic = false;
        Check(catalog.Scans, 3);

        Update();
        var previousOwner = MyHud.Notifications;
        MyHud.Notifications = new MyHudNotifications();
        Update();
        Require(previousOwner.Items.Count == 0);
        AssertHint("AutoDock: [Ctrl+P] Preview docking");
        hints.Clear();
        AssertHint(null);
        Update();
        MySession.Static = null;
        Update();
        AssertHint(null);
        Console.WriteLine("HUD hint contexts, configured bindings, throttling, visibility, and cleanup passed.");
    }

    private static void AssertHint(string text)
    {
        var items = MyHud.Notifications.Items;
        if (text == null ? items.Count != 0 : items.Count != 1 || items[0].Text != text)
            throw new Exception($"Unexpected HUD hint; expected {text ?? "none"}");
    }
}

// Adapters for native HUD/input state; the hint lifecycle and key formatting are production code.
namespace ClientPlugin
{
    internal sealed class DockingPairCatalog
    {
        public bool InRange;
        public int Scans;
        public MyCubeGrid LastGrid;
        public bool HasPairInRange(MyCubeGrid grid) { Scans++; LastGrid = grid; return InRange; }
    }
}

namespace Sandbox
{
    internal static class MySandboxGame
    {
        public static readonly GameConfig Config = new GameConfig();
        internal sealed class GameConfig { public bool ControlsHints = true; }
    }
}

namespace Sandbox.Graphics.GUI
{
    internal class MyGuiScreenBase { }
    internal static class MyScreenManager
    {
        public static MyGuiScreenBase Focus;
        public static MyGuiScreenBase GetScreenWithFocus() => Focus;
    }
}

namespace Sandbox.Game.Gui
{
    internal sealed class MyGuiScreenGamePlay : MyGuiScreenBase
    {
        public static readonly MyGuiScreenGamePlay Static = new MyGuiScreenGamePlay();
    }
    internal enum MyNotificationLevel { Control }
    internal static class MyCommonTexts { public static object CustomText => null; }
    internal static class MyHud
    {
        public static MyHudNotifications Notifications = new MyHudNotifications();
        public static bool MinimalHud;
        public static bool CutsceneHud;
    }
    internal sealed class MyHudNotifications
    {
        public readonly List<MyHudNotification> Items = new List<MyHudNotification>();
        public int Adds;
        public void Add(MyHudNotification notification) { Items.Add(notification); Adds++; }
        public void Remove(MyHudNotification notification) => Items.Remove(notification);
    }
    internal sealed class MyHudNotification
    {
        public string Text;
        public readonly MyNotificationLevel Level;
        public MyHudNotification(object text, int disappearTimeMs, string font, MyNotificationLevel level) => Level = level;
        public void SetTextFormatArguments(string text) => Text = text;
    }
}

namespace VRage.ModAPI { }
namespace VRage.Input
{
    public enum MyKeys { None, P, R, K }
    [Flags] public enum MyKeyboardModifiers { None = 0, Control = 1, Alt = 2, Shift = 4 }
    public interface IMyInput
    {
        bool IsKeyPress(MyKeys key);
        bool IsNewKeyPressed(MyKeys key);
        bool IsAnyCtrlKeyPressed();
        bool IsAnyAltKeyPressed();
        bool IsAnyShiftKeyPressed();
    }
    internal static class MyInput { public static object Static = new object(); }
}
