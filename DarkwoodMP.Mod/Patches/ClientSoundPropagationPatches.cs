using DWMPHorde.Networking;
using HarmonyLib;
using LiteNetLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// AI alert propagation only (not audible SFX sync). Client gunshots/scares
    /// notify the host so AI reacts via Character.alertInArea / scareInArea.
    /// Actual weapon audio is forwarded separately (PlayerAudio / fire FX paths).
    /// </summary>
    [HarmonyPatch(typeof(Player), "fireWeapon")]
    [HarmonyPriority(Priority.Last)]
    public static class ClientFireWeaponSoundPatch
    {
        private static void Postfix(Player __instance)
        {
            if (ModRuntime.Network == null || ModRuntime.Network.Role != NetworkRole.Client)
                return;
            if (!ModRuntime.Network.IsConnected)
                return;
            if (__instance.invisible)
                return;
            if (LanNetworkManager.IsApplyingRemoteState)
                return;

            InvItemClass item = __instance.currentItem;
            if (item == null || item.baseClass == null)
                return;

            var msg = new PlayerSoundMessage
            {
                Range = item.baseClass.attackSoundRange,
                DangerousSound = true,
                Volume = 1f,
                Gunshot = true
            };
            LanNetworkManager.Instance?.Send(NetMessageType.PlayerSound, w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
        }
    }

    /// <summary>Client aim-scare → host AI scareInArea (not a sonic SFX forward).</summary>
    [HarmonyPatch(typeof(Player), "aimScare")]
    public static class ClientAimScarePatch
    {
        private static void Postfix(Player __instance)
        {
            if (ModRuntime.Network == null || ModRuntime.Network.Role != NetworkRole.Client)
                return;
            if (!ModRuntime.Network.IsConnected)
                return;
            if (LanNetworkManager.IsApplyingRemoteState)
                return;

            var msg = new PlayerScareMessage { Range = 350f };
            LanNetworkManager.Instance?.Send(NetMessageType.PlayerScare, w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
        }
    }
}
