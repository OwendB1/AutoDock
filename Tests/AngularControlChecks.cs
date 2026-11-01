using System;
using System.Collections.Generic;
using ClientPlugin;
using Sandbox.Game.Entities;
using VRageMath;

internal static partial class Program
{
    private static void CheckAngularControl()
    {
        var pid = new AngularPidControl();
        // The first sample has no derivative history; later samples use seconds, not frames.
        CheckVector(pid.Update(Vector3D.One, Vector3D.Zero, 0.1, 0, 0, 2, 1, 100, 10), Vector3D.Zero);
        CheckVector(pid.Update(new Vector3D(1.1), Vector3D.Zero, 0.1, 0, 0, 2, 1, 100, 10), new Vector3D(2));
        CheckVector(pid.Update(new Vector3D(1.2), Vector3D.Zero, 0.05, 0, 0, 2, 1, 100, 20), new Vector3D(4));
        CheckVector(pid.Update(new Vector3D(1.1), Vector3D.Zero, 0.1, 0, 0, 2, 1, 100, 10), new Vector3D(-2));
        pid = default;
        CheckVector(pid.Update(Vector3D.One, Vector3D.Zero, 0.1, 0, 0, 2, 1, 100, 10), Vector3D.Zero);

        pid = default;
        CheckVector(pid.Update(Vector3D.Right, Vector3D.Zero, 0.1, 2, 0, 0, 1, 100, 10), Vector3D.Right * 2);
        pid = default;
        for (int i = 0; i < 100; i++)
            CheckVector(pid.Update(Vector3D.Right, Vector3D.Zero, 0.1, 0, 2, 0, 0.05, 100, 10), Vector3D.Right * 0.1);
        pid = default;
        CheckVector(pid.Update(Vector3D.Right * 100, Vector3D.Zero, 0.1, 2, 0, 0, 1, 0.5, 10), Vector3D.Right * 0.5);
        pid = default;
        CheckVector(pid.Update(Vector3D.Zero, Vector3D.Right, 0.1, 0, 0, 0, 1, 100, 2), Vector3D.Right * 0.8);

        // Feed the real PID through an inertia/torque-limited single-axis physical plant.
        CheckAngularResponse(0.01, 10000);
        CheckAngularResponse(10, 10000);
        CheckAngularResponse(10, 200000);
        CheckAngularResponse(100000, 50000);
    }

    private static void CheckAngularResponse(double inertia, double availableTorque)
    {
        const double step = 1.0 / 60;
        var pid = new AngularPidControl();
        double angle = 0.8;
        double velocity = 0;
        for (int i = 0; i < 1800; i++)
        {
            Vector3D command = pid.Update(new Vector3D(0, -2 * Math.Sin(angle), 0),
                new Vector3D(0, velocity, 0), step, 2, 0.08, 0.5, 0.35, 0.65, 1.8);
            double torque = MathHelper.Clamp((command.Y - velocity) * inertia / step, -availableTorque, availableTorque);
            velocity += torque / inertia * step;
            angle += velocity * step;
            if (Math.Abs(angle) > 0.80001 || double.IsNaN(angle))
                throw new Exception("Angular PID diverged in inertia/gyro-strength check");
        }
        if (Math.Abs(angle) > 0.01 || Math.Abs(velocity) > 0.01)
            throw new Exception($"Angular PID failed to settle: angle {angle}, velocity {velocity}");
    }

