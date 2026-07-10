using DWMPHorde.Networking;
using DWMPHorde.Sync;
using HarmonyLib;
using LiteNetLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// 4.5 Epilogue / credits:
    /// EpilogueOutcomes.goToCredits LoadScene("credits") must pull all peers.
    /// Multiplayer: coordinated SceneLoad (protocol 14) instead of solo LoadScene.
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
            // rebroadcasts via Forwardable — but also apply locally so originator is not stuck
            // if host is slow. ApplySceneLoad is idempotent via _sceneLoadPending.
            LanNetworkManager.ApplySceneLoad("credits", delaySeconds: 8f);

            // Credits ends co-op permanently (ChapterSessionPolicy) — no CaptureForResume.
            // Documented residual: post-credits is single-player epilogue, not a co-op chapter.
            ModRuntime.LegacyInfo($"[Epilogue] goToCredits multiplayer path role={net.Role} (network stops — no resume)");
            return false; // skip vanilla (would LoadScene alone at 10s)
        }
    }
}

namespace DWMPHorde.Networking
{
    public sealed partial class LanNetworkManager
    {
        private static bool _sceneLoadPending;

        private void HandleSceneLoad(SceneLoadMessage msg)
        {
            if (string.IsNullOrEmpty(msg.SceneName)) return;
            ApplySceneLoad(msg.SceneName, delaySeconds: 8f);
        }

        /// <summary>
        /// Coordinated scene load (credits). Fades peers out, stops network, loads scene.
        /// Idempotent — first call wins.
        /// </summary>
        internal static void ApplySceneLoad(string sceneName, float delaySeconds = 0.5f)
        {
            if (string.IsNullOrEmpty(sceneName)) return;
            if (_sceneLoadPending) return;
            _sceneLoadPending = true;

            ModRuntime.LegacyInfo($"[Epilogue] SceneLoad scheduled: {sceneName} delay={delaySeconds:F1}s");

            try
            {
                DreamSession.End("scene:" + sceneName);
            }
            catch { /* ignore */ }

            var ctrl = Singleton<Controller>.Instance;
            System.Action loadAction = () =>
            {
                try
                {
                    if (ModRuntime.Network != null && ModRuntime.Network.IsConnected)
                        ModRuntime.Network.StopNetwork();
                }
                catch { /* ignore */ }

                try
                {
                    Core.cantChangeForbidInputs = false;
                    Core.coreStarted = false;
                    Core.loadingGame = false;
                    Core.loadedGame = false;
                    IsApplyingRemoteState = true;
                    try
                    {
                        SceneManager.LoadScene(sceneName);
                    }
                    finally
                    {
                        IsApplyingRemoteState = false;
                    }
                }
                catch (System.Exception ex)
                {
                    ModRuntime.Log?.LogError("[Epilogue] SceneManager.LoadScene failed: " + ex);
                    _sceneLoadPending = false;
                }
            };

            if (ctrl != null && delaySeconds > 0.05f)
            {
                ctrl.Invoke(delegate { loadAction(); }, delaySeconds, timeScaleDependent: false);
            }
            else
            {
                loadAction();
            }
        }

        /// <summary>Reset pending flag on network stop so a new session can load scenes again.</summary>
        internal static void ResetSceneLoadState()
        {
            _sceneLoadPending = false;
        }
    }
}
