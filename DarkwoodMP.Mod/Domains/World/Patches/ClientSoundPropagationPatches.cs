using DWMPHorde.Harmony;
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
    public static class ClientFireWeaponSoundPatch
    {
        [HarmonyPriority(Priority.Last)]
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
            ModRuntime.Network?.Send(NetMessageType.PlayerSound, w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
        }
    }

    /// <summary>
    /// Client torso-clip step (window-jump landing, dodge): vanilla checkFrameTrigger alerts
    /// creatures within 150 (350 running) of the player, as for any step. The host raises the
    /// client's leg steps itself off the stand-in's legs (HandleProxyFootstep); a torso step has
    /// no legs there to come from, so the client sends it like its gunshots.
    /// </summary>
    [OptionalPatch]
    [HarmonyPatch(typeof(Player), nameof(Player.checkFrameTrigger))]
    public static class ClientTorsoStepAlertPatch
    {
        private static void Postfix(Player __instance, string eventInfo)
        {
            if (!PlayerTorsoFrameTriggerScope.Active)
                return;
            bool run = eventInfo == "FootHitGroundRun";
            if (!run && eventInfo != "FootHitGround")
                return;
            if (!NetGuard.Connected(out LanNetworkManager net) || net.Role != NetworkRole.Client)
                return;
            if (LanNetworkManager.IsApplyingRemoteState)
                return;
            // Vanilla: no alert while invisible, nor for a walking step while aiming.
            if (__instance == null || __instance != Player.Instance || __instance.invisible)
                return;
            if (!run && __instance.aiming)
                return;

            var msg = new PlayerSoundMessage
            {
                Range = run ? 350f : 150f,
                DangerousSound = false,
                Volume = 1f,
                Gunshot = false
            };
            net.Send(NetMessageType.PlayerSound, w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
        }
    }

    /// <summary>
    /// Client scary-face skill: vanilla makes every non-NPC character within 500 run away from the
    /// player, which on a client only touches its AI-less copies. The host runs it on the real ones.
    /// </summary>
    [HarmonyPatch(typeof(PlayerSkill), nameof(PlayerSkill.activate))]
    public static class ClientScaryFacePatch
    {
        private static void Prefix(PlayerSkill __instance, out int __state)
        {
            __state = __instance != null ? __instance.timesUsed : 0;
        }

        private static void Postfix(PlayerSkill __instance, int __state)
        {
            if (__instance == null || __instance.timesUsed == __state || __instance.gameObject.name != "scaryFace")
                return;
            if (!NetGuard.Connected(out LanNetworkManager net))
                return;
            if (net.Role == NetworkRole.Host)
            {
                // Vanilla already scared the host's characters; peers see the effect.
                var fx = new PlayerScareMessage { ScaryFace = true, CasterId = (short)net.LocalPlayerId };
                net.SendToAll(NetMessageType.PlayerScare, w => fx.Serialize(w), DeliveryMethod.ReliableOrdered);
                return;
            }
            var msg = new PlayerScareMessage { Range = 500f, ScaryFace = true };
            net.Send(NetMessageType.PlayerScare, w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
        }
    }

    /// <summary>Client aim-scare → host AI scareInArea (not a sonic SFX forward).</summary>
    [HarmonyPatch(typeof(Player), "aimScare")]
    public static class ClientAimScarePatch
    {
        // Vanilla loops aimScare every 1s while aimFinished stays true after aiming —
        // without a gate that is PlayerScare:2 every perf window forever.
        private static float _lastScareSendTime = -999f; // process-scoped: rate limiter on the monotonic game clock
        private const float ScareMinIntervalSec = 1.25f;

        private static void Postfix(Player __instance)
        {
            if (ModRuntime.Network == null || ModRuntime.Network.Role != NetworkRole.Client)
                return;
            if (!ModRuntime.Network.IsConnected)
                return;
            if (LanNetworkManager.IsApplyingRemoteState)
                return;
            if (__instance == null || !__instance.aiming || __instance.aimingReturn)
                return;
            if (!__instance.aimFinished)
                return;

            float now = Time.unscaledTime;
            if (now - _lastScareSendTime < ScareMinIntervalSec)
                return;
            _lastScareSendTime = now;

            var msg = new PlayerScareMessage { Range = 350f };
            ModRuntime.Network?.Send(NetMessageType.PlayerScare, w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
        }
    }
}
