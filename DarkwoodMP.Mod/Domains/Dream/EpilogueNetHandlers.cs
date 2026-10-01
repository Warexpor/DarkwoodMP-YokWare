using DWMPHorde;
using DWMPHorde.Sync;
using UnityEngine.SceneManagement;

namespace DWMPHorde.Networking
{
    /// <summary>Epilogue / coordinated SceneLoad handlers composed for 0.8.</summary>
    internal sealed class EpilogueNetHandlers
    {
        private readonly LanNetworkManager _net;
        private static bool _sceneLoadPending;

        internal EpilogueNetHandlers(LanNetworkManager net)
        {
            _net = net ?? throw new System.ArgumentNullException(nameof(net));
        }

        /// <summary>Only coordinated scene a peer may pull the party into.</summary>
        internal const string CreditsSceneName = "credits";

        /// <summary>World event fired when the burn-crawl camera pan finishes.</summary>
        internal const string EpilogueCameraPanEvent = "epilogue_cameraPanOverBurningForest";

        /// <summary>
        /// True when this peer is actually in the ending (crawl / outcomes), not
        /// merely connected while someone else died in the forest.
        /// </summary>
        internal static bool IsLocalInEpilogue()
        {
            if (Player.Instance != null && Player.Instance.inEpilogue)
                return true;
            // Pad loaded but ApplyEpilogueMode not yet run (one frame), or mid-entry.
            if (Dreams.Instance != null && Dreams.Instance.dreaming
                && Dreams.Instance.dreamLocation != null
                && Dreams.Instance.dreamLocation.isEpilogueLocation)
                return true;
            string preset = DreamSession.PresetName ?? "";
            if (preset.IndexOf("epilog", System.StringComparison.OrdinalIgnoreCase) >= 0
                && DreamSession.IsActive)
                return true;
            return false;
        }

        internal void HandleSceneLoad(SceneLoadMessage msg)
        {
            if (string.IsNullOrEmpty(msg.SceneName)) return;

            int hostId = _net.HostPlayerId > 0 ? _net.HostPlayerId : 1;

            if (_net.Role == NetworkRole.Host)
            {
                if (_net.CurrentReceivePlayerId > 0)
                {
                    // Host goToCredits already applied locally and broadcast.
                    // A client who finishes outcomes first only Sends here.
                    // Apply on the host and let [Forwardable] reach the other peers.
                    if (string.Equals(msg.SceneName, CreditsSceneName, System.StringComparison.Ordinal))
                    {
                        if (!IsLocalInEpilogue())
                        {
                            ModRuntime.LegacyInfo(
                                "[Epilogue] Host ignored credits SceneLoad — not in epilogue "
                                + "(living peer must not be dragged by a remote ending)");
                            _net.SuppressRelay();
                            return;
                        }
                        ModRuntime.LegacyInfo(
                            $"[Epilogue] Client p{_net.CurrentReceivePlayerId} reached credits — pulling the party");
                        ApplySceneLoad(msg.SceneName, delaySeconds: 8f);
                        return;
                    }

                    _net.SuppressRelay();
                    ModRuntime.LegacyInfo(
                        $"[Epilogue] Rejected inbound SceneLoad from p{_net.CurrentReceivePlayerId}: {msg.SceneName}");
                }
                return;
            }

            // Client: only host may force coordinated scene load (credits / StopNetwork).
            if (_net.CurrentReceivePlayerId != hostId)
            {
                ModRuntime.LegacyInfo(
                    $"[Epilogue] Rejected SceneLoad from non-host p{_net.CurrentReceivePlayerId}: {msg.SceneName}");
                return;
            }

            if (string.Equals(msg.SceneName, CreditsSceneName, System.StringComparison.Ordinal)
                && !IsLocalInEpilogue())
            {
                ModRuntime.LegacyInfo(
                    "[Epilogue] Client ignored credits SceneLoad — not in epilogue");
                return;
            }

            ApplySceneLoad(msg.SceneName, delaySeconds: 8f);
        }

        /// <summary>
        /// Coordinated scene load (credits). Fades peers out, stops network, loads scene.
        /// Idempotent; the first call wins.
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
                    LanNetworkManager.IsApplyingRemoteState = true;
                    try
                    {
                        SceneManager.LoadScene(sceneName);
                    }
                    finally
                    {
                        LanNetworkManager.IsApplyingRemoteState = false;
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
