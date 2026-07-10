using DWMPHorde.Networking;
using DWMPHorde.Sync;
using HarmonyLib;
using UnityEngine;

/// <summary>
/// Traps the last entity hit by a weapon fire so its host-side ID can be included in
/// the PlayerAttackMessage sent by ClientDamageRedirectPatch.
/// </summary>
namespace DWMPHorde.Patches
{
    [HarmonyPriority(Priority.Last)]
    [HarmonyPatch(typeof(Player), "fireWeapon")]
    public static class WeaponFireSyncPatch
    {
        private static void Postfix(Player __instance)
        {
            var net = ModRuntime.Network as LanNetworkManager;
            if (net == null || net.Role == NetworkRole.Offline) return;
            if (TraverseHack.ApplyingFromNetwork) return;
            if (InvItemClass.isNull(__instance.currentItem) || __instance.currentItem.baseClass == null) return;
            if (!__instance.currentItem.baseClass.isFirearm) return;

            Vector3 pos = __instance.transform.position;
            net.SendPlayerFiredWeapon(new PlayerFiredWeaponMessage
            {
                ItemType = __instance.currentItem.type,
                AimY = __instance.transform.eulerAngles.y,
                PosX = pos.x,
                PosY = pos.y,
                PosZ = pos.z,
                ProjectileCount = __instance.currentItem.baseClass.projectileAmount
            });
        }
    }
}
