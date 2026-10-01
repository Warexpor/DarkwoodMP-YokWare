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

            var net = ModRuntime.Network;
            if (net == null) return true;

            // Only peers already in the ending may start credits. A living player
            // still in the forest must not be pulled because someone else finished.
            if (!EpilogueNetHandlers.IsLocalInEpilogue())
            {
                ModRuntime.LegacyInfo(
                    "[Epilogue] goToCredits blocked — local peer not in epilogue");
                return false;
            }

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
                w => new SceneLoadMessage { SceneName = EpilogueNetHandlers.CreditsSceneName }.Serialize(w),
                DeliveryMethod.ReliableOrdered);

            // Host applies immediately (broadcast already out). Client applies after host
            // rebroadcasts via Forwardable, but also apply locally so the originator is not stuck
            // if host is slow. ApplySceneLoad is idempotent via _sceneLoadPending.
            EpilogueNetHandlers.ApplySceneLoad(EpilogueNetHandlers.CreditsSceneName, delaySeconds: 8f);

            // Credits ends co-op permanently under ChapterSessionPolicy; do not CaptureForResume.
            // Documented residual: post-credits is single-player epilogue, not a co-op chapter.
            ModRuntime.LegacyInfo($"[Epilogue] goToCredits multiplayer path role={net.Role} (network stops — no resume)");
            return false; // skip vanilla (would LoadScene alone at 10s)
        }
    }

    /// <summary>
    /// Burn-crawl death fires <c>epilogue_cameraPanOverBurningForest</c> on the dying body.
    /// Client one-shot GameEvents are blocked, so the client asks the host to fire it.
    /// Host Postfix then fans GameEventsFired out to every peer.
    /// </summary>
    [HarmonyPatch(typeof(Events), "fireWorldEvent")]
    public static class EpilogueClientCameraPanRelay
    {
        private static void Prefix(string type)
        {
            if (!string.Equals(type, EpilogueNetHandlers.EpilogueCameraPanEvent, System.StringComparison.Ordinal))
                return;
            if (LanNetworkManager.IsApplyingRemoteState || NetworkApplyGuard.IsActive)
                return;

            var net = ModRuntime.Network;
            if (net == null || !net.IsConnected || net.Role != NetworkRole.Client)
                return;
            if (Player.Instance == null || !Player.Instance.inEpilogue)
                return;

            var events = Singleton<Events>.Instance;
            if (events == null || events.worldEvents == null || !events.worldEvents.ContainsKey(type))
                return;

            GameEvents ge = events.worldEvents[type];
            Vector3 p = ge != null ? ge.transform.position : Vector3.zero;
            var msg = new GameEventsFiredMessage
            {
                PosX = p.x,
                PosY = p.y,
                PosZ = p.z,
                EventName = type
            };
            net.Send(NetMessageType.GameEventsFired,
                w => msg.Serialize(w),
                DeliveryMethod.ReliableOrdered);
            ModRuntime.LegacyInfo("[Epilogue] Client crawl pan — asking host to fire " + type);
        }
    }
}
