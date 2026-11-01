using System.Collections.Generic;
using Sandbox.Game.Entities;
using Sandbox.ModAPI.Ingame;
using VRageMath;

namespace ClientPlugin;

internal static class GyroVelocityControl
{
    private readonly struct SavedGyro
    {
        public readonly bool Override;
        public readonly float Power;
        public readonly Vector3 Velocity;

        public SavedGyro(MyGyro gyro)
        {
            IMyGyro api = gyro;
            Override = gyro.GyroOverride;
            Power = gyro.GyroPower;
            Velocity = new Vector3(-api.Pitch, -api.Yaw, -api.Roll);
        }

        public void Restore(MyGyro gyro)
        {
            if (gyro.MarkedForClose)
                return;

            gyro.SetGyroTorque(Velocity);
            gyro.GyroPower = Power;
            gyro.GyroOverride = Override;
        }
    }

    private static readonly Dictionary<MyGyro, SavedGyro> savedGyros = new Dictionary<MyGyro, SavedGyro>();
    private static readonly List<MyGyro> unavailableGyros = new List<MyGyro>();
    private static MyCubeGrid controlledGrid;

    public static bool IsActive => controlledGrid != null;

    public static void Set(MyCubeGrid grid, Vector3D worldAngularVelocity, float powerLimit)
    {
        if (grid != controlledGrid)
            Clear();
        if (grid == null)
            return;

        controlledGrid = grid;
        foreach (var entry in savedGyros)
        {
            if (IsAvailable(entry.Key, grid))
                continue;

            entry.Value.Restore(entry.Key);
            unavailableGyros.Add(entry.Key);
        }
        foreach (MyGyro gyro in unavailableGyros)
            savedGyros.Remove(gyro);
        unavailableGyros.Clear();

        foreach (MyGyro gyro in grid.GetFatBlocks<MyGyro>())
        {
            if (!IsAvailable(gyro, grid))
                continue;

            if (!savedGyros.TryGetValue(gyro, out SavedGyro saved))
            {
                saved = new SavedGyro(gyro);
                savedGyros.Add(gyro, saved);
            }

            gyro.GyroPower = saved.Power * MathHelper.Clamp(powerLimit, 0f, 1f);
            gyro.GyroOverride = true;
            gyro.SetGyroTorque((Vector3)Vector3D.TransformNormal(worldAngularVelocity, gyro.PositionComp.WorldMatrixNormalizedInv));
        }
    }

    public static void Clear()
    {
        foreach (var entry in savedGyros)
            entry.Value.Restore(entry.Key);
        savedGyros.Clear();
        unavailableGyros.Clear();
        controlledGrid = null;
    }

    private static bool IsAvailable(MyGyro gyro, MyCubeGrid grid)
    {
        return gyro != null && !gyro.MarkedForClose && gyro.CubeGrid == grid && gyro.Enabled && gyro.IsFunctional;
    }
}