    private static void CheckGyroLifecycle()
    {
        var grid = new MyCubeGrid();
        var controller = new MyShipController { EntityId = 3, CubeGrid = grid, DampenersOverride = true };
        var gyro = new MyGyro { CubeGrid = grid, GyroPower = 0.6f, Velocity = new Vector3(1, 2, 3) };
        var overridden = new MyGyro { CubeGrid = grid, GyroOverride = true, GyroPower = 0.4f, Velocity = new Vector3(-1, 2, -3) };
        overridden.PositionComp.WorldMatrixNormalizedInv = MatrixD.CreateRotationX(0.7) * MatrixD.CreateRotationY(0.3);
        var disabled = new MyGyro { CubeGrid = grid, Enabled = false };
        var broken = new MyGyro { CubeGrid = grid, IsFunctional = false };
        grid.Gyros.AddRange(new[] { gyro, overridden, disabled, broken });

        Vector3D target = new Vector3D(0.1, -0.2, 0.3);
        AutoDockControlOverride.BeginFrame();
        AutoDockControlOverride.Set(controller, Vector3.Zero, target, 0.5f);
        AutoDockControlOverride.EndFrame();
        Require(gyro.GyroOverride && overridden.GyroOverride && !disabled.GyroOverride && !broken.GyroOverride);
        Check(gyro.GyroPower, 0.3);
        Check(overridden.GyroPower, 0.2);
        CheckVector(gyro.Velocity, target);
        CheckVector(overridden.Velocity, Vector3D.TransformNormal(target, overridden.PositionComp.WorldMatrixNormalizedInv));
        Vector3 move = Vector3.One;
        Vector2 rotation = Vector2.One;
        float roll = 1;
        Require(AutoDockControlOverride.TryOverride(controller, ref move, ref rotation, ref roll));
        CheckVector(new Vector3D(rotation.X, rotation.Y, roll), Vector3D.Zero);

        AutoDockControlOverride.Set(controller, Vector3.Zero, target, 0.25f);
        Check(gyro.GyroPower, 0.15); // The limit applies to original power, never compounds.
        gyro.CubeGrid = new MyCubeGrid();
        AutoDockControlOverride.Set(controller, Vector3.Zero, target, 0.5f);
        Require(!gyro.GyroOverride);
        Check(gyro.GyroPower, 0.6);
        CheckVector(gyro.Velocity, new Vector3D(1, 2, 3));

        var added = new MyGyro { CubeGrid = grid, GyroPower = 0.8f };
        grid.Gyros.Add(added);
        AutoDockControlOverride.Set(controller, Vector3.Zero, target, 0.5f);
        Require(added.GyroOverride);
        added.Enabled = false;
        AutoDockControlOverride.Set(controller, Vector3.Zero, target, 0.5f);
        Require(!added.GyroOverride);
        Check(added.GyroPower, 0.8);

        AutoDockControlOverride.BeginFrame();
        AutoDockControlOverride.EndFrame();
        Require(overridden.GyroOverride && !GyroVelocityControl.IsActive && controller.DampenersOverride);
        Check(overridden.GyroPower, 0.4);
        CheckVector(overridden.Velocity, new Vector3D(-1, 2, -3));

        AutoDockControlOverride.Set(controller, Vector3.Zero, target, 0.5f);
        var other = new MyShipController { EntityId = 4, CubeGrid = new MyCubeGrid() };
        AutoDockControlOverride.Set(other, Vector3.Zero, target, 0.5f);
        CheckVector(overridden.Velocity, new Vector3D(-1, 2, -3));
        AutoDockControlOverride.Set(controller, Vector3.Zero, target, 0.5f);
        overridden.MarkedForClose = true;
        int writes = overridden.Writes;
        AutoDockControlOverride.Clear();
        Check(overridden.Writes, writes);
        Require(!GyroVelocityControl.IsActive);
    }
}

namespace Sandbox.ModAPI.Ingame
{
    internal interface IMyGyro
    {
        float Pitch { get; }
        float Yaw { get; }
        float Roll { get; }
    }
}

namespace Sandbox.Game.Entities
{
    internal sealed class MyCubeGrid
    {
        public readonly List<MyGyro> Gyros = new List<MyGyro>();

        public IEnumerable<T> GetFatBlocks<T>()
        {
            foreach (MyGyro gyro in Gyros)
                if (gyro is T block)
                    yield return block;
        }
    }

    internal sealed class MyGyro : Sandbox.ModAPI.Ingame.IMyGyro
    {
        public MyCubeGrid CubeGrid { get; set; }
        public bool MarkedForClose { get; set; }
        public bool Enabled { get; set; } = true;
        public bool IsFunctional { get; set; } = true;
        public bool GyroOverride { get; set; }
        public float GyroPower { get; set; } = 1;
        public Vector3 Velocity { get; set; }
        public float Pitch => -Velocity.X;
        public float Yaw => -Velocity.Y;
        public float Roll => -Velocity.Z;
        public int Writes { get; private set; }
        public readonly GyroPosition PositionComp = new GyroPosition();

        public void SetGyroTorque(Vector3 velocity)
        {
            Velocity = velocity;
            Writes++;
        }
    }

    internal sealed class GyroPosition
    {
        public MatrixD WorldMatrixNormalizedInv = MatrixD.Identity;
    }
}
