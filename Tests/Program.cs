using System;
using ClientPlugin;
using Sandbox.Game.Entities;
using VRageMath;

internal static class Program
{
    private static void Main()
    {
        Vector3D positive = new Vector3D(10000, 20000, 30000);
        Vector3D negative = new Vector3D(40000, 50000, 60000);
        var control = new LinearThrustControl(1000, positive, negative, Vector3D.Zero, MatrixD.Identity);
        CheckVector(control.CreateMoveIndicator(new Vector3D(2, 4, 6), MatrixD.Identity), new Vector3D(0.2));
        CheckVector(control.CreateMoveIndicator(new Vector3D(-8, -10, -12), MatrixD.Identity), new Vector3D(-0.2));
        var heavier = new LinearThrustControl(2000, positive, negative, Vector3D.Zero, MatrixD.Identity);
        CheckVector(heavier.CreateMoveIndicator(new Vector3D(2, 4, 6), MatrixD.Identity), new Vector3D(0.4));
        CheckVector(control.CreateMoveIndicator(new Vector3D(1000), MatrixD.Identity), Vector3D.One);
        CheckVector(control.CreateMoveIndicator(new Vector3D(-1000), MatrixD.Identity), -Vector3D.One);

        Check(control.GetAvailableAcceleration(Vector3D.Right, 100), 10);
        Check(control.GetAvailableAcceleration(Vector3D.Left, 100), 40);
        Check(control.GetAvailableAcceleration(Vector3D.Up, 100), 20);
        Check(control.GetAvailableAcceleration(Vector3D.Down, 100), 50);
        Check(control.GetAvailableAcceleration(Vector3D.Backward, 100), 30);
        Check(control.GetAvailableAcceleration(Vector3D.Forward, 100), 60);
        Check(control.GetAvailableAcceleration(Vector3D.Left, 4), 4);
        Check(control.GetAvailableAcceleration(Vector3D.Normalize(new Vector3D(1, 1, 0)), 100), 10 * Math.Sqrt(2));

        var inGravity = new LinearThrustControl(1000, positive, negative, new Vector3D(0, -10, 0), MatrixD.Identity);
        CheckVector(inGravity.CreateMoveIndicator(Vector3D.Zero, MatrixD.Identity), new Vector3D(0, 0.5, 0));
        Check(inGravity.GetAvailableAcceleration(Vector3D.Up, 100), 10);
        Check(inGravity.GetAvailableAcceleration(Vector3D.Down, 100), 60);

        // Recover the requested world acceleration from cockpit input on a rotated grid/cockpit.
        MatrixD grid = MatrixD.CreateRotationY(0.7) * MatrixD.CreateRotationX(0.3);
        grid.Translation = new Vector3D(100, 200, 300);
        MatrixD cockpit = MatrixD.CreateRotationZ(Math.PI / 2) * grid;
        Vector3D gravity = new Vector3D(0, -9.81, 0);
        var rotated = new LinearThrustControl(1000, positive, negative, gravity, MatrixD.Invert(grid));
        Vector3D desired = new Vector3D(1, -2, 3);
        Vector3D input = rotated.CreateMoveIndicator(desired, MatrixD.Invert(cockpit));
        Vector3D gridInput = Vector3D.TransformNormal(Vector3D.TransformNormal(input, cockpit), MatrixD.Invert(grid));
        Vector3D force = new Vector3D(
            gridInput.X * (gridInput.X >= 0 ? positive.X : negative.X),
            gridInput.Y * (gridInput.Y >= 0 ? positive.Y : negative.Y),
            gridInput.Z * (gridInput.Z >= 0 ? positive.Z : negative.Z));
        CheckVector(Vector3D.TransformNormal(force / 1000, grid) + gravity, desired);
        Check(rotated.GetAvailableAcceleration(Vector3D.TransformNormal(Vector3D.Right, grid), 100),
            10 + Vector3D.TransformNormal(gravity, MatrixD.Invert(grid)).X);
        var singleAxis = new LinearThrustControl(1000, new Vector3D(10000, 0, 0), Vector3D.Zero, Vector3D.Zero, MatrixD.Invert(grid));
        Check(singleAxis.GetAvailableAcceleration(Vector3D.TransformNormal(Vector3D.Right, grid), 100), 10);

        foreach (double mass in new[] { 0, -1, double.NaN, double.PositiveInfinity })
        {
            var invalid = new LinearThrustControl(mass, positive, negative, gravity, MatrixD.Identity);
            CheckVector(invalid.CreateMoveIndicator(desired, MatrixD.Identity), Vector3D.Zero);
            Check(invalid.GetAvailableAcceleration(Vector3D.Left, 100), 0);
        }
        var missing = new LinearThrustControl(1000, Vector3D.Zero, negative, Vector3D.Zero, MatrixD.Identity);
        CheckVector(missing.CreateMoveIndicator(new Vector3D(1), MatrixD.Identity), Vector3D.Zero);
        Check(missing.GetAvailableAcceleration(Vector3D.Right, 100), 0);
        var invalidThrust = new LinearThrustControl(1000, new Vector3D(double.NaN, double.PositiveInfinity, -1), negative, Vector3D.Zero, MatrixD.Identity);
        CheckVector(invalidThrust.CreateMoveIndicator(new Vector3D(1), MatrixD.Identity), Vector3D.Zero);
        var insufficientLift = new LinearThrustControl(1000, new Vector3D(1000), negative, gravity, MatrixD.Identity);
        Check(insufficientLift.GetAvailableAcceleration(Vector3D.Up, 100), 0);

        CheckDampenersLifecycle();
        Console.WriteLine("Thrust scaling, six directions, braking, gravity, rotation, invalid inputs, and dampener lifecycle passed.");
    }

