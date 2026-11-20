using System;
using System.Collections.Generic;
using System.IO;
using ClientPlugin;
using ClientPlugin.Settings;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.World;
using VRageMath;

internal static partial class Program
{
    private static void CheckDockingOrientation()
    {
        string testDirectory = Path.Combine(Path.GetTempPath(), "autodock-orientation-" + Guid.NewGuid());
        VRage.FileSystem.MyFileSystem.UserDataPath = testDirectory;
        try
        {
            CheckDockingOrientationCore(testDirectory);
        }
        finally
        {
            Directory.Delete(testDirectory, recursive: true);
        }
    }

    private static void CheckDockingOrientationCore(string testDirectory)
    {
        MySession.Static = new MySession { WorldId = Guid.NewGuid() };
        var grid = new MyCubeGrid { EntityId = 10 };
        var local = new MyShipConnector { EntityId = 1, CubeGrid = grid, RotationStep = 2 };
        var target = new MyShipConnector { EntityId = 2, CubeGrid = new MyCubeGrid { EntityId = 20 } };
        var pair = new DockingPair(local, target, 10, false, false);
        var rotations = new LockRotationService();
        Config.Current = new Config();
        Check(rotations.GetRotationStep(pair), 2);

        // Moving/rolling the ship and rebuilding catalog pairs must not pick a new default.
        local.RotationStep = 6;
        pair = new DockingPair(local, target, 8, false, false);
        Check(rotations.GetRotationStep(pair), 2);
        Check(rotations.CycleRotationStep(pair, 1), 3);
        Check(Config.Current.DockingRotations.Count, 0); // Preview alone isn't saved.
        string configPath = Path.Combine(testDirectory, "Storage", "AutoDock.cfg");
        Require(!File.Exists(configPath));

        // Run the actual start/cancel path. Save before lock, retain the choice after cancellation.
        var pilot = new AutoDockPilot(rotations);
        pilot.Start(pair);
        Require(File.Exists(configPath));
        Require(!File.Exists(configPath + ".tmp"));
        Check(Config.Current.DockingRotations[0].RotationStep, 3);
        pilot.Cancel("cancelled", "White");
        rotations.ClearSelectedRotations();
        Check(rotations.GetRotationStep(pair), 3);
        pilot.Start(pair);
        local.Other = target;
        pilot.Update(); // The completed lock must keep the saved choice, even if the actual pose differs.
        Require(!pilot.IsActive);
        Require(!rotations.RememberCurrentRotation(pair));
        Check(rotations.GetRotationStep(pair), 3);

        // Simulate a restart through the serialized preferences, with a different current pose.
        Config.Current = ConfigStorage.Load();
        rotations = new LockRotationService();
        Check(rotations.GetRotationStep(pair), 3);
        Require(rotations.HasRememberedRotation(local, target));

        // Each component of the identity must isolate the saved orientation.
        var anotherTarget = new MyShipConnector { EntityId = 3, CubeGrid = target.CubeGrid };
        Check(rotations.GetRotationStep(new DockingPair(local, anotherTarget, 0, false, false)), 6);
        var anotherLocal = new MyShipConnector { EntityId = 4, CubeGrid = grid, RotationStep = 7 };
        Check(rotations.GetRotationStep(new DockingPair(anotherLocal, target, 0, false, false)), 7);
        grid.EntityId = 11;
        Check(rotations.GetRotationStep(pair), 6);
        grid.EntityId = 10;
        Guid firstWorld = MySession.Static.WorldId;
        MySession.Static.WorldId = Guid.NewGuid();
        Check(rotations.GetRotationStep(pair), 6);
        MySession.Static.WorldId = firstWorld;
        Check(rotations.GetRotationStep(pair), 3);

        // Replacing a saved choice, wraparound, and clearing previews.
        Check(rotations.CycleRotationStep(pair, 5), 0);
        pilot = new AutoDockPilot(rotations);
        pilot.Start(pair);
        pilot.Reset();
        Require(!File.Exists(configPath + ".tmp"));
        Config.Current = ConfigStorage.Load();
        rotations.ClearSelectedRotations();
        Check(rotations.GetRotationStep(pair), 0);
        Check(Config.Current.DockingRotations.Count, 1);
        Check(rotations.CycleRotationStep(pair, -1), 7);

        // Manual docking seeds a preference only when this combination has no saved choice.
        local.Other = anotherTarget;
        grid.Connectors.Add(local);
        MySession.Static.ControlledGrid = grid;
        Require(rotations.TryGetLockedPair(out DockingPair manuallyDocked));
        Require(rotations.RememberCurrentRotation(manuallyDocked));
        Check(ConfigStorage.Load().DockingRotations.Count, 2);
        Require(!rotations.RememberCurrentRotation(manuallyDocked));

        // A failed disk write must not interrupt flight or lose the in-memory selection.
        string savedConfig = File.ReadAllText(configPath);
        VRage.FileSystem.MyFileSystem.UserDataPath = configPath; // A file cannot contain a Storage directory.
        pilot.Start(pair);
        Require(pilot.IsActive);
        Check(rotations.GetRotationStep(pair), 7);
        Require(VRage.Utils.MyLog.Default.Warned);
        pilot.Reset();
        Require(File.ReadAllText(configPath) == savedConfig);
        VRage.FileSystem.MyFileSystem.UserDataPath = testDirectory;
        Require(!rotations.TryGetRotationStep(null, out _));
        Require(!rotations.RememberSelectedRotation(null));

        // Existing config files without the new preference list still load normally.
        File.WriteAllText(configPath, "<Config />");
        Check(ConfigStorage.Load().DockingRotations.Count, 0);
    }
}

