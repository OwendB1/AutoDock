using Sandbox.Game.Entities;
using Sandbox.ModAPI.Ingame;
using VRageMath;

namespace ClientPlugin;

internal static class AutoDockControlOverride
{
    private static long controllerEntityId;
    private static Vector3 moveIndicator;
    private static Vector2 rotationIndicator;
    private static float rollIndicator;
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
            RestoreDampeners();
    }

    public static void Set(MyShipController controller, Vector3 move, Vector2 rotation, float roll, bool controlTranslation = true)
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
        rotationIndicator = rotation;
        rollIndicator = roll;
        active = true;
    }

    public static void Clear()
    {
        RestoreDampeners();
        controllerEntityId = 0L;
        moveIndicator = Vector3.Zero;
        rotationIndicator = Vector2.Zero;
        rollIndicator = 0f;
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
        rotation = rotationIndicator;
        roll = rollIndicator;
        return true;
    }
}
