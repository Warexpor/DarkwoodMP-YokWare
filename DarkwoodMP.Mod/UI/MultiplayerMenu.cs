using DWMPHorde.Config;
using DWMPHorde.Logging;
using DWMPHorde.Networking;
using UnityEngine;

namespace DWMPHorde
{
    public sealed class MultiplayerMenu : MonoBehaviour
    {
        private static MultiplayerMenu _instance;

        private bool _visible;
        private string _connectAddress = "127.0.0.1";
        private string _portText = PluginInfo.DefaultPort.ToString();
        private string _passwordText = "";
        private string _steamLobbyText = "";
        private Rect _windowRect;
        private Vector2 _scroll;
        private bool _windowRectInitialized;

        private LanNetworkManager Network => ModRuntime.Network;

        private static float UiScale => Mathf.Clamp(Screen.height / 900f, 1f, 2f);

        public static void ToggleVisible()
        {
            if (_instance == null) return;
            _instance._visible = !_instance._visible;
            if (_instance._visible)
                _instance.PullFieldsFromConfig();
        }

        /// <summary>Toggle IMGUI settings (IP/port/password) — main-menu SETTINGS open/close.</summary>
        public static void ShowSettings()
        {
            if (_instance == null) return;
            // Same button opens and closes (was open-only; second click did nothing).
            if (_instance._visible)
            {
                _instance.WriteFieldsToConfig();
                _instance._visible = false;
                return;
            }
            _instance._visible = true;
            _instance.PullFieldsFromConfig();
        }

        /// <summary>Write typed IMGUI fields into BepInEx config before host/join.</summary>
        public static void PushFieldsToConfig()
        {
            if (_instance == null) return;
            _instance.WriteFieldsToConfig();
        }

        public static void EnsureExists()
        {
            if (_instance != null)
                return;

            GameObject go = new GameObject("DWMPHorde_Menu");
            Object.DontDestroyOnLoad(go);
            _instance = go.AddComponent<MultiplayerMenu>();
            _instance.ResetWindowRect();
            _instance.PullFieldsFromConfig();
        }

        private void PullFieldsFromConfig()
        {
            if (ModConfig.ConnectAddress != null)
                _connectAddress = ModConfig.ConnectAddress.Value ?? "127.0.0.1";
            if (ModConfig.ConnectPort != null)
                _portText = ModConfig.ConnectPort.Value.ToString();
            if (ModConfig.HostPassword != null)
                _passwordText = ModConfig.HostPassword.Value ?? "";
            if (ModConfig.SteamLobbyId != null)
                _steamLobbyText = ModConfig.SteamLobbyId.Value ?? "";
            // Live host lobby id wins over stale config.
            if (Network != null && Network.IsSteamSession && !string.IsNullOrEmpty(Network.SteamLobbyIdText))
                _steamLobbyText = Network.SteamLobbyIdText;
        }

        private void WriteFieldsToConfig()
        {
            if (ModConfig.ConnectAddress != null && _connectAddress != null)
                ModConfig.ConnectAddress.Value = _connectAddress.Trim();
            if (ModConfig.ConnectPort != null && int.TryParse(_portText, out int p))
                ModConfig.ConnectPort.Value = p;
            if (ModConfig.HostPassword != null && _passwordText != null)
                ModConfig.HostPassword.Value = _passwordText;
            if (ModConfig.SteamLobbyId != null && _steamLobbyText != null)
                ModConfig.SteamLobbyId.Value = _steamLobbyText.Trim();
        }

        private void Update()
        {
            // Native MULTIPLAYER button inject (Yokyy product feature on Horde base)
            MainMenuMultiplayerInject.OnUpdate();
        }

        private void ResetWindowRect()
        {
            float scale = UiScale;
            float width = Mathf.Clamp(540f * scale, 440f, Screen.width * 0.65f);
            float height = Mathf.Clamp(580f * scale, 440f, Screen.height * 0.7f);
            _windowRect = new Rect(24f, 24f, width, height);
            _windowRectInitialized = true;
        }

        private void OnGUI()
        {
            if (!_windowRectInitialized)
                ResetWindowRect();

            if (!_visible)
                return;

            Matrix4x4 oldMatrix = GUI.matrix;
            float scale = UiScale;
            GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(scale, scale, 1f));