// Game/configuration adapters only: exercise the production rotation service, pilot lifecycle,
// pair model, and serialized preference schema without loading native game physics or UI.
namespace ClientPlugin
{
    public sealed class Config
    {
        public static Config Current = new Config();
        public List<DockingRotationPreference> DockingRotations { get; set; } = new List<DockingRotationPreference>();
        public ClientPlugin.Settings.Tools.Binding ActivationKeybind = new ClientPlugin.Settings.Tools.Binding(VRage.Input.MyKeys.P, ctrl: true);
        public ClientPlugin.Settings.Tools.Binding LandingActivationKeybind = new ClientPlugin.Settings.Tools.Binding(VRage.Input.MyKeys.P, alt: true);
        public ClientPlugin.Settings.Tools.Binding RotateAlignmentKeybind = new ClientPlugin.Settings.Tools.Binding(VRage.Input.MyKeys.R, ctrl: true);
    }

    internal static class Plugin
    {
        public const string Name = "AutoDock";
    }

    internal static class DockingMath
    {
        public static int NormalizeRotationStep(int step) => (step % 8 + 8) % 8;
        public static int GetDefaultRotationStep(DockingPair pair) => pair.Local.RotationStep;
        public static bool TryGetActualRotationStep(MyShipConnector local, MyShipConnector target, out int step)
        {
            step = local.RotationStep;
            return true;
        }

        public static bool TryCreateDesiredLocalConnectorWorldMatrix(MyShipConnector target, int step, out MatrixD matrix)
        {
            matrix = MatrixD.CreateRotationZ(step * Math.PI / 4);
            return true;
        }

        public static Vector3D GetConnectorReferencePosition(MyShipConnector connector) => Vector3D.Zero;
        public static bool IsLockedPair(MyShipConnector local, MyShipConnector target) => local.Other == target;
        public static bool IsLockReady(MyShipConnector local, MyShipConnector target) => false;
        public static bool IsWithinSearchRange(MyCubeGrid grid, double distance) => true;
        public static bool HasManualFlightInput() => false;
        public static bool IsDocked(DockingPair pair) => pair.Local.Other == pair.Target;
        public static bool IsPairStillUsable(DockingPair pair) => true;
        public static void ReleaseShipControl(MyCubeGrid grid) => AutoDockControlOverride.Clear();

        // Orientation checks stop before flight simulation; unexpected entry must fail loudly.
        public static bool TryCreateDockingTarget(DockingPair pair, LockRotationService rotations, bool final,
            out DockingTarget target, out bool finalActive, double maxAxialReach = double.PositiveInfinity)
            => throw new NotSupportedException();
        public static double MeasureFreeDistance(MyCubeGrid grid, Vector3D direction, double maxDistance)
            => throw new NotSupportedException();
        public static void GetProgressSample(DockingPair pair, DockingTarget target, out double connectorDistance,
            out double orientationMismatch, out double relativeSpeed, out double angularSpeed)
            => throw new NotSupportedException();
        public static bool TryGetControlledShipController(MyCubeGrid grid, out MyShipController controller)
        {
            controller = MySession.Static?.Controller;
            return controller != null && controller.CubeGrid == grid && controller.IsWorking && !controller.MarkedForClose;
        }
        public static void ApplyAutoDockControl(ref Vector3D integral, ref AngularPidControl angular,
            DockingPair pair, MyShipController controller, DockingTarget target, bool allowTranslation)
            => throw new NotSupportedException();
    }
}

namespace VRage.FileSystem
{
    internal static class MyFileSystem
    {
        public static string UserDataPath;
    }
}

namespace Sandbox.Game.World
{
    internal sealed class MySession
    {
        public static MySession Static;
        public Guid WorldId;
        public MyCubeGrid ControlledGrid;
        public MyShipController Controller;
    }
}

namespace Sandbox.Game.Entities.Cube
{
    internal sealed class MyShipConnector : Sandbox.ModAPI.Ingame.IMyShipConnector
    {
        public long EntityId;
        public MyCubeGrid CubeGrid;
        public MyShipConnector Other;
        public bool MarkedForClose { get; set; }
        public bool IsWorking => true;
        public int RotationStep;
        public bool HasLocalPlayerAccess() => true;
        public void Connect() => throw new NotSupportedException();
    }
}

namespace Sandbox.ModAPI.Ingame
{
    internal interface IMyShipConnector
    {
        void Connect();
    }
}

namespace VRage.Utils
{
    internal sealed class MyLog
    {
        public static readonly MyLog Default = new MyLog();
        public bool Warned;
        public void Warning(string message) => Warned = true;
    }
}
