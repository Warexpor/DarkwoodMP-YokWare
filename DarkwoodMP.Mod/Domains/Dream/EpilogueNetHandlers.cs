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

        /// <summary>Host: who in the ending has reached the credits.</summary>
        internal static class EpilogueCredits
        {
            /// <summary>An idle reader does not hold the rest of the party forever.</summary>
            private const float MaxWaitSec = 120f;

            private static readonly System.Collections.Generic.HashSet<int> _ready = new System.Collections.Generic.HashSet<int>(); // reset-in: Reset
            private static float _firstReadyAt = -1f; // reset-in: Reset

            internal static void Reset()
            {
                _ready.Clear();
                _firstReadyAt = -1f;
            }

            internal static void MarkReady(int playerId)
            {
                if (playerId <= 0 || !_ready.Add(playerId))
                    return;
                if (_firstReadyAt < 0f)
                    _firstReadyAt = UnityEngine.Time.unscaledTime;
                ModRuntime.LegacyInfo($"[Epilogue] p{playerId} reached the credits ({_ready.Count} ready)");
            }

            internal static void Tick(LanNetworkManager net)
            {
                if (_firstReadyAt < 0f || _sceneLoadPending || net == null || net.Role != NetworkRole.Host)
                    return;
                bool all = !IsLocalInEpilogue() || _ready.Contains(net.LocalPlayerId);
                foreach (var proxy in net.GetAllProxies())
                {
                    if (proxy != null && proxy.PlayerId > 0 && proxy.RemoteInEpilogue && !_ready.Contains(proxy.PlayerId))
                        all = false;
                }
                bool timedOut = UnityEngine.Time.unscaledTime - _firstReadyAt > MaxWaitSec;
                if (!all && !timedOut)
                    return;
                ModRuntime.LegacyInfo("[Epilogue] everyone in the ending is done" + (all ? "" : " (waited too long)") + " — credits");
                net.Broadcast(NetMessageType.SceneLoad,
                    w => new SceneLoadMessage { SceneName = CreditsSceneName }.Serialize(w),
                    LiteNetLib.DeliveryMethod.ReliableOrdered);
                ApplySceneLoad(CreditsSceneName, delaySeconds: 8f);
                Reset();
            }
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
                        // A client finished its ending pages. Credits start when everyone in the
                        // ending has (EpilogueCredits), not on the fastest reader.
                        _net.SuppressRelay();
                        EpilogueCredits.MarkReady(_net.CurrentReceivePlayerId);
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
                    bool prevApply1 = LanNetworkManager.GetExplicitApplyingRemoteState();
                    LanNetworkManager.IsApplyingRemoteState = true;
                    try
                    {
                        SceneManager.LoadScene(sceneName);
                    }
                    finally
                    {
                        LanNetworkManager.SetExplicitApplyingRemoteState(prevApply1);
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
