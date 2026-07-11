using DWMPHorde.Networking;
using DWMPHorde.Sync;
using HarmonyLib;
using LiteNetLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Host: TryBegin session as soon as prepareDream starts (closes double-prepare race).
    /// </summary>
    [HarmonyPatch(typeof(Dreams), "prepareDream")]
    public static class DreamPreparePatch
    {
        private static void Prefix(Dreams __instance, string presetName)
        {
            if (ModRuntime.Network == null || !ModRuntime.Network.IsConnected)
                return;
            if (LanNetworkManager.IsApplyingRemoteState)
                return;
            if (ModRuntime.Network.Role != NetworkRole.Host)
                return;

            string name = presetName;
            if (string.IsNullOrEmpty(name) && __instance.preset != null)
                name = __instance.preset.name;
            if (string.IsNullOrEmpty(name))
                return;

            // Host owns level flags when a skill dream is prepared.
            DreamSession.TryBegin(name);
            // Ensure host truth flags if this is a leveling dream trigger already set locally.
            // (Flags may already be true from SkillsMenu on host.)
        }
    }

    /// <summary>
    /// Prefix on Dreams.startDreaming: blocks completed dreams, routes client starts to host,
    /// and registers a shared DreamSession so all peers enter together.
    /// </summary>
    [HarmonyPatch(typeof(Dreams), "startDreaming")]
    public static class DreamStartPatch
    {
        private static bool Prefix(Dreams __instance)
        {
            if (__instance.preset == null || string.IsNullOrEmpty(__instance.preset.name))
                return true;

            string preset = __instance.preset.name;

            if (ModRuntime.Network != null && ModRuntime.Network.IsConnected)
            {
                if (DreamSession.IsPresetCompleted(preset)
                    && !DreamSession.IsActive)
                {
                    ModRuntime.LegacyInfo($"[DreamSync] Blocked re-entry of completed dream: {preset}");
                    return false;
                }

                if (LanNetworkManager.IsApplyingRemoteState)
                    return true; // remote/session-driven enter

                var net = ModRuntime.Network as LanNetworkManager;
                if (net != null && net.Role == NetworkRole.Client)
                {
                    ModRuntime.LegacyInfo($"[DreamSync] Client-initiated dream — requesting host to start: {preset}");
                    net.Send(NetMessageType.DreamStartRequest, w => new DreamStartRequestMessage
                    {
                        PresetName = preset,
                        RequestId = (int)(Time.realtimeSinceStartup * 1000f)
                    }.Serialize(w), DeliveryMethod.ReliableOrdered);
                    return false;
                }

                if (net != null && net.Role == NetworkRole.Host)
                {
                    // prepareDream already TryBegin; ensure session if host started without prepare patch path.
                    if (!DreamSession.IsActive && !DreamSession.TryBegin(preset))
                        return false;
                }
            }

            return true;
        }

        private static void Postfix(Dreams __instance)
        {
            if (ModRuntime.Network == null || !ModRuntime.Network.IsConnected)
                return;

            if (LanNetworkManager.IsApplyingRemoteState)
            {
                DreamSession.MarkActive();
                return;
            }

            if (__instance.preset == null || string.IsNullOrEmpty(__instance.preset.name))
                return;

            Vector3 locPos = Vector3.zero;
            if (__instance.dreamLocation != null)
                locPos = __instance.dreamLocation.transform.position;

            DreamSyncManager.OnLocalDreamStarted(__instance.preset.name, locPos);
            DreamSession.MarkActive();
        }
    }

    /// <summary>Prefix on endDreaming: ends shared session then notifies manager.</summary>
    [HarmonyPatch(typeof(Dreams), "endDreaming")]
    public static class DreamEndPatch
    {
        private static void Prefix(Dreams __instance)
        {
            if (ModRuntime.Network == null || !ModRuntime.Network.IsConnected)
                return;

            if (LanNetworkManager.IsApplyingRemoteState)
                return;

            if (!__instance.dreaming)
                return;

            // transferToDream sets switchingDream before end — chain instead of full end broadcast.
            if (__instance.switchingDream)
            {
                ModRuntime.LegacyInfo("[DreamSync] endDreaming with switchingDream — chain path owns session");
                return;
            }

            string outcome = __instance.outcome ?? "";
            if (DreamSession.IsActive)
                DreamSession.End(outcome);
            DreamSyncManager.OnLocalDreamEnded();
        }
    }

    /// <summary>
    /// Host: when prepareDream is called with switchingDream / chain, notify peers of next pocket.
    /// </summary>
    [HarmonyPatch(typeof(Dreams), "prepareDream")]
    public static class DreamPrepareChainPatch
    {
        private static void Prefix(Dreams __instance, string presetName)
        {
            if (ModRuntime.Network == null || !ModRuntime.Network.IsConnected)
                return;
            if (LanNetworkManager.IsApplyingRemoteState)
                return;
            if (ModRuntime.Network.Role != NetworkRole.Host)
                return;
            if (!__instance.switchingDream && !DreamSession.IsActive)
                return;

            string name = presetName;
            if (string.IsNullOrEmpty(name))
                return;

            // Only broadcast chain when already in a dream session and preparing a new pocket.
            if (!DreamSession.IsActive || string.IsNullOrEmpty(DreamSession.PresetName))
                return;
            if (string.Equals(DreamSession.PresetName, name, System.StringComparison.OrdinalIgnoreCase)
                && DreamSession.IsStarting)
                return;

            if (__instance.switchingDream || DreamSession.IsActive)
            {
                DreamSession.SetChainedPreset(name);
                var net = LanNetworkManager.Instance;
                net?.Broadcast(NetMessageType.DreamChainStart,
                    w => new DreamChainStartMessage
                    {
                        NextPresetName = name,
                        SessionId = DreamSession.SessionId
                    }.Serialize(w),
                    DeliveryMethod.ReliableOrdered);
                ModRuntime.LegacyInfo("[DreamSync] Host DreamChainStart → " + name);
            }
        }
    }

    /// <summary>
    /// Single Prefix for Dreams.initiateEndDreaming (merged death + client-authority logic).
    /// Branch order: offline → applying remote → not in session → death spectate →
    /// client story defer to host → host/vanilla continues.
    /// </summary>
    [HarmonyPatch(typeof(Dreams), "initiateEndDreaming")]
    public static class DreamEndDreamingAuthorityPatch
    {
        private static bool Prefix(Dreams __instance)
        {
            if (ModRuntime.Network == null || !ModRuntime.Network.IsConnected)
                return true;

            if (LanNetworkManager.IsApplyingRemoteState)
                return true;

            if (!DreamSession.IsActive && !DreamSyncManager.IsDreamActive)
                return true;

            string outcome = __instance.outcome ?? "";

            // Death: never end the shared session alone — spectate until all dead / story end.
            if (outcome == "playerDeath")
            {
                if (Player.Instance != null && Player.Instance.inEpilogue)
                {
                    ModRuntime.LegacyInfo("[DreamDeath] Epilogue playerDeath — allowing vanilla end path");
                    return true;
                }
                ModRuntime.LegacyInfo("[DreamDeath] Player died in dream — redirecting to spectator");
                if (!FinalDreamsceneManager.IsActive)
                    FinalDreamsceneManager.OnDreamStarted();
                FinalDreamsceneManager.OnLocalDeathInDream();
                return false;
            }

            // Client story end: host owns teardown (including outcome transition).
            if (ModRuntime.Network.Role == NetworkRole.Client)
            {
                ModRuntime.LegacyInfo($"[DreamSession] Client story end '{outcome}' — deferring to host");
                var net = ModRuntime.Network as LanNetworkManager;
                net?.Send(NetMessageType.DreamEnded,
                    w => DreamEndedMessage.Build(
                        __instance.preset != null ? __instance.preset.name : "",
                        outcome).Serialize(w),
                    DeliveryMethod.ReliableOrdered);
                return false;
            }

            // Host story end: allow vanilla initiateEndDreaming → transition → endDreaming
            return true;
        }
    }
}
