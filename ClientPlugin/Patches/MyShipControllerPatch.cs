using HarmonyLib;
using Sandbox.Game.Entities;
using VRageMath;

namespace ClientPlugin.Patches;

[HarmonyPatch(typeof(MyShipController), nameof(MyShipController.MoveAndRotate), typeof(Vector3), typeof(Vector2), typeof(float))]
internal static class MyShipControllerPatch
{
    [HarmonyPrefix]
    private static void MoveAndRotatePrefix(
        MyShipController __instance,
        ref Vector3 moveIndicator,
        ref Vector2 rotationIndicator,
        ref float rollIndicator)
    {
        AutoDockControlOverride.TryOverride(__instance, ref moveIndicator, ref rotationIndicator, ref rollIndicator);
    }
}