            Rect scaledRect = new Rect(
                _windowRect.x / scale,
                _windowRect.y / scale,
                _windowRect.width / scale,
                _windowRect.height / scale);

            scaledRect = GUI.Window(987654, scaledRect, DrawWindow, PluginInfo.Name + " v" + PluginInfo.DisplayVersion);

            _windowRect = new Rect(
                scaledRect.x * scale,
                scaledRect.y * scale,
                scaledRect.width * scale,
                scaledRect.height * scale);

            GUI.matrix = oldMatrix;
        }

        private void DrawWindow(int id)
        {
            float innerWidth = GUILayoutUtility.GetRect(1f, 1f, GUILayout.ExpandWidth(true)).width;
            if (innerWidth < 1f)
                innerWidth = _windowRect.width / UiScale - 28f;

            _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.ExpandHeight(true));

            GUILayout.Label("Co-op: LAN (LiteNetLib) or Steam (P2P lobby) — pick one per session", GUILayout.ExpandWidth(true));
            GUILayout.Label("Status: " + (Network != null ? Network.StatusText : "No network"), GUILayout.ExpandWidth(true));
            if (Network != null && Network.Role != NetworkRole.Offline)
            {
                int peers = Network.ConnectedPlayerCount;
                string backend = Network.IsSteamSession ? "Steam" : "LAN";
                // Host: peers = clients only. Total humans = peers + 1. Client: peers is usually 1 (host).
                string sessionLine = Network.Role == NetworkRole.Host
                    ? $"Session: Host/{backend} | LocalId={Network.LocalPlayerId} | clients={peers} | players≈{peers + 1} | handshake={Network.IsHandshakeComplete}"
                    : $"Session: Client/{backend} | LocalId={Network.LocalPlayerId} | connectedPeers={peers} | handshake={Network.IsHandshakeComplete}";
                GUILayout.Label(sessionLine, GUILayout.ExpandWidth(true));
                if (Network.IsSteamSession && Network.Role == NetworkRole.Host
                    && !string.IsNullOrEmpty(Network.SteamLobbyIdText))
                {
                    GUILayout.Label("Steam lobby id: " + Network.SteamLobbyIdText, GUILayout.ExpandWidth(true));
                    if (GUILayout.Button("Copy lobby id + open invite overlay", GUILayout.Height(28f)))
                    {
                        Networking.Steam.SteamCoopTransport.CopyToClipboard(Network.SteamLobbyIdText);
                        Network.InviteSteamFriends();
                    }
                }
            }
            GUILayout.Label(
                "JOIN flow: Host IN chapter → (1) world share → (2) pick permanent local profile → ENTER WORLD offline → (3) reconnect co-op.",
                GUILayout.ExpandWidth(true));
            GUILayout.Label(
                "Host from title then load: clients already connected get world push when host Player.Start runs. F2 Resend if stuck.",
                GUILayout.ExpandWidth(true));
            if (Network != null && Network.WorldSaveShare != null)
            {
                string prog = Network.WorldSaveShare.ProgressText;
                if (!string.IsNullOrEmpty(prog))
                    GUILayout.Label(prog, GUILayout.ExpandWidth(true));
            }
            string note = Networking.ClientSaveBridge.LastClientSaveNote;
            if (!string.IsNullOrEmpty(note))
                GUILayout.Label(note, GUILayout.ExpandWidth(true));

            GUILayout.Space(8f);
            GUILayout.Label("Host IP:", GUILayout.ExpandWidth(true));
            _connectAddress = GUILayout.TextField(_connectAddress, GUILayout.ExpandWidth(true));

            GUILayout.Space(4f);
            GUILayout.Label("Port:", GUILayout.ExpandWidth(true));
            _portText = GUILayout.TextField(_portText, GUILayout.ExpandWidth(true));

            GUILayout.Space(4f);
            GUILayout.Label("Password (optional, must match host config / Steam lobby key):", GUILayout.ExpandWidth(true));
            _passwordText = GUILayout.TextField(_passwordText ?? "", GUILayout.ExpandWidth(true));

