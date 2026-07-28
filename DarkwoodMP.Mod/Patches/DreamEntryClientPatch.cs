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

            // Opaque black BEFORE unpause — otherwise 1+ frames of overworld after last video frame.
            HoldEntryBlackAndStopAudio(__instance);
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

        /// <summary>
        /// Stop entry Audio (vanilla onLoaded) but keep opaque black + EnteringDream until
        /// remote dream load fades in — tearing down the video with no black left a
        /// multi-second overworld flash after the entry video.
        /// </summary>
        private static void HoldEntryBlackAndStopAudio(DreamTransition transition)
        {
            if (transition == null) return;
            try
            {
                if (transition.transitionObjects != null)
                {
                    for (int i = 0; i < transition.transitionObjects.Count; i++)
                    {
                        var obj = transition.transitionObjects[i];
                        if (obj == null) continue;
                        if (obj.type == DreamTransition.TransitionObject.Type.Audio
                            && !string.IsNullOrEmpty(obj.audioItemName))
                        {
                            float fade = obj.fadeOut >= 0f ? obj.fadeOut : 0.5f;
                            try { AudioController.Stop(obj.audioItemName, fade); }
                            catch { /* ignore */ }
                        }
                    }
                }

                Core.EnteringDream = true;
                // Snap both black layers opaque immediately (0.05s tween left a visible gap).
                var ui = Singleton<UI>.Instance;
                if (ui != null)
                {
                    try
                    {
                        if (ui.blackScreen != null)
                        {
                            var baseSprite = ui.blackScreen.GetComponent<tk2dBaseSprite>();
                            if (baseSprite != null)
                                baseSprite.color = new Color(0f, 0f, 0f, 1f);
                        }
                        if (ui.blackScreenTop != null)
                        {
                            var topSprite = ui.blackScreenTop.GetComponent<tk2dBaseSprite>();
                            if (topSprite != null)
                                topSprite.color = new Color(0f, 0f, 0f, 1f);
                        }
                    }
                    catch { /* fall through to tween */ }
                    ui.tweenBlackScreen(new Color(0f, 0f, 0f, 1f), 0f);
                    try { ui.tweenBlackScreenTop(new Color(0f, 0f, 0f, 1f), 0f); }
                    catch { /* older UI path */ }
                }

                // Hide finished video frame; blackScreen covers the gap to DreamStarted.
                if (Singleton<UI>.Instance != null && Singleton<UI>.Instance.videoOverlay != null)
                {
                    var overlay = Singleton<UI>.Instance.videoOverlay;
                    var renderer = overlay.GetComponent<Renderer>();
                    if (renderer != null)
                    {
                        var vp = renderer.GetComponent<UnityEngine.Video.VideoPlayer>();
                        if (vp != null && vp.isPlaying)
                            vp.Stop();
                        if (renderer.material != null)
                            renderer.material.color = new Color(1f, 1f, 1f, 0f);
                        renderer.enabled = false;
                    }
                    overlay.gameObject.SetActive(false);
                }
            }
            catch (System.Exception ex)
            {
                ModRuntime.Log?.LogWarning("[DreamSync] HoldEntryBlackAndStopAudio: " + ex.Message);
            }
        }
    }
}
