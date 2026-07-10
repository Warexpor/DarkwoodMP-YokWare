using DWMPHorde.Config;
using DWMPHorde.Logging;
using DWMPHorde.Networking;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DWMPHorde.Sync
{
    /// <summary>
    /// Audit C3: after chapter LoadScene tears peers, auto rehost / reconnect
    /// instead of permanent silent solo. Credits still end co-op permanently.
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

        /// <summary>Call before StopNetwork during chapter transition.</summary>
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
                // Delay slightly so Core/Controller exist and listen port is free.
                var go = new GameObject("DWMP_ChapterResume");
                Object.DontDestroyOnLoad(go);
                go.AddComponent<ChapterResumeRunner>().Begin();
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
                        $"[ChapterResume] auto reconnect {_hostAddress}:{_port}");
                    // Host needs a moment to bind after their LoadScene.
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

    internal sealed class ChapterResumeRunner : MonoBehaviour
    {
        private float _delay = 1.25f;
        private bool _started;

        public void Begin()
        {
            _started = true;
        }

        private void Update()
        {
            if (!_started) return;
            _delay -= Time.unscaledDeltaTime;
            if (_delay > 0f) return;
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
