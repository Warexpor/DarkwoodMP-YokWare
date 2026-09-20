using DWMPHorde.Networking;
using DWMPHorde.Sync;
using HarmonyLib;
using LiteNetLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// EpilogueOutcomes.goToCredits must pull all peers into the credits scene.
    /// Multiplayer uses a coordinated SceneLoad instead of a solo LoadScene.
    /// </summary>
    [HarmonyPatch(typeof(EpilogueOutcomes), "goToCredits")]
    public static class EpilogueGoToCreditsPatch
    {
        private static bool Prefix(EpilogueOutcomes __instance)
        {
            if (ModRuntime.Network == null || !ModRuntime.Network.IsConnected)
                return true;
            if (LanNetworkManager.IsApplyingRemoteState)
                return true;

            var net = ModRuntime.Network as LanNetworkManager;
            if (net == null) return true;

            // Mirror vanilla fade/forbid before shared load.
            try
            {
                if (Singleton<Controller>.Instance != null)
                    Singleton<Controller>.Instance.fadeAudio(fadeOut: true, 10f, musicToo: true);
                Core.hideGameCursor();
                if (Singleton<UI>.Instance != null)
                    Singleton<UI>.Instance.tweenBlackScreenTop(new Color(0f, 0f, 0f, 1f), 8f);
                Core.forbidInputs = true;
                Core.coreStarted = false;
                Core.loadingGame = false;
                Core.loadedGame = false;
            }
            catch (System.Exception ex)
            {
                ModRuntime.Log?.LogWarning("[Epilogue] goToCredits pre-fade: " + ex.Message);
            }

            net.Broadcast(NetMessageType.SceneLoad,
                w => new SceneLoadMessage { SceneName = "credits" }.Serialize(w),
                DeliveryMethod.ReliableOrdered);

            // Host applies immediately (broadcast already out). Client applies after host
            // rebroadcasts via Forwardable, but also apply locally so the originator is not stuck
            // if host is slow. ApplySceneLoad is idempotent via _sceneLoadPending.
            LanNetworkManager.ApplySceneLoad("credits", delaySeconds: 8f);

            // Credits ends co-op permanently under ChapterSessionPolicy; do not CaptureForResume.
            // Documented residual: post-credits is single-player epilogue, not a co-op chapter.
            ModRuntime.LegacyInfo($"[Epilogue] goToCredits multiplayer path role={net.Role} (network stops — no resume)");
            return false; // skip vanilla (would LoadScene alone at 10s)
        }
    }
}