            GUILayout.Space(4f);
            GUILayout.Label("Steam lobby id (for Steam join; host auto-fills):", GUILayout.ExpandWidth(true));
            _steamLobbyText = GUILayout.TextField(_steamLobbyText ?? "", GUILayout.ExpandWidth(true));

            GUILayout.Space(4f);
            GUILayout.Label("Chat name:", GUILayout.ExpandWidth(true));
            if (ModConfig.PlayerName != null)
                ModConfig.PlayerName.Value = GUILayout.TextField(ModConfig.PlayerName.Value ?? "Player", GUILayout.ExpandWidth(true));

            if (!int.TryParse(_portText, out int port))
                port = PluginInfo.DefaultPort;
            if (port < 1 || port > 65535)
                port = PluginInfo.DefaultPort;

            // Keep BepInEx config in sync with typed fields
            WriteFieldsToConfig();

            GUILayout.Space(10f);

            if (Network != null && Network.Role == NetworkRole.Offline)
            {
                if (GUILayout.Button("Host LAN game (port " + port + ")", GUILayout.Height(32f)))
                {
                    WriteFieldsToConfig();
                    Network.StartHost(port);
                }

                if (GUILayout.Button("Connect LAN", GUILayout.Height(32f)))
                {
                    WriteFieldsToConfig();
                    Network.ConnectToHost(_connectAddress.Trim(), port);
                }

                GUILayout.Space(6f);
                if (GUILayout.Button("Host Steam (friends lobby)", GUILayout.Height(32f)))
                {
                    WriteFieldsToConfig();
                    Network.StartHostSteam();
                }

                if (GUILayout.Button("Join Steam lobby", GUILayout.Height(32f)))
                {
                    WriteFieldsToConfig();
                    Network.ConnectSteam((_steamLobbyText ?? "").Trim());
                }
            }
            else if (Network != null)
            {
                if (Network.Role == NetworkRole.Host)
                {
                    bool shareBusy = Network.WorldSaveShare != null && Network.WorldSaveShare.IsBusy;
                    GUI.enabled = Network.IsConnected && Network.IsHandshakeComplete && !shareBusy;
                    if (GUILayout.Button(
                            shareBusy ? "Resending world…" : "Resend world to clients",
                            GUILayout.Height(32f)))
                    {
                        Network.WorldSaveShare?.ScheduleHostResend();
                    }
                    GUI.enabled = true;
                    GUILayout.Label(
                        "Resend: force-save host profile and push files to clients (same slot number).",
                        GUILayout.ExpandWidth(true));
                    GUILayout.Space(6f);
                }

                if (GUILayout.Button("Disconnect", GUILayout.Height(32f)))
                {
                    // Host: graceful handoff to lowest survivor when possible (n+ host grant).
                    if (Network.Role == NetworkRole.Host
                        && Network.TryGracefulHostLeave())
                    {
                        /* async handoff → StopNetwork */
                    }
                    else
                        Network.StopNetwork();
                }
            }

            GUILayout.Space(12f);
            GUILayout.Label("v" + PluginInfo.DisplayVersion + "  proto=" + PluginInfo.ProtocolVersion, GUILayout.ExpandWidth(true));
            GUILayout.Label("Config: BepInEx/config/" + PluginInfo.Guid + ".cfg  (restart after LogPreset change)", GUILayout.ExpandWidth(true));
            GUILayout.Label("LogPreset=" + ModLog.CurrentPreset + " (default full Trace; Public=quiet)", GUILayout.ExpandWidth(true));
            GUILayout.Label("Title: MULTIPLAYER  |  F2=this  F3=save  F4=spectate  F5=spawner", GUILayout.ExpandWidth(true));
            GUILayout.Label("Bugs: quit → send BOTH host+client BepInEx/LogOutput.log", GUILayout.ExpandWidth(true));

            GUILayout.EndScrollView();

            if (Event.current.type == EventType.Repaint)
            {
                float contentHeight = GUILayoutUtility.GetLastRect().yMax + 48f;
                float scaledHeight = contentHeight * UiScale;
                if (scaledHeight > _windowRect.height)
                    _windowRect.height = Mathf.Min(scaledHeight, Screen.height * 0.85f);
            }

            GUI.DragWindow(new Rect(0f, 0f, 10000f, 24f));
        }
    }
}