    private static void CheckDampenersLifecycle()
    {
        var controller = new MyShipController { EntityId = 1, DampenersOverride = true };
        AutoDockControlOverride.BeginFrame();
        AutoDockControlOverride.Set(controller, Vector3.One, Vector2.Zero, 0);
        AutoDockControlOverride.EndFrame();
        Require(!controller.DampenersOverride);
        Vector3 move = Vector3.Zero;
        Vector2 rotation = Vector2.Zero;
        float roll = 0;
        Require(AutoDockControlOverride.TryOverride(controller, ref move, ref rotation, ref roll));
        CheckVector(move, Vector3D.One);
        AutoDockControlOverride.BeginFrame();
        AutoDockControlOverride.Set(controller, Vector3.Zero, Vector2.Zero, 0);
        AutoDockControlOverride.EndFrame();
        AutoDockControlOverride.Clear();
        Require(controller.DampenersOverride);

        AutoDockControlOverride.Set(controller, Vector3.Zero, Vector2.Zero, 0);
        AutoDockControlOverride.BeginFrame();
        AutoDockControlOverride.EndFrame();
        Require(controller.DampenersOverride);
        Require(!AutoDockControlOverride.TryOverride(controller, ref move, ref rotation, ref roll));

        AutoDockControlOverride.Set(controller, Vector3.Zero, Vector2.Zero, 0);
        AutoDockControlOverride.Set(controller, Vector3.Zero, Vector2.Zero, 0, controlTranslation: false);
        Require(controller.DampenersOverride);
        AutoDockControlOverride.Set(controller, Vector3.Zero, Vector2.Zero, 0);
        var other = new MyShipController { EntityId = 2, DampenersOverride = false };
        AutoDockControlOverride.Set(other, Vector3.Zero, Vector2.Zero, 0);
        Require(controller.DampenersOverride && !other.DampenersOverride);
        AutoDockControlOverride.Clear();
        Require(!other.DampenersOverride);
        AutoDockControlOverride.Set(controller, Vector3.Zero, Vector2.Zero, 0);
        AutoDockControlOverride.Set(null, Vector3.Zero, Vector2.Zero, 0);
        Require(controller.DampenersOverride);
    }

    private static void CheckVector(Vector3D actual, Vector3D expected)
    {
        Check(actual.X, expected.X);
        Check(actual.Y, expected.Y);
        Check(actual.Z, expected.Z);
    }

    private static void Check(double actual, double expected)
    {
        if (double.IsNaN(actual) || Math.Abs(actual - expected) > 0.00001)
            throw new Exception($"Expected {expected}, got {actual}");
    }

    private static void Require(bool condition)
    {
        if (!condition)
            throw new Exception("Dampener lifecycle check failed");
    }
}

// Only the controller's state is needed to exercise the real override lifecycle without a running game.
namespace Sandbox.ModAPI.Ingame
{
    internal interface IMyShipController
    {
        bool DampenersOverride { get; set; }
    }
}

namespace Sandbox.Game.Entities
{
    internal sealed class MyShipController : Sandbox.ModAPI.Ingame.IMyShipController
    {
        public long EntityId { get; set; }
        public bool MarkedForClose { get; set; }
        public bool DampenersOverride { get; set; }
    }
}
