using Sandbox.Game.Entities;
using VRageMath;

namespace ClientPlugin;

internal static class AutoDockControlOverride
{
    private static long controllerEntityId;
    private static Vector3 moveIndicator;
    private static Vector2 rotationIndicator;
    private static float rollIndicator;
    private static bool active;

    public static void BeginFrame()
    {
        active = false;
    }

    public static void Set(MyShipController controller, Vector3 move, Vector2 rotation, float roll)
    {
        if (controller == null)
        {
            Clear();
            return;
        }

        controllerEntityId = controller.EntityId;
        moveIndicator = move;
        rotationIndicator = rotation;
        rollIndicator = roll;
        active = true;
    }

    public static void Clear()
    {
        controllerEntityId = 0L;
        moveIndicator = Vector3.Zero;
        rotationIndicator = Vector2.Zero;
        rollIndicator = 0f;
        active = false;
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
