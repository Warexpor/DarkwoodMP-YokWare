using DWMPHorde.Config;
using DWMPHorde.Logging;
using DWMPHorde.Networking;
using Steamworks;
using System.Collections.Generic;
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
        private static bool _hooked; // process-scoped: hook-installed flag
        private static bool _steam;
        private static ulong _steamLobbyId;
        private static Dictionary<string, int> _hostRoster;

        public static bool IsPending => _pending;
        public static bool WasHost => _wasHost;
        public static int Port => _port;
        public static string HostAddress => _hostAddress ?? "";
        /// <summary>True when the captured session ran over Steam (lobby resume, not LAN address/port).</summary>
        public static bool WasSteam => _steam;
        /// <summary>Steam host: stay in the lobby through the scene load so clients can rejoin its id.</summary>
        public static bool KeepSteamLobbyOnStop => _pending && _wasHost && _steam;

        public static void Reset()
        {
            _pending = false;
            _wasHost = false;
            _port = PluginInfo.DefaultPort;
            _hostAddress = "127.0.0.1";
            _steam = false;
            _steamLobbyId = 0;
            _hostRoster = null;
        }

        /// <summary>
        /// Call before StopNetwork during chapter transition or join offline load.
        /// A host must have a live link (nothing to rehost otherwise). A client whose transfer link
        /// already dropped (slot picker, failed link) still arms the phase-3 reconnect: it resumes
        /// from the configured connect address / port, or from the last Steam lobby it joined.
        /// </summary>
        public static void CaptureForResume(LanNetworkManager net)
        {
            if (net == null || (net.Role == NetworkRole.Host && !net.IsConnected))
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
            _port = ModConfig.GetConnectPort();
            _hostAddress = ModConfig.ConnectAddress != null
                ? (ModConfig.ConnectAddress.Value ?? "127.0.0.1").Trim()
                : "127.0.0.1";
            if (string.IsNullOrEmpty(_hostAddress))
                _hostAddress = "127.0.0.1";

            // A dropped client link has already lost its backend; the last Steam lobby id survives.
            ulong lastClientLobby = _wasHost ? 0UL : net.LastClientSteamLobbyId;
            _steam = net.IsSteamSession || lastClientLobby != 0;
            _steamLobbyId = 0;
            if (_steam)
            {
                // Steam has no address/port: resume through the lobby (host keeps it open, clients rejoin its id).
                ulong.TryParse(net.SteamLobbyIdText, out _steamLobbyId);
                if (_steamLobbyId == 0)
                    _steamLobbyId = lastClientLobby;
                if (_steamLobbyId == 0)
                {
                    ModLog.Error(LogCat.Session,
                        "[ChapterResume] Steam session has no lobby id — cannot resume after the chapter load");
                    _pending = false;
                    return;
                }
            }

            // Host: remember each client's PlayerId by stable key (peers disconnect before the
            // rehost) so ids stay the same across the chapter instead of arrival order.
            _hostRoster = _wasHost ? net.SnapshotStableKeyRoster() : null;
            _pending = true;

            ModLog.Event(LogCat.Session,
                $"[ChapterResume] captured wasHost={_wasHost} backend={(_steam ? "steam lobby=" + _steamLobbyId : "lan")} "
                + $"port={_port} addr={_hostAddress} roster={(_hostRoster != null ? _hostRoster.Count : 0)}");
        }

        public static void EnsureSceneHook()
        {
            if (_hooked) return;
            _hooked = true;
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            // Back at the title: whatever world is loaded next comes from a share or a save.
            if (string.Equals(scene.name, "Darkwood", System.StringComparison.OrdinalIgnoreCase))
                Patches.ClientChapterWorld.Reset();
            if (!_pending) return;
            // Back at the title ("Darkwood", Core.returnToMainMenu) instead of the chapter: the
            // transition was abandoned. Kept armed, the resume fired on a later, unrelated chapter
            // load (a single-player save) and reconnected or rehosted by itself.
            if (string.Equals(scene.name, "Darkwood", System.StringComparison.OrdinalIgnoreCase))
            {
                ModLog.Event(LogCat.Session, "[ChapterResume] returned to the title — resume dropped");
                Reset();
                return;
            }
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
                if (GameScreen.AtTitle)
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
                    if (_steam)
                    {
                        ModLog.Event(LogCat.Session, $"[ChapterResume] auto rehost via Steam (kept lobby {_steamLobbyId})");
                        net.StartHostSteam(_steamLobbyId);
                        net.StatusText = "Chapter rehost — waiting for peers in Steam lobby " + _steamLobbyId;
                    }
                    else
                    {
                        ModLog.Event(LogCat.Session, $"[ChapterResume] auto rehost on port {_port}");
                        net.StartHost(_port);
                        net.StatusText = "Chapter rehost — waiting for peers on " + _port;
                    }
                    net.ReservePlayerIdsForResume(_hostRoster);
                }
                else if (_steam)
                {
                    ModLog.Event(LogCat.Session,
                        $"[ChapterResume] auto reconnect via Steam lobby {_steamLobbyId} (playable={IsLocalPlayableForCoopReconnect()})");
                    net.ConnectSteamLobby(new CSteamID(_steamLobbyId));
                    net.StatusText = "Chapter reconnect to Steam lobby " + _steamLobbyId;
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
        private bool _loadFailed;

        public void Begin(bool wasHost)
        {
            _wasHost = wasHost;
            _started = true;
            _elapsed = 0f;
            _nextLogAt = LogEverySec;
            _loadFailed = false;
            Application.logMessageReceived += OnLog;
        }

        private void OnDestroy() => Application.logMessageReceived -= OnLog;

        /// <summary>
        /// The save load died: vanilla logs "ERROR WHEN LOADING DYNAMIC AND STATIC SAVE!" and the load
        /// coroutine then throws, leaving <c>Core.loadingGame</c> set for good.
        /// </summary>
        private void OnLog(string message, string stack, LogType type)
        {
            if (!Core.loadingGame)
                return;
            if ((type == LogType.Exception && stack != null && stack.Contains("SaveManager"))
                || (message != null && message.StartsWith("ERROR WHEN LOADING DYNAMIC AND STATIC SAVE", System.StringComparison.Ordinal)))
                _loadFailed = true;
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
                // A player new to this world plays its own prologue here, still offline; the
                // session resumes once it wakes in the hideout (PersonalPrologue).
                if (PersonalPrologue.FreshCharacter && !PersonalPrologue.JoinerActive && !PersonalPrologue.JoinerArrived)
                {
                    if (!PersonalPrologue.ReadyToBegin())
                        return;
                    if (PersonalPrologue.PlaysPrologue)
                    {
                        PersonalPrologue.BeginJoiner();
                        return;
                    }
                    PersonalPrologue.ArriveFresh();
                }
                if (PersonalPrologue.JoinerActive && !PersonalPrologue.TickJoiner())
                    return;

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
            // Only a load that died: a slow one (a busy machine, a Wine client presenting once a
            // second) was cut at a fixed 45 s while still loading, and the rest of the load ran
            // with the flag off (UniqueIDDict misses, the fresh character's home oven not found).
            if (_loadFailed && Core.loadingGame && Player.Instance != null && !GameScreen.AtTitle)
            {
                ModLog.Warn(LogCat.Session,
                    "[ChapterResume] save load failed (sav/savs) — clearing loadingGame");
                Core.loadingGame = false;
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
