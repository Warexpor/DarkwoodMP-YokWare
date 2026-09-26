using DWMPHorde.Networking;
using DWMPHorde.Sync;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    [HarmonyPriority(Priority.Last)]
    [HarmonyPatch(typeof(Bullet), "onCollide", typeof(Collider), typeof(Vector3))]
    public static class ClientProjectileDamagePatch
    {
        private static void Prefix(Bullet __instance)
        {
            if (__instance.objectThatSpawnedMe != null) return;
            if (ModRuntime.Network is LanNetworkManager net && net.Role == NetworkRole.Client)
                TraverseHack.IsInsidePlayerBulletCollision = true;
        }

        private static void Postfix()
        {
            TraverseHack.IsInsidePlayerBulletCollision = false;
        }

        // onCollide can throw; stuck true redirects peer hitscan/projectile damage forever.
        private static void Finalizer()
        {
            TraverseHack.IsInsidePlayerBulletCollision = false;
        }
    }
}
