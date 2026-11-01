using Sandbox.Game.Entities;
using Sandbox.ModAPI.Ingame;
using VRageMath;

namespace ClientPlugin;

internal static class AutoDockControlOverride
{
    private static long controllerEntityId;
    private static Vector3 moveIndicator;
    private static bool active;
    private static MyShipController dampenersController;
    private static bool savedDampenersEnabled;

    public static void BeginFrame()
    {
        active = false;
    }

    public static void EndFrame()
    {
        if (!active)
        {
            RestoreDampeners();
            GyroVelocityControl.Clear();
        }
    }

    public static void Set(MyShipController controller, Vector3 move, Vector3D worldAngularVelocity, float gyroPowerLimit, bool controlTranslation = true)
    {
        if (controller == null)
        {
            Clear();
            return;
        }

        if (!controlTranslation || dampenersController != controller)
            RestoreDampeners();

        if (controlTranslation)
        {
            IMyShipController controllerApi = controller;
            if (dampenersController == null)
            {
                dampenersController = controller;
                savedDampenersEnabled = controllerApi.DampenersOverride;
            }

            // The PID supplies gravity compensation; native dampening would add a second controller.
            if (controllerApi.DampenersOverride)
                controllerApi.DampenersOverride = false;
        }

        controllerEntityId = controller.EntityId;
        moveIndicator = move;
        GyroVelocityControl.Set(controller.CubeGrid, worldAngularVelocity, gyroPowerLimit);
        active = true;
    }

    public static void Clear()
    {
        RestoreDampeners();
        GyroVelocityControl.Clear();
        controllerEntityId = 0L;
        moveIndicator = Vector3.Zero;
        active = false;
    }

    private static void RestoreDampeners()
    {
        if (dampenersController != null && !dampenersController.MarkedForClose)
            ((IMyShipController)dampenersController).DampenersOverride = savedDampenersEnabled;

        dampenersController = null;
    }

    public static bool TryOverride(
        MyShipController controller,
        ref Vector3 move,
        ref Vector2 rotation,
        ref float roll)
    {
        if (!active || controller == null || controller.EntityId != controllerEntityId)
            return false;

        move = moveIndicator;
        rotation = Vector2.Zero;
        roll = 0f;
        return true;
    }
}
