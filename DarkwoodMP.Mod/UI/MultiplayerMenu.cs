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
        private bool _advancedOpen;
        private string _connectAddress = "127.0.0.1";
        private string _portText = PluginInfo.DefaultPort.ToString();
        private string _passwordText = "";
        private string _steamLobbyText = "";
        private string _hostNextStepHint = "";
        private string _restoreSelfStatus = "";
        private float _restoreSelfStatusUntil;
        private ClientStateBackupData _peekBackup;
        private float _peekBackupAt;
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
            if (_instance._visible)
            {
                _instance.WriteFieldsToConfig();
                _instance._visible = false;
                return;
            }
            _instance._visible = true;
            _instance.PullFieldsFromConfig();
        }

        public static void PushFieldsToConfig()
        {
            if (_instance == null) return;
            _instance.WriteFieldsToConfig();
        }

        public static void SetHostNextStepHint(string hint)
        {
            if (_instance == null)
                EnsureExists();
            if (_instance != null)
                _instance._hostNextStepHint = hint ?? "";
        }

        public static void ClearHostNextStepHint()
        {
            if (_instance != null)
                _instance._hostNextStepHint = "";
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
            MainMenuMultiplayerInject.OnUpdate();
        }

        private void ResetWindowRect()
        {
            float scale = UiScale;
            float width = Mathf.Clamp(480f * scale, 400f, Screen.width * 0.55f);
            float height = Mathf.Clamp(420f * scale, 340f, Screen.height * 0.55f);
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
            float scaleGui = UiScale;
            GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(scaleGui, scaleGui, 1f));

            Rect scaledRect = new Rect(
                _windowRect.x / scaleGui,
                _windowRect.y / scaleGui,
                _windowRect.width / scaleGui,
                _windowRect.height / scaleGui);

            scaledRect = GUI.Window(987654, scaledRect, DrawWindow, PluginInfo.Name + " v" + PluginInfo.DisplayVersion);

            _windowRect = new Rect(
                scaledRect.x * scaleGui,
                scaledRect.y * scaleGui,
                scaledRect.width * scaleGui,
                scaledRect.height * scaleGui);

            GUI.matrix = oldMatrix;
        }

        private void DrawWindow(int id)
        {
            _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.ExpandHeight(true));

            GUILayout.Label("Status: " + (Network != null ? Network.StatusText : "No network"), GUILayout.ExpandWidth(true));
            if (Network != null && Network.WorldSaveShare != null
                && !string.IsNullOrEmpty(Network.WorldSaveShare.ProgressText))
                GUILayout.Label(Network.WorldSaveShare.ProgressText, GUILayout.ExpandWidth(true));
            GUILayout.Label(
                "Join: host must be in-chapter → world share → pick slot → ENTER WORLD.",
                GUILayout.ExpandWidth(true));

            GUILayout.Space(8f);
            GUILayout.Label("Host IP:", GUILayout.ExpandWidth(true));
            _connectAddress = GUILayout.TextField(_connectAddress, GUILayout.ExpandWidth(true));

            GUILayout.Space(4f);
            GUILayout.Label("Port:", GUILayout.ExpandWidth(true));
            _portText = GUILayout.TextField(_portText, GUILayout.ExpandWidth(true));

            GUILayout.Space(4f);
            GUILayout.Label("Password (optional):", GUILayout.ExpandWidth(true));
            _passwordText = GUILayout.TextField(_passwordText ?? "", GUILayout.ExpandWidth(true));

            GUILayout.Space(4f);
            GUILayout.Label("Steam lobby id:", GUILayout.ExpandWidth(true));
            _steamLobbyText = GUILayout.TextField(_steamLobbyText ?? "", GUILayout.ExpandWidth(true));

            GUILayout.Space(4f);
            GUILayout.Label("Chat name:", GUILayout.ExpandWidth(true));
            if (ModConfig.PlayerName != null)
                ModConfig.PlayerName.Value = GUILayout.TextField(ModConfig.PlayerName.Value ?? "Player", GUILayout.ExpandWidth(true));

            WriteFieldsToConfig();

            GUILayout.Space(10f);
            if (GUILayout.Button(_advancedOpen ? "Advanced ▾" : "Advanced ▸", GUILayout.Height(26f)))
                _advancedOpen = !_advancedOpen;

            if (_advancedOpen)
            {
                GUILayout.Space(4f);
                if (Network != null && Network.IsSteamSession && Network.Role == NetworkRole.Host
                    && !string.IsNullOrEmpty(Network.SteamLobbyIdText))
                {
                    GUILayout.Label("Lobby: " + Network.SteamLobbyIdText, GUILayout.ExpandWidth(true));
                    if (GUILayout.Button("Copy lobby id + open invite", GUILayout.Height(28f)))
                    {
                        Networking.Steam.SteamCoopTransport.CopyToClipboard(Network.SteamLobbyIdText);
                        Network.InviteSteamFriends();
                    }
                }

                if (Network != null && Network.Role == NetworkRole.Host)
                {
                    bool shareBusy = Network.WorldSaveShare != null && Network.WorldSaveShare.IsBusy;
                    GUI.enabled = Network.IsConnected && Network.IsHandshakeComplete && !shareBusy;
                    if (GUILayout.Button(shareBusy ? "Resending world…" : "Resend world to clients", GUILayout.Height(28f)))
                        Network.WorldSaveShare?.ScheduleHostResend();
                    GUI.enabled = true;
                }

                if (Network != null && Network.Role != NetworkRole.Offline)
                {
                    if (GUILayout.Button("Disconnect", GUILayout.Height(28f)))
                    {
                        ClearHostNextStepHint();
                        if (Network.Role == NetworkRole.Host && Network.TryGracefulHostLeave())
                        { /* handoff */ }
                        else
                            Network.StopNetwork();
                    }
                }

                DrawRestoreSelfSection();
            }

            GUILayout.Space(10f);
            GUILayout.Label(
                "v" + PluginInfo.DisplayVersion + "  proto=" + PluginInfo.ProtocolVersion
                + "  |  F2=settings F3=save  |  " + PluginInfo.Guid + ".cfg",
                GUILayout.ExpandWidth(true));

            GUILayout.EndScrollView();
            GUI.DragWindow(new Rect(0f, 0f, 10000f, 24f));
        }

        /// <summary>
        /// Client personal inv/skills/pos live in ClientStateBackup (host world share
        /// loads the host body). Auto-restore usually runs on join/load; this is the
        /// manual recovery path when that misses.
        /// </summary>
        private void DrawRestoreSelfSection()
        {
            GUILayout.Space(6f);
            GUILayout.Label("Client self-backup (inv / skills / exit pos):", GUILayout.ExpandWidth(true));

            bool canRestore = TryGetRestoreSelfGate(out string reason);
            var peek = PeekLocalSelfBackup();
            if (peek != null)
            {
                GUILayout.Label(
                    "On disk: day≈" + peek.Day
                    + " lvl=" + peek.CurrentLevel
                    + " inv=" + (peek.InventoryItems?.Count ?? 0)
                    + " skills=" + (peek.Skills?.Count ?? 0)
                    + (string.IsNullOrEmpty(peek.Timestamp) ? "" : " @ " + peek.Timestamp),
                    GUILayout.ExpandWidth(true));
            }
            else
            {
                GUILayout.Label("On disk: none for this campaign.", GUILayout.ExpandWidth(true));
            }

            GUI.enabled = canRestore;
            if (GUILayout.Button("Restore self now", GUILayout.Height(28f)))
                TryRestoreSelf();
            GUI.enabled = true;

            if (!canRestore && !string.IsNullOrEmpty(reason))
                GUILayout.Label(reason, GUILayout.ExpandWidth(true));

            if (!string.IsNullOrEmpty(_restoreSelfStatus) && Time.realtimeSinceStartup < _restoreSelfStatusUntil)
            {
                GUI.color = Color.yellow;
                GUILayout.Label(_restoreSelfStatus, GUILayout.ExpandWidth(true));
                GUI.color = Color.white;
            }
        }

        private bool TryGetRestoreSelfGate(out string reason)
        {
            reason = null;
            if (Core.mainMenu || Core.loadingGame || Player.Instance == null)
            {
                reason = "Need to be in-chapter (not title). Auto-restore runs on join.";
                return false;
            }

            var net = ModRuntime.Network as LanNetworkManager;
            if (net != null && net.Role == NetworkRole.Host)
            {
                reason = "Host uses sav.dat — restore self is client-only (would overwrite host).";
                return false;
            }

            var data = PeekLocalSelfBackup();
            if (data == null)
            {
                reason = "No usable self-backup for this campaign yet (save / disconnect as client first).";
                return false;
            }

            return true;
        }

        private ClientStateBackupData PeekLocalSelfBackup()
        {
            // OnGUI can fire many times/frame — don't re-read + log-spam every paint.
            if (Time.realtimeSinceStartup - _peekBackupAt < 1.0f)
                return _peekBackup;
            _peekBackupAt = Time.realtimeSinceStartup;
            _peekBackup = ClientStateBackup.LoadLocalSelfBackupFile();
            return _peekBackup;
        }

        private void SetRestoreSelfStatus(string msg)
        {
            _restoreSelfStatus = msg ?? "";
            _restoreSelfStatusUntil = Time.realtimeSinceStartup + 6f;
        }

        private void TryRestoreSelf()
        {
            if (!TryGetRestoreSelfGate(out string reason))
            {
                SetRestoreSelfStatus(reason ?? "Restore blocked.");
                ModLog.Event(LogCat.Save, "RESTORE SELF blocked: " + (reason ?? "?"));
                return;
            }

            // Fresh read on click (bypass 1s peek cache).
            _peekBackupAt = 0f;
            var data = ClientStateBackup.LoadLocalSelfBackupFile();
            if (data == null)
            {
                SetRestoreSelfStatus("No local self-backup found.");
                ModLog.Event(LogCat.Save, "No local self-backup found.");
                return;
            }

            // Snapshot before so the log shows we actually ran.
            float beforeHp = Player.Instance.health;
            int beforeLvl = Player.Instance.currentLevel;

            ClientStateBackup.RestoreFromBackup(data);

            // Campaign/empty guards inside RestoreFromBackup log + no-op.
            if (!ClientStateBackup.MatchesCurrentCampaign(data)
                || !ClientStateBackup.HasMeaningfulProgress(data))
            {
                SetRestoreSelfStatus("Restore refused (campaign mismatch or empty backup). See log.");
                return;
            }

            SetRestoreSelfStatus(
                "Restored self — lvl=" + data.CurrentLevel
                + " inv=" + (data.InventoryItems?.Count ?? 0)
                + " skills=" + (data.Skills?.Count ?? 0)
                + " pos=(" + data.PosX.ToString("F0") + "," + data.PosZ.ToString("F0") + ")");
            ModLog.Event(LogCat.Save,
                "Applied local self-backup (before lvl=" + beforeLvl
                + " hp=" + beforeHp.ToString("F0") + ").");
        }
    }
}
