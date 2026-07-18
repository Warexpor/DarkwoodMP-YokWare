using DWMPHorde.Networking;
using DWMPHorde.Sync;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Client: intercepts vanilla DreamTransition.onFinishedVideo to block the local
    /// prepareDream + startDreaming chain. Instead sends a DreamStartRequest to the
    /// host so the host becomes the sole authority for dream entry.
    ///
    /// The host's DreamStarted message then triggers ProcessRemoteDreamCoroutine,
    /// which loads the dream scene exactly once per session (no duplicate spawn).
    /// </summary>
    [HarmonyPatch(typeof(DreamTransition), "onFinishedVideo")]
    public static class DreamEntryClientPatch
    {
        private static bool Prefix(DreamTransition __instance)
        {
            // Allow vanilla for offline / remote-applied / host / already-dreaming
            if (ModRuntime.Network == null || !ModRuntime.Network.IsConnected)
                return true;
            if (LanNetworkManager.IsApplyingRemoteState)
                return true;

            var net = ModRuntime.Network as LanNetworkManager;
            if (net == null || net.Role == NetworkRole.Host)
                return true;

            // Already in a dream or loading a save — let vanilla chain/switching path handle it
            if (Singleton<Dreams>.Instance != null && Singleton<Dreams>.Instance.dreaming)
                return true;
            if (Core.loadingGame)
                return true;

            // Only intercept the first-play entry transition
            if (!__instance.isPlaying)
                return true;
            if (__instance.isCutsceneTransition || __instance.isChapter1Transition || __instance.isChapter2Transition)
                return true;

            // -- Client entry transition: intercept, send request to host --

            string dreamName = __instance.dreamToTransitionTo ?? "";

            // Mark not playing so re-entry is blocked (vanilla would do this inside the method)
            __instance.isPlaying = false;

            // Unpause game (vanilla onFinishedVideo would do this before prepareDream)
            Core.unpause();

            // Send DreamStartRequest so host (the sole authority) starts the dream
            var msg = new DreamStartRequestMessage
            {
                PresetName = dreamName,
                RequestId = (int)(Time.realtimeSinceStartup * 1000f),
                LvlFlags = DreamSession.ReadLocalLvlFlags()
            };
            net.Send(NetMessageType.DreamStartRequest,
                w => msg.Serialize(w),
                LiteNetLib.DeliveryMethod.ReliableOrdered);

            if (!string.IsNullOrEmpty(dreamName))
            {
                DreamSession.SetPendingHostPreset(dreamName);
                DreamSession.MirrorPoolRemove(dreamName);
            }

            DreamSyncManager.FreezeWorld();
            DreamSyncManager.MarkLocalEntryTransitionPlayed();

            ModRuntime.LegacyInfo(
                $"[DreamSync] Client intercepted entry transition — DreamStartRequest sent for '{dreamName}'");

            return false; // Skip original method body
        }
    }
}
