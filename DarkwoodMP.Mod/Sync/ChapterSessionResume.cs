using DWMPHorde.Config;
using DWMPHorde.Logging;
using DWMPHorde.Networking;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DWMPHorde.Sync
{
    /// <summary>
    /// After chapter / join LoadScene tears the transfer link, auto rehost / reconnect.
    /// Join pipeline phase 3: must wait until the local game is playable — sceneLoaded alone
    /// fires before SaveManager.Load finishes (see Player.log: reconnect then "Load game ver").
    /// Credits still end co-op permanently.
    /// </summary>
    public static class ChapterSessionResume
    {
        private static bool _pending;
        private static bool _wasHost;
        private static int _port;
        private static string _hostAddress;
        private static bool _hooked;

        public static bool IsPending => _pending;
        public static bool WasHost => _wasHost;
        public static int Port => _port;
        public static string HostAddress => _hostAddress ?? "";

        public static void Reset()
        {
            _pending = false;
            _wasHost = false;
            _port = PluginInfo.DefaultPort;
            _hostAddress = "127.0.0.1";
        }

        /// <summary>Call before StopNetwork during chapter transition or join offline load.</summary>
        public static void CaptureForResume(LanNetworkManager net)
        {
            if (net == null || !net.IsConnected)
            {
                _pending = false;
                return;
            }

            if (!ChapterSessionPolicy.ShouldAutoResumeNetworkAfterChapter)
            {
                _pending = false;
                return;
            }

            _wasHost = net.Role == NetworkRole.Host;
            _port = ModConfig.ConnectPort != null ? ModConfig.ConnectPort.Value : PluginInfo.DefaultPort;
            if (_port < 1 || _port > 65535)
                _port = PluginInfo.DefaultPort;
            _hostAddress = ModConfig.ConnectAddress != null
                ? (ModConfig.ConnectAddress.Value ?? "127.0.0.1").Trim()
                : "127.0.0.1";
            if (string.IsNullOrEmpty(_hostAddress))
                _hostAddress = "127.0.0.1";
            _pending = true;

            ModLog.Event(LogCat.Session,
                $"[ChapterResume] captured wasHost={_wasHost} port={_port} addr={_hostAddress}");
        }

        public static void EnsureSceneHook()
        {
            if (_hooked) return;
            _hooked = true;
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (!_pending) return;
            if (scene.name != null
                && scene.name.StartsWith("chapter", System.StringComparison.OrdinalIgnoreCase))
            {
                // Do NOT ConnectToHost here — save load runs AFTER sceneLoaded.
                var go = new GameObject("DWMP_ChapterResume");
                Object.DontDestroyOnLoad(go);
                go.AddComponent<ChapterResumeRunner>().Begin(_wasHost);
            }
        }

        /// <summary>
        /// Client is ready for phase-3 co-op: not title, not mid SaveManager.Load, player alive.
        /// </summary>
        public static bool IsLocalPlayableForCoopReconnect()
        {
            try
            {
                if (Core.mainMenu)
                    return false;
                if (Core.loadingGame)
                    return false;
                Player p = Player.Instance;
                if (p == null || p.gameObject == null || !p.gameObject.activeInHierarchy)
                    return false;
                // coreStarted can lag a frame after Activate player; Player is enough.
                return true;
            }
            catch
            {
                return false;
            }
        }

        internal static void ExecuteResume()
        {
            if (!_pending) return;
            _pending = false;

            ModRuntime.EnsureRunning();
            var net = ModRuntime.Network;
            if (net == null)
            {
                ModLog.Error(LogCat.Session, "[ChapterResume] no network manager");
                return;
            }

            try
            {
                if (_wasHost)
                {
                    ModLog.Event(LogCat.Session, $"[ChapterResume] auto rehost on port {_port}");
                    net.StartHost(_port);
                    net.StatusText = "Chapter rehost — waiting for peers on " + _port;
                }
                else
                {
                    ModLog.Event(LogCat.Session,
                        $"[ChapterResume] auto reconnect {_hostAddress}:{_port} (playable={IsLocalPlayableForCoopReconnect()})");
                    net.ConnectToHost(_hostAddress, _port);
                    net.StatusText = "Chapter reconnect to " + _hostAddress + ":" + _port;
                }
            }
            catch (System.Exception ex)
            {
                ModLog.Error(LogCat.Session, "[ChapterResume] failed", ex);
            }
        }
    }

    /// <summary>
    /// Waits until local world is playable (client) or a short bind delay (host), then resumes net.
    /// </summary>
    internal sealed class ChapterResumeRunner : MonoBehaviour
    {
        private const float HostMinDelaySec = 1.25f;
        private const float ClientMinDelaySec = 0.5f;
        private const float ClientMaxWaitSec = 180f;
        private const float LogEverySec = 5f;

        private bool _wasHost;
        private float _elapsed;
        private float _nextLogAt;
        private bool _started;
        private bool _loggedWaiting;

        public void Begin(bool wasHost)
        {
            _wasHost = wasHost;
            _started = true;
            _elapsed = 0f;
            _nextLogAt = LogEverySec;
        }

        private void Update()
        {
            if (!_started) return;
            _elapsed += Time.unscaledDeltaTime;

            if (_wasHost)
            {
                if (_elapsed < HostMinDelaySec)
                    return;
                Finish();
                return;
            }

            // Client join / chapter: sceneLoaded ≠ save finished.
            if (_elapsed < ClientMinDelaySec)
                return;

            if (ChapterSessionResume.IsLocalPlayableForCoopReconnect())
            {
                // Join offline load can leave a fat WorldGrid active set (Player.log ~500k objects).
                // Force a cull pass around the player before co-op traffic starts.
                try
                {
                    Player p = Player.Instance;
                    if (p != null && Singleton<WorldGrid>.Instance != null)
                    {
                        Vector3 pos = p._transform != null ? p._transform.position : p.transform.position;
                        Singleton<WorldGrid>.Instance.refreshPosition(pos, instant: true, force: true);
                    }
                }
                catch { /* optional */ }

                ModLog.Event(LogCat.Session,
                    "[ChapterResume] client playable after " + _elapsed.ToString("F1")
                    + "s — phase 3 co-op reconnect");
                Finish();
                return;
            }

            if (!_loggedWaiting)
            {
                _loggedWaiting = true;
                ModLog.Event(LogCat.Session,
                    "[ChapterResume] waiting for offline load to finish before phase 3 reconnect "
                    + "(loadingGame=" + Core.loadingGame
                    + " player=" + (Player.Instance != null)
                    + " mainMenu=" + Core.mainMenu + ")");
            }
            else if (_elapsed >= _nextLogAt)
            {
                _nextLogAt = _elapsed + LogEverySec;
                ModLog.Event(LogCat.Session,
                    "[ChapterResume] still waiting for playable… t=" + _elapsed.ToString("F0")
                    + "s loadingGame=" + Core.loadingGame
                    + " player=" + (Player.Instance != null)
                    + " mainMenu=" + Core.mainMenu);
            }

            // SaveManager.Load NRE leaves loadingGame=true forever (see Player.log
            // "ERROR WHEN LOADING DYNAMIC AND STATIC SAVE"). Unstick so phase-3 can run
            // or user can quit; world may still be broken — host must re-share consistent pair.
            if (_elapsed >= 45f && Core.loadingGame && Player.Instance != null && !Core.mainMenu)
            {
                ModLog.Warn(LogCat.Session,
                    "[ChapterResume] loadingGame stuck 45s after scene (likely failed sav/savs load) — clearing flag");
                try { Core.loadingGame = false; }
                catch { /* ignore */ }
            }

            if (_elapsed >= ClientMaxWaitSec)
            {
                ModLog.Warn(LogCat.Session,
                    "[ChapterResume] timeout " + ClientMaxWaitSec
                    + "s waiting for playable — reconnecting anyway");
                Finish();
            }
        }

        private void Finish()
        {
            _started = false;
            try
            {
                ChapterSessionResume.ExecuteResume();
            }
            finally
            {
                Destroy(gameObject);
            }
        }
    }
}
