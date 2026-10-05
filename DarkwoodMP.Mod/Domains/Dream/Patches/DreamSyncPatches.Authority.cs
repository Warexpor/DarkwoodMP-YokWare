using DWMPHorde.Networking;
using DWMPHorde.Sync;
using HarmonyLib;
using LiteNetLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
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
                var net = ModRuntime.Network;
                net?.Broadcast(NetMessageType.DreamChainStart,
                    w => new DreamChainStartMessage
                    {
                        NextPresetName = name,
                        SessionId = DreamSession.SessionId
                    }.Serialize(w),
                    DeliveryMethod.ReliableOrdered);
                ModRuntime.LegacyInfo($"[DreamSync] Host DreamChainStart → {name}");
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

            // Allow one vanilla initiateEndDreaming call for all-dead teardown.
            if (FinalDreamsceneManager.AllowDeathEndPass)
            {
                FinalDreamsceneManager.AllowDeathEndPass = false;
                ModRuntime.LegacyInfo("[DreamDeath] AllowDeathEndPass — vanilla initiateEndDreaming");
                return true;
            }

            // Death: never end the shared session alone; spectate until all are dead or the story ends.
            // The tutorial's scripted hit is its story ending and takes the story path below.
            if (outcome == "playerDeath" && !DreamSyncManager.IsScriptedDeathEnd(outcome))
            {
                if (Player.Instance != null && Player.Instance.inEpilogue)
                {
                    ModRuntime.LegacyInfo("[DreamDeath] Epilogue playerDeath — allowing vanilla end path");
                    return true;
                }

                if (!FinalDreamsceneManager.IsActive)
                    FinalDreamsceneManager.OnDreamStarted();

                // A solo dream uses vanilla death handling.
                if (!FinalDreamsceneManager.HasRemoteParticipants())
                {
                    ModRuntime.LegacyInfo(
                        "[DreamDeath] Solo/empty-peer dream death — allowing vanilla initiateEndDreaming");
                    return true;
                }

                ModRuntime.LegacyInfo("[DreamDeath] Player died in dream — redirecting to spectator");
                FinalDreamsceneManager.OnLocalDeathInDream();
                return false;
            }

            // Client story end: host owns teardown (including outcome transition).
            // Exception: host already ordered us to play the exit video locally.
            if (ModRuntime.Network.Role == NetworkRole.Client)
            {
                if (DreamSyncManager.IsHostOrderedDreamEnd)
                {
                    ModRuntime.LegacyInfo(
                        $"[DreamSession] Client host-ordered initiateEndDreaming '{outcome}'");
                    return true;
                }
                if (DreamSyncManager.IsStoryEndDeferPending)
                {
                    ModRuntime.LegacyInfo(
                        $"[DreamSession] Client story end '{outcome}' — defer already pending");
                    return false;
                }
                ModRuntime.LegacyInfo($"[DreamSession] Client story end '{outcome}' — deferring to host");
                var net = ModRuntime.Network;
                string preset = DreamSyncManager.ResolveActivePresetName();
                if (string.IsNullOrEmpty(preset) && __instance.preset != null)
                    preset = __instance.preset.name;
                net?.Send(NetMessageType.DreamEnded,
                    w => DreamEndedMessage.Build(preset ?? "", outcome).Serialize(w),
                    DeliveryMethod.ReliableOrdered);
                DreamSyncManager.BeginStoryEndDefer(preset ?? "", outcome);
                return false;
            }

            // Host story end: fan-out DreamEnded NOW so peers play the exit video in parallel,
            // then allow vanilla initiateEndDreaming → transition → endDreaming.
            string hostPreset = DreamSyncManager.ResolveActivePresetName();
            if (string.IsNullOrEmpty(hostPreset) && __instance.preset != null)
                hostPreset = __instance.preset.name;
            DreamSyncManager.NotifyPeersStoryEndBeginning(hostPreset, outcome);
            return true;
        }
    }

    /// <summary>
    /// Party-once skill dreams: before vanilla checks hadDreamAtLvl*, force local flags from
    /// the shared session so the second peer confirming skills does not re-fire bunker/random.
    /// </summary>
    [HarmonyPatch(typeof(SkillsMenu), "confirmSkills")]
    public static class SkillsMenuDreamPartyOncePatch
    {
        private static void Prefix()
        {
            if (ModRuntime.Network == null || !ModRuntime.Network.IsConnected)
                return;
            if (Dreams.Instance == null) return;

            // Union from DreamStarted/Ended/Bulk + bunker completion → all hadDreamAtLvl*.
            // Prior build only forced bunker→lvl2; random lvl 3/5/6/7 could re-fire when
            // Dreams.Instance flags lagged the session snapshot.
            DreamSession.ReassertLocalLvlFlags();
        }
    }
}
